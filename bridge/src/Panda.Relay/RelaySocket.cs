using System.Buffers;
using System.Net.WebSockets;
using System.Text;

namespace Panda.Relay;

/// <summary>What one receive gave: a whole message, the peer's close, an oversized message, or a dead connection.</summary>
internal enum FrameKind
{
    Text,
    Binary,
    Close,
    TooLarge,
    Lost,
}

/// <summary>
/// One received message. <see cref="Bytes"/> is lent by its socket: valid only until the next
/// <see cref="RelaySocket.ReceiveAsync"/> / <see cref="RelaySocket.DrainAsync"/> / <see cref="RelaySocket.Dispose"/> on
/// that socket (plan-zero-alloc-relay seam S1). Empty for Close, TooLarge and Lost.
/// </summary>
internal readonly record struct Frame(FrameKind Kind, ReadOnlyMemory<byte> Bytes)
{
    public static readonly Frame Close = new(FrameKind.Close, default);
    public static readonly Frame TooLarge = new(FrameKind.TooLarge, default);
    public static readonly Frame Lost = new(FrameKind.Lost, default);
}

/// <summary>
/// One message being received (D2): the buffer read into, the pooled array among them that only this message holds until
/// its frame is published, and the bytes so far. A local of the receiving loop, never shared.
/// </summary>
internal struct Inbound(byte[] buffer)
{
    public byte[] Buffer = buffer;
    public byte[]? Rented;
    public int Count;
}

/// <summary>
/// One accepted WebSocket (control, dialer or acceptor). One receiver (the socket's own handler) and any number of
/// senders: sends are serialized, each bounded by <see cref="RelayOptions.SendTimeout"/> (a peer that stops reading
/// is aborted). <see cref="CloseAsync"/> is idempotent — the first code wins — and bounded: the peer's close frame is
/// awaited by the receiver for <see cref="RelayOptions.CloseTimeout"/>, then the socket is aborted.
/// </summary>
internal sealed class RelaySocket : IDisposable
{
    private const int FirstBuffer = 16 * 1024;

    private readonly WebSocket _ws;
    private readonly RelayOptions _options;
    private readonly TimeProvider _time;
    private readonly ArrayPool<byte> _pool;
    private readonly byte[] _buffer;
    private readonly SemaphoreSlim _send = new(1, 1);
    private const long NoSend = 0;
    private const long Aborted = long.MinValue; // never a timestamp

    private readonly ITimer _sendTimer;
    private ITimer? _abortTimer;
    private long _sendStart; // the send in flight's start (_time timestamp), NoSend, or Aborted — D3
    private byte[]? _rented; // lent by the last frame; taken with Interlocked.Exchange by the next receive or Dispose
    private int _closing;
    private int _disposed;

    public RelaySocket(WebSocket ws, RelayOptions options, TimeProvider time)
        : this(ws, options, time, ArrayPool<byte>.Shared)
    {
    }

    internal RelaySocket(WebSocket ws, RelayOptions options, TimeProvider time, ArrayPool<byte> pool)
    {
        _ws = ws;
        _options = options;
        _time = time;
        _pool = pool;
        _buffer = new byte[Math.Min(FirstBuffer, options.MaxFrameBytes + 1)];
        _sendTimer = time.CreateTimer(static s => ((RelaySocket)s!).OnSendTimer(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public bool IsClosing => Volatile.Read(ref _closing) != 0;

    /// <summary>The close code this socket was closed with by the relay (0 while open).</summary>
    public int CloseCode { get; private set; }

    /// <summary>
    /// One whole message, at most <see cref="RelayOptions.MaxFrameBytes"/>, in this socket's own buffer or, when larger,
    /// in an array rented from the pool — lent to the returned frame (see <see cref="Frame"/>). Ends the previous
    /// frame's lease. Never throws for the peer's faults. Built from the receive steps below; the pump inlines the same
    /// steps so its one long-lived state machine does the awaits (D4).
    /// </summary>
    public async Task<Frame> ReceiveAsync()
    {
        var message = BeginReceive();
        try
        {
            while (true)
            {
                var read = ReadAsync(ref message);
                if (Received(ref message, await read) is { } frame)
                {
                    return frame;
                }
            }
        }
        catch (Exception e) when (IsConnectionGone(e))
        {
            return Frame.Lost;
        }
        finally
        {
            Abandon(ref message);
        }
    }

    /// <summary>
    /// A peer fault or the end of the connection — a reset, an abort (ours, our send timer's or the keepalive's), or a
    /// dead TCP peer; never a programmer error.
    /// </summary>
    internal static bool IsConnectionGone(Exception e) =>
        e is WebSocketException or OperationCanceledException or ObjectDisposedException;

    /// <summary>Receive step 1: ends the previous frame's lease; the message starts in the socket's own buffer.</summary>
    internal Inbound BeginReceive()
    {
        ReturnRented();
        return new Inbound(_buffer);
    }

    /// <summary>
    /// Receive step 2: the next framework read into the message — when the buffer is full, into a larger pooled array
    /// (the superseded one returned); sliced to MaxFrameBytes + 1, as a pooled array may be larger than asked for (F7).
    /// </summary>
    internal ValueTask<ValueWebSocketReceiveResult> ReadAsync(ref Inbound message)
    {
        var limit = _options.MaxFrameBytes + 1;
        if (message.Count == message.Buffer.Length)
        {
            var bigger = _pool.Rent(Math.Min(message.Buffer.Length * 2, limit));
            message.Buffer.AsSpan(0, message.Count).CopyTo(bigger);
            if (message.Rented is { } superseded)
            {
                _pool.Return(superseded, clearArray: true);
            }

            message.Rented = message.Buffer = bigger;
        }

        return _ws.ReceiveAsync(message.Buffer.AsMemory(message.Count, Math.Min(message.Buffer.Length, limit) - message.Count), CancellationToken.None);
    }

    /// <summary>Receive step 3: folds one read in. Null = read more; else the message is over and this is its frame.</summary>
    internal Frame? Received(ref Inbound message, ValueWebSocketReceiveResult result)
    {
        if (result.MessageType == WebSocketMessageType.Close)
        {
            return Frame.Close;
        }

        message.Count += result.Count;
        if (message.Count > _options.MaxFrameBytes)
        {
            return Frame.TooLarge;
        }

        if (!result.EndOfMessage)
        {
            return null;
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            return Frame.Lost; // never lend from a disposed socket; Abandon returns the array
        }

        // published, and the message drops it: a Dispose that already ran leaves it to the GC (allowed)
        Volatile.Write(ref _rented, message.Rented);
        message.Rented = null;
        var kind = result.MessageType == WebSocketMessageType.Text ? FrameKind.Text : FrameKind.Binary;
        return new Frame(kind, message.Buffer.AsMemory(0, message.Count));
    }

    /// <summary>Receive step 4, on every exit: returns an array the message never published — the read is over.</summary>
    internal void Abandon(ref Inbound message)
    {
        if (message.Rented is { } rented)
        {
            message.Rented = null;
            _pool.Return(rented, clearArray: true);
        }
    }

    /// <summary>Receives and discards until the peer's close or the end of the connection (after the relay closed).</summary>
    public async Task DrainAsync()
    {
        while (true)
        {
            var frame = await ReceiveAsync();
            if (frame.Kind is FrameKind.Close or FrameKind.Lost)
            {
                return;
            }
        }
    }

    public Task<bool> SendTextAsync(string json) => SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text);

    /// <summary>
    /// False when the socket is closing or gone; a send that runs <see cref="RelayOptions.SendTimeout"/> is aborted by
    /// the socket's one send timer (D3: no CancellationTokenSource per send — any cancelable token also sends
    /// ManagedWebSocket down its unpooled path). Built from the send steps below, which the pump inlines (D4).
    /// </summary>
    public async Task<bool> SendAsync(ReadOnlyMemory<byte> data, WebSocketMessageType type)
    {
        await WaitSendAsync();
        bool armed = false, written = false, sent;
        try
        {
            armed = BeginSend();
            if (armed)
            {
                await WriteAsync(data, type);
                written = true;
            }
        }
        catch (Exception e) when (IsConnectionGone(e))
        {
            // a dead peer, or our timer's abort: not written
        }
        finally
        {
            sent = EndSend(armed, written);
        }

        return sent;
    }

    /// <summary>Send step 1: one send at a time. Bounded: the holder's own send or close is bounded.</summary>
    internal Task WaitSendAsync() => _send.WaitAsync(CancellationToken.None);

    /// <summary>Send step 2, holding the lock: false when closing; else the send's start is stored and the timer armed.</summary>
    internal bool BeginSend()
    {
        if (IsClosing)
        {
            return false;
        }

        Volatile.Write(ref _sendStart, _time.GetTimestamp());
        _sendTimer.Change(_options.SendTimeout, Timeout.InfiniteTimeSpan);
        return true;
    }

    /// <summary>Send step 3: the framework write, with no token (D3).</summary>
    internal ValueTask WriteAsync(ReadOnlyMemory<byte> data, WebSocketMessageType type) =>
        _ws.SendAsync(data, type, endOfMessage: true, CancellationToken.None);

    /// <summary>
    /// Send step 4, on every exit: clears the start and disarms BEFORE releasing the lock (a disarm after it could wipe
    /// the next send's arm, a stale start could abort an idle socket). True only when written and the timer did not win
    /// (it aborted the socket).
    /// </summary>
    internal bool EndSend(bool armed, bool written)
    {
        var ended = NoSend;
        if (armed)
        {
            ended = Interlocked.Exchange(ref _sendStart, NoSend);
            _sendTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        _send.Release();
        return written && ended != Aborted;
    }

    /// <summary>
    /// The send timer's callback (internal for tests that run it late by hand). Aborts only the send whose start it read,
    /// and only once it has really run SendTimeout: a callback that fires late for an older send re-arms or does nothing.
    /// </summary>
    internal void OnSendTimer()
    {
        var start = Volatile.Read(ref _sendStart);
        if (start is NoSend or Aborted)
        {
            return;
        }

        var elapsed = _time.GetElapsedTime(start);
        if (elapsed < _options.SendTimeout)
        {
            _sendTimer.Change(_options.SendTimeout - elapsed, Timeout.InfiniteTimeSpan);
            return;
        }

        if (Interlocked.CompareExchange(ref _sendStart, Aborted, start) == start)
        {
            _ws.Abort(); // the peer stopped reading: a stuck send leaves the socket unusable anyway
        }
    }

    /// <summary>
    /// Sends a close frame once (the first caller's code wins), after any send in progress, then gives the peer
    /// <see cref="RelayOptions.CloseTimeout"/> to answer before aborting. The receiver sees the answer as Close.
    /// </summary>
    public async Task CloseAsync(int code, string reason = "")
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0)
        {
            return;
        }

        CloseCode = code;
        _abortTimer = _time.CreateTimer(static s => ((WebSocket)s!).Abort(), _ws, _options.CloseTimeout, Timeout.InfiniteTimeSpan);
        await _send.WaitAsync(CancellationToken.None);
        try
        {
            if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(_options.CloseTimeout, _time);
                await _ws.CloseOutputAsync((WebSocketCloseStatus)code, reason, timeout.Token);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            _ws.Abort();
        }
        finally
        {
            _send.Release();
        }
    }

    public void Abort() => _ws.Abort();

    /// <summary>
    /// Stops the send and abort timers and ends the last frame's lease. The send lock is not disposed: another handler's
    /// fire-and-forget <see cref="CloseAsync"/> (a quota or control-loss close) may still be waiting on it, and a
    /// SemaphoreSlim whose wait handle was never read holds no OS resource. A receive still in flight keeps its array
    /// and returns it itself (D2).
    /// </summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        _sendTimer.Dispose(); // a later send's Change on it returns false; the disposed WebSocket fails that send (S2)
        _abortTimer?.Dispose();
        ReturnRented();
    }

    private void ReturnRented()
    {
        if (Interlocked.Exchange(ref _rented, null) is { } array)
        {
            _pool.Return(array, clearArray: true); // no tunnel bytes or control secrets left in the shared pool (F12)
        }
    }
}
