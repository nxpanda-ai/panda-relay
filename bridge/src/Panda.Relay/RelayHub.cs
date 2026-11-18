using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Panda.Bridge.Auth;
using Panda.Bridge.Protocol;
using Panda.Bridge.Relay;

namespace Panda.Relay;

/// <summary>A registered control socket: the Chrome online under a relayId (S1).</summary>
internal sealed class BrowserLink(string relayId, RelaySocket socket, string? linkHash, string? pairHash)
{
    public string RelayId { get; } = relayId;
    public RelaySocket Socket { get; } = socket;
    public string? LinkHash { get; set; } = linkHash;
    public string? PairHash { get; set; } = pairHash;
    public Dictionary<RelayStreamKind, RelayStream> Slots { get; } = [];
    public bool Gone { get; set; }

    private int _hosting;

    /// <summary>D6: one RELAY_HOST / ROTATE / UNHOST in flight per control socket; false = one is running.</summary>
    public bool TryStartHosting() => Interlocked.CompareExchange(ref _hosting, 1, 0) == 0;

    public void EndHosting() => Volatile.Write(ref _hosting, 0);
}

internal enum StreamState
{
    Waiting,
    Open,
    Closed,
}

/// <summary>One data stream: a dialer, and once accepted the extension's socket. State changes under the hub's lock.</summary>
internal sealed class RelayStream(string id, BrowserLink owner, RelayStreamKind kind, RelaySocket dialer)
{
    public string Id { get; } = id;
    public BrowserLink Owner { get; } = owner;
    public RelayStreamKind Kind { get; } = kind;
    public RelaySocket Dialer { get; } = dialer;
    public RelaySocket? Ext { get; set; }
    public StreamState State { get; set; } = StreamState.Waiting;
    public long Up { get; set; }
    public long Down { get; set; }

    /// <summary>True when the extension accepted; false when the stream closed first.</summary>
    public TaskCompletionSource<bool> Accepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the dialer's handler has finished both directions (the acceptor's request waits for it).</summary>
    public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// The relay (plan-relay P4, seams S1 + S6). ONE lock guards every map and every state change; sockets are closed
/// only after the state that names them is gone (a freed slot is visible before the close frame leaves), and never
/// under the lock. The relay never reads payload: binary frames are counted and forwarded whole.
/// </summary>
internal sealed partial class RelayHub
{
    private const string DialHeader = "X-Panda-Dial";
    private const string ExtensionOriginPrefix = "chrome-extension://";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, BrowserLink> _online = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RelayStream> _pending = new(StringComparer.Ordinal);
    private readonly RelayOptions _options;
    private readonly TimeProvider _time;
    private readonly RelayQuotas _quotas;
    private readonly NotOnlineLog _notOnlineLog; // under _gate
    private readonly ILogger<RelayHub> _logger;
    private readonly HostedAdmin _hosted;
    private int _hostingInFlight; // plan-relay-deploy C3: hosted calls running, all sockets (Interlocked)

    public RelayHub(RelayOptions options, TimeProvider time, ILogger<RelayHub> logger, HostedAdmin hosted)
    {
        _options = options;
        _time = time;
        _quotas = new RelayQuotas(options);
        _notOnlineLog = new NotOnlineLog(options.MaxTrackedNoise);
        _logger = logger;
        _hosted = hosted;
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    private WebSocketAcceptContext AcceptContext => new()
    {
        KeepAliveInterval = _options.KeepAliveInterval,
        KeepAliveTimeout = _options.KeepAliveTimeout,
    };

    // ------------------------------------------------------------------ /v1/browser

    public async Task RunControlAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!FromExtension(context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden; // before anything is counted
            return;
        }

        var ip = RelayQuotas.Bucket(context.Connection.RemoteIpAddress);
        using var ws = await context.WebSockets.AcceptWebSocketAsync(AcceptContext);
        using var socket = new RelaySocket(ws, _options, _time);
        bool allowed;
        int retryAfter;
        lock (_gate)
        {
            allowed = _quotas.TryCountRegistration(ip, Now, out retryAfter);
        }

        if (!allowed)
        {
            LogRegistrationLimited(ip);
            await CloseAndDrainAsync(socket, RelayCloseCodes.DialerLimited, Reason(retryAfter));
            return;
        }

        // S1 v1.11: an optional `id` names the relayId this socket will prove; malformed or repeated → 4500
        var query = context.Request.Query;
        var claimed = Single(query, "id");
        if (query.ContainsKey("id") && (claimed is null || !IsRelayId(claimed)))
        {
            await CloseAndDrainAsync(socket, RelayCloseCodes.ProtocolError);
            return;
        }

        BrowserLink? link = null;
        try
        {
            link = await RegisterAsync(socket, ip, claimed);
            if (link is not null)
            {
                await ControlLoopAsync(link);
            }
        }
        finally
        {
            if (link is not null)
            {
                CloseLink(link, (int)WebSocketCloseStatus.EndpointUnavailable);
                LogControlClosed(link.RelayId, socket.CloseCode);
            }
        }
    }

    /// <summary>
    /// CHALLENGE → REGISTER (≤ RegisterTimeout) → REGISTERED; null when the socket was closed instead. A
    /// <paramref name="claimed"/> relayId (S1 v1.11 `id`) must be the one the signature proves, else 4503.
    /// </summary>
    private async Task<BrowserLink?> RegisterAsync(RelaySocket socket, IPAddress ip, string? claimed)
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        Frame first;
        using (_time.CreateTimer(Close(RelayCloseCodes.ProtocolError, "register timeout"), socket, _options.RegisterTimeout, Timeout.InfiniteTimeSpan))
        {
            await socket.SendTextAsync(ProtocolJson.SerializeRelay(new RelayChallenge(AuthCrypto.EncodeBase64Url(nonce))));
            first = await socket.ReceiveAsync();
        }

        if (await EndedAsync(socket, first))
        {
            return null;
        }

        if (first.Kind != FrameKind.Text
            || ProtocolJson.ParseRelay(System.Text.Encoding.UTF8.GetString(first.Bytes.Span)) is not RelayParseResult.Ok { Message: RelayRegister register })
        {
            await CloseAndDrainAsync(socket, first.Kind == FrameKind.TooLarge ? RelayCloseCodes.FrameTooLarge : RelayCloseCodes.ProtocolError);
            return null;
        }

        if (RegistrationProof.Verify(register, nonce, _options.PublicHost) is not { } spki
            || (claimed is not null && !string.Equals(claimed, DialTokens.RelayIdFromSpki(spki), StringComparison.Ordinal)))
        {
            LogRegistrationRefused(ip);
            await CloseAndDrainAsync(socket, RelayCloseCodes.RegistrationInvalid);
            return null;
        }

        var link = new BrowserLink(DialTokens.RelayIdFromSpki(spki), socket, register.Link, register.Pair);
        if (!await socket.SendTextAsync(ProtocolJson.SerializeRelay(new RelayRegistered(link.RelayId))))
        {
            await socket.DrainAsync();
            return null;
        }

        BrowserLink? older;
        lock (_gate)
        {
            _online.TryGetValue(link.RelayId, out older);
            _online[link.RelayId] = link; // the hashes go online with the relayId (S1, critic N1)
        }

        if (older is not null)
        {
            CloseLink(older, RelayCloseCodes.Replaced);
        }

        LogRegistered(link.RelayId, ip);
        return link;
    }

    private async Task ControlLoopAsync(BrowserLink link)
    {
        var socket = link.Socket;
        using var idle = _time.CreateTimer(Close((int)WebSocketCloseStatus.EndpointUnavailable, "idle"), socket, _options.ControlIdleTimeout, Timeout.InfiniteTimeSpan);
        while (true)
        {
            var frame = await socket.ReceiveAsync();
            idle.Change(_options.ControlIdleTimeout, Timeout.InfiniteTimeSpan);
            if (await EndedAsync(socket, frame))
            {
                return;
            }

            if (frame.Kind != FrameKind.Text)
            {
                await socket.CloseAsync(frame.Kind == FrameKind.TooLarge ? RelayCloseCodes.FrameTooLarge : RelayCloseCodes.ProtocolError);
                continue;
            }

            switch (ProtocolJson.ParseRelay(System.Text.Encoding.UTF8.GetString(frame.Bytes.Span)))
            {
                case RelayParseResult.Ok { Message: RelayDialTokens tokens }:
                    lock (_gate)
                    {
                        link.LinkHash = tokens.Link; // replaces the set; absent = none (S1)
                        link.PairHash = tokens.Pair;
                    }

                    await socket.SendTextAsync(ProtocolJson.SerializeRelay(new RelayTokensOk(tokens.Seq)));
                    break;
                case RelayParseResult.Ok { Message: RelayPing }:
                    await socket.SendTextAsync(ProtocolJson.SerializeRelay(new RelayPong()));
                    break;
                case RelayParseResult.Ok { Message: RelayPong }:
                    break;
                case RelayParseResult.Ok { Message: RelayHost or RelayRotate or RelayUnhost } hosting:
                    await StartHostingAsync(link, hosting.Message);
                    break;
                default:
                    await socket.CloseAsync(RelayCloseCodes.ProtocolError);
                    break;
            }
        }
    }

    /// <summary>
    /// plan-relay-url D6: the control loop never waits for the hosted bridge. The request runs as a task, one per socket
    /// (a second → <c>busy</c>), and RELAY_HOST / ROTATE also take one of the relay-wide <c>--max-hosting</c> slots (full →
    /// <c>busy</c>; plan-relay-deploy C3; UNHOST is exempt); its result goes through the socket's locked send path, and is dropped if the socket
    /// has closed meanwhile. The slot is freed before the result is sent, so the Chrome's next request (sent once it
    /// reads this result) never finds it taken. The message itself is never formatted or logged (it holds the code).
    /// </summary>
    private async Task StartHostingAsync(BrowserLink link, IRelayMessage request)
    {
        var seq = HostedAdmin.SeqOf(request);
        if (!_hosted.Configured)
        {
            await link.Socket.SendTextAsync(ProtocolJson.SerializeRelay(new RelayHostResult(seq, false, Error: RelayHostError.Unavailable)));
            return;
        }

        if (!link.TryStartHosting())
        {
            await link.Socket.SendTextAsync(ProtocolJson.SerializeRelay(new RelayHostResult(seq, false, Error: RelayHostError.Busy)));
            return;
        }

        // plan-relay-deploy C3: the relay-wide cap; UNHOST never counts, so a full cap cannot delay a revocation (C3 critic T1)
        var capped = request is not RelayUnhost;
        if (capped && Interlocked.Increment(ref _hostingInFlight) > _options.MaxHosting)
        {
            Interlocked.Decrement(ref _hostingInFlight);
            link.EndHosting();
            await link.Socket.SendTextAsync(ProtocolJson.SerializeRelay(new RelayHostResult(seq, false, Error: RelayHostError.Busy)));
            return;
        }

        _ = Task.Run(async () =>
        {
            RelayHostResult result;
            try
            {
                result = await _hosted.RunAsync(link.RelayId, request);
            }
            catch (Exception ex)
            {
                LogHostingFailed(link.RelayId, ex.GetType().Name); // a bug, not an answer: the Chrome hears failed
                result = new RelayHostResult(seq, false, Error: RelayHostError.Failed);
            }
            finally
            {
                if (capped)
                {
                    Interlocked.Decrement(ref _hostingInFlight);
                }

                link.EndHosting();
            }

            await link.Socket.SendTextAsync(ProtocolJson.SerializeRelay(result));
        });
    }

    /// <summary>Takes a Chrome offline: its slots and pending streamIds go first, then every socket is closed.</summary>
    private void CloseLink(BrowserLink link, int controlCode)
    {
        List<RelayStream> streams;
        lock (_gate)
        {
            link.Gone = true;
            if (_online.TryGetValue(link.RelayId, out var current) && current == link)
            {
                _online.Remove(link.RelayId);
            }

            streams = [.. link.Slots.Values];
            foreach (var stream in streams)
            {
                Release(stream);
            }
        }

        foreach (var stream in streams)
        {
            CloseSockets(stream, (int)WebSocketCloseStatus.EndpointUnavailable, "");
        }

        _ = link.Socket.CloseAsync(controlCode);
    }

    public void CloseAll()
    {
        List<BrowserLink> links;
        lock (_gate)
        {
            links = [.. _online.Values];
        }

        foreach (var link in links)
        {
            CloseLink(link, (int)WebSocketCloseStatus.EndpointUnavailable);
        }
    }

    // ------------------------------------------------------------------ /v1/stream

    public async Task RunStreamAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // C5: a dial on the internal listener (the hosted bridge beside the relay) has no IP bucket
        IPAddress? ip = InternalListener.Is(context) ? null : RelayQuotas.Bucket(context.Connection.RemoteIpAddress);
        var query = context.Request.Query;
        var dial = Single(query, "dial");
        var kind = Single(query, "kind") switch
        {
            "link" => RelayStreamKind.Link,
            "pair" => (RelayStreamKind?)RelayStreamKind.Pair,
            _ => null,
        };
        var accept = Single(query, "accept");
        var headers = context.Request.Headers[DialHeader];
        var token = headers.Count == 1 ? headers[0] : null;
        if (query.ContainsKey("accept") ? !FromExtension(context) : context.Request.Headers.Origin.Count != 0)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden; // before anything is counted
            return;
        }

        using var ws = await context.WebSockets.AcceptWebSocketAsync(AcceptContext);
        using var socket = new RelaySocket(ws, _options, _time);
        if (query.Count == 2 && dial is not null && IsRelayId(dial) && kind is { } k)
        {
            await RunDialAsync(socket, dial, k, token, ip);
        }
        else if (accept is not null && AuthCrypto.TryDecodeBase64Url(accept, 16, out _)
            && (query.Count == 1 || (query.Count == 2 && Single(query, "id") is { } owner && IsRelayId(owner))))
        {
            await RunAcceptAsync(socket, accept, Single(query, "id"), ip);
        }
        else
        {
            CountNoise(ip); // C6
            await CloseAndDrainAsync(socket, RelayCloseCodes.ProtocolError);
        }
    }

    private void CountNoise(IPAddress? ip)
    {
        if (ip is not null)
        {
            lock (_gate)
            {
                _quotas.CountNoise(ip, Now);
            }
        }
    }

    private async Task RunDialAsync(RelaySocket dialer, string relayId, RelayStreamKind kind, string? token, IPAddress? ip)
    {
        var (stream, code, retryAfter) = Admit(dialer, relayId, kind, token, ip);
        if (stream is null)
        {
            var line = NotOnlineLine.Untracked;
            var previousHour = 0;
            if (code == RelayCloseCodes.NotOnline)
            {
                lock (_gate)
                {
                    line = _notOnlineLog.Count(relayId, kind, Now, out previousHour);
                }
            }

            if (line == NotOnlineLine.First)
            {
                LogDialNotOnline(relayId, kind, code, ip, previousHour);
            }
            else if (line == NotOnlineLine.Untracked)
            {
                LogDialRefused(relayId, kind, code, ip); // every other code, and a 4504 past the table
            }

            await CloseAndDrainAsync(dialer, code, retryAfter is { } s ? Reason(s) : "");
            return;
        }

        try
        {
            await RunAdmittedAsync(stream);
        }
        finally
        {
            Release(stream, locked: false);
            stream.Done.TrySetResult();
            LogStreamClosed(relayId, kind, stream.Up, stream.Down, dialer.CloseCode, stream.Ext?.CloseCode ?? 0);
        }
    }

    /// <summary>The S1 check order: 4530 → 4504 → 4501 (counts on the IP) → 4529 → 4509 → slot reserved.</summary>
    private (RelayStream? Stream, int Code, int? RetryAfter) Admit(RelaySocket dialer, string relayId, RelayStreamKind kind, string? token, IPAddress? ip)
    {
        var now = Now;
        lock (_gate)
        {
            if (ip is not null && _quotas.DialerLimited(ip, now, out var ipRetry))
            {
                return (null, RelayCloseCodes.DialerLimited, ipRetry);
            }

            if (!_online.TryGetValue(relayId, out var link))
            {
                if (ip is not null)
                {
                    _quotas.CountNotOnline(ip, relayId, now); // C6: once per (IP, relayId) per hour
                }

                return (null, RelayCloseCodes.NotOnline, null);
            }

            if (!TokenMatches(kind == RelayStreamKind.Link ? link.LinkHash : link.PairHash, token))
            {
                if (ip is not null)
                {
                    _quotas.CountRefusedDial(ip, now);
                }

                return (null, RelayCloseCodes.DialRefused, null);
            }

            if (_quotas.Blocked(relayId, now, out var quotaRetry))
            {
                return (null, RelayCloseCodes.QuotaExceeded, quotaRetry);
            }

            if (link.Slots.ContainsKey(kind))
            {
                return (null, RelayCloseCodes.SlotTaken, null);
            }

            var stream = new RelayStream(AuthCrypto.EncodeBase64Url(RandomNumberGenerator.GetBytes(16)), link, kind, dialer);
            link.Slots[kind] = stream;
            _pending[stream.Id] = stream;
            return (stream, 0, null);
        }
    }

    private async Task RunAdmittedAsync(RelayStream stream)
    {
        var dialer = stream.Dialer;
        var timedOut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timer = _time.CreateTimer(static s => ((TaskCompletionSource)s!).TrySetResult(), timedOut, _options.AcceptTimeout, Timeout.InfiniteTimeSpan);
        // A failed send means the control socket is closing: its CloseLink closes this stream (1001).
        await stream.Owner.Socket.SendTextAsync(ProtocolJson.SerializeRelay(new RelayIncoming(stream.Id, stream.Kind)));
        var first = dialer.ReceiveAsync();
        await Task.WhenAny(stream.Accepted.Task, first, timedOut.Task);

        bool open;
        lock (_gate)
        {
            open = stream.State == StreamState.Open;
            if (stream.State == StreamState.Waiting)
            {
                Release(stream); // the dialer spoke or left first, or the accept timed out
            }
        }

        if (!open)
        {
            stream.Accepted.TrySetResult(false);
            if (!first.IsCompleted)
            {
                await dialer.CloseAsync(RelayCloseCodes.AcceptTimeout); // no-op when the stream was closed by others
            }
            else
            {
                await EndOrRefuseAsync(dialer, await first);
            }

            await DrainAfterAsync(dialer, first);
            return;
        }

        var ext = stream.Ext!;
        if (first.IsCompleted)
        {
            // The dialer spoke or left before STREAM_OPEN.
            Release(stream, locked: false);
            await EndOrRefuseAsync(dialer, await first);
            await ext.CloseAsync((int)WebSocketCloseStatus.NormalClosure);
            await DrainAfterAsync(dialer, first);
            await ext.DrainAsync();
            return;
        }

        if (SpendStream(stream) is { } retryAfter)
        {
            CloseSockets(stream, RelayCloseCodes.QuotaExceeded, Reason(retryAfter));
            await DrainAfterAsync(dialer, first);
            await ext.DrainAsync();
            return;
        }

        await dialer.SendTextAsync(ProtocolJson.SerializeRelay(new RelayStreamOpen()));
        await Task.WhenAll(PumpAsync(stream, dialer, ext, first, up: true), PumpAsync(stream, ext, dialer, null, up: false));
    }

    /// <summary>
    /// Opens the pending stream <paramref name="streamId"/>. A given <paramref name="owner"/> (S1 v1.11 `id`) is
    /// checked BEFORE the stream is taken, so a wrong id gets 4504 and leaves the stream to its real accept.
    /// </summary>
    private async Task RunAcceptAsync(RelaySocket ext, string streamId, string? owner, IPAddress? ip)
    {
        RelayStream? stream = null;
        int? limitedFor = null;
        lock (_gate)
        {
            if (_pending.TryGetValue(streamId, out var pending)
                && (owner is null || string.Equals(owner, pending.Owner.RelayId, StringComparison.Ordinal)))
            {
                _pending.Remove(streamId);
                if (pending.State == StreamState.Waiting && !pending.Owner.Gone)
                {
                    pending.State = StreamState.Open;
                    pending.Ext = ext;
                    stream = pending;
                }
            }

            // C6: a refused accept is noise; over the budget it is 4530 (a known pending accept above is never refused)
            if (stream is null && ip is not null)
            {
                if (_quotas.DialerLimited(ip, Now, out var retryAfter))
                {
                    limitedFor = retryAfter;
                }
                else
                {
                    _quotas.CountNoise(ip, Now);
                }
            }
        }

        if (stream is null)
        {
            await (limitedFor is { } seconds
                ? CloseAndDrainAsync(ext, RelayCloseCodes.DialerLimited, Reason(seconds))
                : CloseAndDrainAsync(ext, RelayCloseCodes.NotOnline)); // unknown, expired, used, its Chrome left, or another relayId's (S1 v1.11)
            return;
        }

        stream.Accepted.TrySetResult(true);
        await stream.Done.Task; // the dialer's handler reads and closes this socket too
    }

    /// <summary>
    /// Forwards whole binary frames from one side to the other until <paramref name="from"/> ends. Receives and sends
    /// with RelaySocket's steps inline (= its ReceiveAsync / SendAsync), so this one state machine, boxed once per
    /// stream, does every per-message await: nothing is allocated per forwarded message (plan-zero-alloc-relay D4).
    /// </summary>
    internal async Task PumpAsync(RelayStream stream, RelaySocket from, RelaySocket to, Task<Frame>? first, bool up)
    {
        var frame = first is null ? await from.ReceiveAsync() : await first;
        while (true)
        {
            switch (frame.Kind)
            {
                case FrameKind.Close:
                    Release(stream, locked: false);
                    await from.CloseAsync((int)WebSocketCloseStatus.NormalClosure);
                    await to.CloseAsync((int)WebSocketCloseStatus.NormalClosure); // never the peer's own code (S1)
                    return;
                case FrameKind.Lost:
                    Release(stream, locked: false);
                    await to.CloseAsync((int)WebSocketCloseStatus.NormalClosure);
                    return;
                case var _ when from.IsClosing:
                    break; // draining after a close
                case FrameKind.TooLarge or FrameKind.Text:
                    Release(stream, locked: false);
                    await from.CloseAsync(frame.Kind == FrameKind.TooLarge ? RelayCloseCodes.FrameTooLarge : RelayCloseCodes.ProtocolError);
                    await to.CloseAsync((int)WebSocketCloseStatus.NormalClosure);
                    break;
                case FrameKind.Binary:
                {
                    if (Count(stream, frame.Bytes.Length, up) is { } over)
                    {
                        foreach (var s in over.Streams)
                        {
                            CloseSockets(s, RelayCloseCodes.QuotaExceeded, Reason(over.RetryAfter));
                        }

                        break;
                    }

                    // = to.SendAsync(frame.Bytes, Binary), inline (D4); keep in step with SendAsync: WaitSendAsync pairs
                    // with exactly one EndSend, in finally
                    await to.WaitSendAsync();
                    bool armed = false, written = false, sent;
                    try
                    {
                        armed = to.BeginSend();
                        if (armed)
                        {
                            await to.WriteAsync(frame.Bytes, WebSocketMessageType.Binary);
                            written = true;
                        }
                    }
                    catch (Exception e) when (RelaySocket.IsConnectionGone(e))
                    {
                        // a dead peer, or the send timer's abort: not written
                    }
                    finally
                    {
                        sent = to.EndSend(armed, written);
                    }

                    if (!sent)
                    {
                        Release(stream, locked: false);
                        await from.CloseAsync((int)WebSocketCloseStatus.NormalClosure);
                        if (!to.IsClosing)
                        {
                            to.Abort(); // dead or stuck; a socket already closing keeps its close code (S1, S6)
                        }
                    }

                    break;
                }
            }

            // = from.ReceiveAsync(), inline (D4); keep in step with ReceiveAsync: BeginReceive (ends the lease of the
            // frame just forwarded) pairs with Abandon, in finally
            var message = from.BeginReceive();
            try
            {
                Frame? received;
                do
                {
                    var read = from.ReadAsync(ref message);
                    received = from.Received(ref message, await read);
                }
                while (received is null);

                frame = received.Value;
            }
            catch (Exception e) when (RelaySocket.IsConnectionGone(e))
            {
                frame = Frame.Lost;
            }
            finally
            {
                from.Abandon(ref message);
            }
        }
    }

    /// <summary>
    /// S6: a stream is spent at STREAM_OPEN. Null when it may open (now counted); else the seconds to the next day, and
    /// the stream is released. A stream that another close already released is left to that close (null, not counted).
    /// </summary>
    private int? SpendStream(RelayStream stream)
    {
        var now = Now;
        lock (_gate)
        {
            if (stream.State != StreamState.Open)
            {
                return null;
            }

            if (_quotas.Blocked(stream.Owner.RelayId, now, out var retryAfter))
            {
                Release(stream);
                return retryAfter;
            }

            _quotas.CountStream(stream.Owner.RelayId, now);
            return null;
        }
    }

    /// <summary>Counts piped bytes; on crossing the day's quota, returns every stream of that Chrome (already released).</summary>
    private (List<RelayStream> Streams, int RetryAfter)? Count(RelayStream stream, int bytes, bool up)
    {
        lock (_gate)
        {
            if (up)
            {
                stream.Up += bytes;
            }
            else
            {
                stream.Down += bytes;
            }

            if (!_quotas.AddBytes(stream.Owner.RelayId, bytes, Now, out var retryAfter))
            {
                return null;
            }

            List<RelayStream> all = [.. stream.Owner.Slots.Values];
            if (!all.Contains(stream))
            {
                all.Add(stream);
            }

            foreach (var s in all)
            {
                Release(s);
            }

            return (all, retryAfter);
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Frees a stream's slot and streamId (under the lock; idempotent).</summary>
    private void Release(RelayStream stream, bool locked = true)
    {
        if (!locked)
        {
            lock (_gate)
            {
                Release(stream);
            }

            return;
        }

        if (stream.State == StreamState.Closed)
        {
            return;
        }

        stream.State = StreamState.Closed;
        if (stream.Owner.Slots.TryGetValue(stream.Kind, out var held) && held == stream)
        {
            stream.Owner.Slots.Remove(stream.Kind);
        }

        _pending.Remove(stream.Id);
    }

    private static void CloseSockets(RelayStream stream, int code, string reason)
    {
        stream.Accepted.TrySetResult(false);
        _ = stream.Dialer.CloseAsync(code, reason);
        _ = stream.Ext?.CloseAsync(code, reason);
    }

    /// <summary>True when the peer's close or a dead connection ended the socket (the close is answered).</summary>
    private static async Task<bool> EndedAsync(RelaySocket socket, Frame frame)
    {
        switch (frame.Kind)
        {
            case FrameKind.Close:
                await socket.CloseAsync((int)WebSocketCloseStatus.NormalClosure);
                return true;
            case FrameKind.Lost:
                return true;
            default:
                if (socket.IsClosing)
                {
                    await socket.DrainAsync();
                    return true;
                }

                return false;
        }
    }

    /// <summary>Answers a peer close, or closes 4513 / 4500 for anything else the dialer sent before STREAM_OPEN.</summary>
    private static Task EndOrRefuseAsync(RelaySocket dialer, Frame frame) => frame.Kind switch
    {
        FrameKind.Close => dialer.CloseAsync((int)WebSocketCloseStatus.NormalClosure),
        FrameKind.Lost => Task.CompletedTask,
        FrameKind.TooLarge => dialer.CloseAsync(RelayCloseCodes.FrameTooLarge),
        _ => dialer.CloseAsync(RelayCloseCodes.ProtocolError),
    };

    private static async Task DrainAfterAsync(RelaySocket socket, Task<Frame> pending)
    {
        var frame = await pending;
        if (frame.Kind is not (FrameKind.Close or FrameKind.Lost))
        {
            await socket.DrainAsync();
        }
    }

    private static async Task CloseAndDrainAsync(RelaySocket socket, int code, string reason = "")
    {
        await socket.CloseAsync(code, reason);
        await socket.DrainAsync();
    }

    private static TimerCallback Close(int code, string reason) => s => _ = ((RelaySocket)s!).CloseAsync(code, reason);

    private static bool TokenMatches(string? registered, string? token)
    {
        if (registered is null || token is null || !DialTokens.TryHash(token, out var hash))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(hash), System.Text.Encoding.ASCII.GetBytes(registered));
    }

    /// <summary>
    /// Only an extension opens /v1/browser or accepts a stream; a bridge sends no Origin. A web page always sends its
    /// own, so it can neither use these paths nor spend the IP limits of the user whose browser runs it.
    /// </summary>
    private static bool FromExtension(HttpContext context)
    {
        var origin = context.Request.Headers.Origin;
        return origin.Count == 1 && origin[0] is { Length: 51 } value // chrome-extension:// + a 32-letter id (a–p)
            && value.StartsWith(ExtensionOriginPrefix, StringComparison.Ordinal)
            && value.AsSpan(ExtensionOriginPrefix.Length).IndexOfAnyExceptInRange('a', 'p') < 0;
    }

    private static bool IsRelayId(string value) =>
        value.Length == 22 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string? Single(IQueryCollection query, string key) =>
        query.TryGetValue(key, out var values) && values.Count == 1 ? values[0] : null;

    private static string Reason(int seconds) => seconds.ToString(CultureInfo.InvariantCulture);

    // Logs carry relayId, kind, codes, byte counts and the IP bucket only — never tokens, hashes, nonces,
    // signatures, keys or payload (spec §37, §78.4).
    [LoggerMessage(Level = LogLevel.Information, Message = "registered {RelayId} from {Ip}")]
    private partial void LogRegistered(string relayId, IPAddress ip);

    [LoggerMessage(Level = LogLevel.Information, Message = "registration proof refused from {Ip}")]
    private partial void LogRegistrationRefused(IPAddress ip);

    [LoggerMessage(Level = LogLevel.Information, Message = "registration limit reached for {Ip}")]
    private partial void LogRegistrationLimited(IPAddress ip);

    [LoggerMessage(Level = LogLevel.Information, Message = "control socket of {RelayId} closed ({Code})")]
    private partial void LogControlClosed(string relayId, int code);

    [LoggerMessage(Level = LogLevel.Information, Message = "dial {Kind} for {RelayId} refused {Code} from {Ip}")]
    private partial void LogDialRefused(string relayId, RelayStreamKind kind, int code, IPAddress? ip);

    // 2026-11-18: a bridge redials an offline Chrome every minute — the first 4504 of a (relayId, kind) per hour only
    [LoggerMessage(Level = LogLevel.Information, Message = "dial {Kind} for {RelayId} refused {Code} from {Ip} (logged once an hour; {PreviousHour} in the previous hour)")]
    private partial void LogDialNotOnline(string relayId, RelayStreamKind kind, int code, IPAddress? ip, int previousHour);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hosting request of relay {RelayId} failed with {ExceptionType}")]
    private partial void LogHostingFailed(string relayId, string exceptionType);

    [LoggerMessage(Level = LogLevel.Information, Message = "stream {Kind} of {RelayId} closed: {Up} B up, {Down} B down, dialer {DialerCode}, extension {ExtCode}")]
    private partial void LogStreamClosed(string relayId, RelayStreamKind kind, long up, long down, int dialerCode, int extCode);
}
