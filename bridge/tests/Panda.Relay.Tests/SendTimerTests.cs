using System.Net.WebSockets;
using Microsoft.Extensions.Time.Testing;

namespace Panda.Relay.Tests;

/// <summary>
/// plan-zero-alloc-relay D3 / seam S2: one send timer per socket, driven by the send's start time. A late (stale)
/// callback never aborts a younger healthy send or an idle socket; a send that really runs SendTimeout is aborted.
/// </summary>
public class SendTimerTests
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_send_stuck_for_SendTimeout_is_aborted_and_returns_false()
    {
        var (socket, stream, ws, time) = Create();
        using var s = socket;
        var send = socket.SendAsync(new byte[10], WebSocketMessageType.Binary);
        await stream.WriteStartedAsync();

        time.Advance(SendTimeout - TimeSpan.FromMilliseconds(1));
        Assert.Equal(WebSocketState.Open, ws.State); // synchronous: an early abort would show here
        Assert.False(send.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.False(await send.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(WebSocketState.Aborted, ws.State);
    }

    [Fact]
    public async Task A_stale_callback_never_aborts_a_younger_send()
    {
        var (socket, stream, ws, time) = Create();
        using var s = socket;

        var older = socket.SendAsync(new byte[10], WebSocketMessageType.Binary);
        await stream.WriteStartedAsync();
        time.Advance(SendTimeout - TimeSpan.FromSeconds(1));
        stream.CompleteWrite();
        Assert.True(await older);

        var younger = socket.SendAsync(new byte[10], WebSocketMessageType.Binary);
        await stream.WriteStartedAsync();
        time.Advance(TimeSpan.FromSeconds(1)); // the older send's deadline
        socket.OnSendTimer(); // its callback, running late

        Assert.Equal(WebSocketState.Open, ws.State);
        Assert.False(younger.IsCompleted);
        stream.CompleteWrite();
        Assert.True(await younger);
    }

    [Fact]
    public async Task A_late_callback_never_aborts_an_idle_socket()
    {
        var (socket, stream, ws, time) = Create();
        using var s = socket;

        var send = socket.SendAsync(new byte[10], WebSocketMessageType.Binary);
        await stream.WriteStartedAsync();
        stream.CompleteWrite();
        Assert.True(await send);

        time.Advance(SendTimeout * 2);
        socket.OnSendTimer();

        Assert.Equal(WebSocketState.Open, ws.State);
    }

    /// <summary>S2: the hosting sender may send on a control socket its handler already disposed (critic F11).</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_send_after_Dispose_returns_false_without_throwing(bool systemClock)
    {
        var (socket, _, ws, _) = Create(systemClock ? TimeProvider.System : null);
        socket.Dispose();
        ws.Dispose();

        Assert.False(await socket.SendAsync(new byte[10], WebSocketMessageType.Binary));
    }

    private static (RelaySocket Socket, StallingStream Stream, WebSocket Ws, FakeTimeProvider Time) Create(TimeProvider? clock = null)
    {
        var time = new FakeTimeProvider();
        var stream = new StallingStream();
        var ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.Zero });
        var options = new RelayOptions { PublicHost = "relay.example", SendTimeout = SendTimeout };
        return (new RelaySocket(ws, options, clock ?? time), stream, ws, time);
    }

    /// <summary>Reads never complete; each write waits until the test completes it.</summary>
    private sealed class StallingStream : Stream
    {
        private readonly Lock _gate = new();
        private TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _write;

        public Task WriteStartedAsync() => _started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        public void CompleteWrite()
        {
            TaskCompletionSource write;
            lock (_gate)
            {
                write = _write!;
                _write = null;
                _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            write.SetResult();
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var write = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource started;
            lock (_gate)
            {
                _write = write;
                started = _started;
            }

            started.TrySetResult();
            await write.Task.WaitAsync(cancellationToken);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override bool CanRead => true;

        public override bool CanWrite => true;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        /// <summary>Like a network stream: an abort (the WebSocket disposes its stream) fails the pending write.</summary>
        protected override void Dispose(bool disposing)
        {
            TaskCompletionSource? write;
            lock (_gate)
            {
                write = _write;
                _write = null;
            }

            write?.TrySetException(new ObjectDisposedException(nameof(StallingStream)));
            base.Dispose(disposing);
        }
    }
}
