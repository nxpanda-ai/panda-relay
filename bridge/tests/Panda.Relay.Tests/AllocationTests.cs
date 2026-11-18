using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO.Pipelines;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Panda.Bridge.Protocol;

namespace Panda.Relay.Tests;

[CollectionDefinition(nameof(AllocationMeasure), DisableParallelization = true)]
public sealed class AllocationMeasure;

/// <summary>
/// plan-zero-alloc-relay D1: bytes allocated per forwarded message by the relay's pump, against a bare pump on the same
/// in-memory WebSockets (the floor F). Lockstep traffic and always-async writes, so every receive and send suspends; the
/// counter is process-wide, so these tests run alone and gate per message.
/// </summary>
[Collection(nameof(AllocationMeasure))]
public class AllocationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(1024, 20_000)]
    [InlineData(100 * 1024, 2_000)]
    public async Task Bytes_allocated_per_forwarded_message_are_measured(int size, int roundTrips)
    {
        var result = await Task.Run(() => MeasureAsync(size, roundTrips), Ct);

        TestContext.Current.TestOutputHelper?.WriteLine(result.ToString());
        Assert.True(result.RelaySuspended, $"the relay's sockets did not suspend on every message: {result}");
        Assert.True(result.ControlSuspended, $"the control's sockets did not suspend on every message: {result}");
        Assert.True(result.Ticks > 0, "the AllocationTick listener saw nothing: the type gate would be vacuous");
        Assert.True(result.PandaTypes.Count == 0, $"Panda code allocated per message: {string.Join(", ", result.PandaTypes)}");
        var gate = Optimized ? Gate : DebugGate(size);
        Assert.True(result.PandaPerMessage < gate, $"not under the gate {gate} B/msg ({(Optimized ? "optimized" : "Debug")}): {result}");
    }

    /// <summary>
    /// The routine Debug run's numeric gate (diff critic R3): v1.4 measured −5 … 25 B/msg at 1 KiB and ~763 at 100 KiB
    /// (the harness's Debug state machines). Still fails a per-send CancellationTokenSource (~300 B), a payload-sized
    /// array or a per-message 16 KiB buffer coming back.
    /// </summary>
    private static double DebugGate(int size) => size <= 1024 ? 128 : 2048;

    /// <summary>
    /// The goal (plan-zero-alloc-relay R3, v1.4): nothing allocated per forwarded message in Panda code. Gates: (1) in
    /// every build, a separate relay pass under GC AllocationTick samples no Panda.Relay type — reliable at 1 KiB (a
    /// 24 B Panda-typed object per message ≈ 10 samples, miss ≈ e^-10), not at 100 KiB (≈ 1 sample); a per-message
    /// object does not depend on the size, so the 1 KiB case is the one that counts; it cannot see framework types
    /// (byte[], a CTS); (2) numeric: optimized build (relay − F) / N &lt; 24 B/msg — with the ±15 B/msg noise of both
    /// signs (the harness's and the framework's pooled-box misses) it reliably catches ≥ ~40 B/msg, not one 24 B
    /// object alone; Debug build <see cref="DebugGate"/>, which catches the R1 / R2 regressions.
    /// </summary>
    private const double Gate = 24;

    private static bool Optimized =>
        typeof(RelaySocket).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false) is not [DebuggableAttribute { IsJITOptimizerDisabled: true }];

    internal sealed record Measurement(int Size, int Messages, double FloorPerMessage, double RelayPerMessage, bool ControlSuspended,
        bool RelaySuspended, int Ticks, IReadOnlyList<string> PandaTypes)
    {
        public double PandaPerMessage => RelayPerMessage - FloorPerMessage;

        public override string ToString() =>
            $"size {Size} B, {Messages} messages: floor {FloorPerMessage:F1} B/msg, relay {RelayPerMessage:F1} B/msg, Panda {PandaPerMessage:F1} B/msg";
    }

    internal static async Task<Measurement> MeasureAsync(int size, int roundTrips)
    {
        var payload = new byte[size];
        Random.Shared.NextBytes(payload);
        await using var control = Rig.Control(size);
        await using var relay = Rig.Relay(size);

        // warm-up by time: past tier-0, so tiering does not move the number (critic F4)
        var warm = Stopwatch.StartNew();
        while (warm.Elapsed < TimeSpan.FromSeconds(2))
        {
            await control.RoundTripsAsync(payload, 200);
            await relay.RoundTripsAsync(payload, 200);
        }

        long floor = long.MaxValue, measured = long.MaxValue;
        for (var round = 0; round < 3; round++) // interleaved, the minimum of each (critic F4)
        {
            floor = Math.Min(floor, await AllocatedAsync(control, payload, roundTrips));
            measured = Math.Min(measured, await AllocatedAsync(relay, payload, roundTrips));
        }

        var messages = roundTrips * 2;
        var controlSuspended = control.SuspendedOnEveryMessage();
        var relaySuspended = relay.SuspendedOnEveryMessage();

        // its own pass: the listener allocates for every event it gets
        int ticks;
        IReadOnlyList<string> pandaTypes;
        using (var listener = new AllocationTicks())
        {
            await relay.RoundTripsAsync(payload, roundTrips);
            (ticks, pandaTypes) = listener.PandaTypes();
        }

        return new Measurement(size, messages, (double)floor / messages, (double)measured / messages, controlSuspended, relaySuspended,
            ticks, pandaTypes);
    }

    /// <summary>
    /// Records the type of every GC AllocationTick sample (one per ~100 KB allocated). A Panda.Relay type is a production
    /// type by its full name (a Release state-machine box names it in its generic arguments), or a production
    /// compiler-generated nested type (a Debug state machine is named only by its short name) that the test assembly
    /// does not also define.
    /// </summary>
    private sealed class AllocationTicks : EventListener
    {
        private readonly Lock _gate = new();
        private readonly HashSet<string> _types = [];
        private int _ticks;

        public (int Ticks, IReadOnlyList<string> PandaTypes) PandaTypes()
        {
            var tests = Generated(typeof(AllocationTests).Assembly);
            var production = Generated(typeof(RelaySocket).Assembly);
            production.ExceptWith(tests);
            lock (_gate)
            {
                return (_ticks, [.. _types.Where(t => t.Replace("Panda.Relay.Tests.", "", StringComparison.Ordinal).Contains("Panda.Relay.", StringComparison.Ordinal)
                    || production.Contains(t))]);
            }
        }

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
            {
                EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)0x1); // GC keyword: AllocationTick
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventName?.StartsWith("GCAllocationTick", StringComparison.Ordinal) != true
                || eventData.PayloadNames?.IndexOf("TypeName") is not (>= 0 and var index)
                || eventData.Payload?[index] is not string type)
            {
                return;
            }

            lock (_gate)
            {
                _ticks++;
                _types.Add(type);
            }
        }

        private static HashSet<string> Generated(System.Reflection.Assembly assembly) =>
            [.. assembly.GetTypes().Where(t => t.IsNested && t.Name.StartsWith('<')).Select(t => t.Name)];
    }

    private static async Task<long> AllocatedAsync(Rig rig, byte[] payload, int roundTrips)
    {
        rig.ResetCounters();
        var before = GC.GetTotalAllocatedBytes(precise: true);
        await rig.RoundTripsAsync(payload, roundTrips);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        rig.CountedMessages = roundTrips * 2;
        return allocated;
    }

    /// <summary>
    /// Two clients (A, B), each joined to a server-side WebSocket over an in-memory duplex stream; a pump between the two
    /// server sides. A sends, B receives, B sends, A receives: one message in flight at a time (lockstep).
    /// </summary>
    internal sealed class Rig : IAsyncDisposable
    {
        private readonly WebSocket _clientA;
        private readonly WebSocket _clientB;
        private readonly HarnessStream _serverStreamA;
        private readonly HarnessStream _serverStreamB;
        private readonly byte[] _receiveBuffer;
        private readonly Task _pump;
        private readonly Action _stop;

        private Rig(int size, Func<WebSocket, WebSocket, (Task Pump, Action Stop)> pump)
        {
            (_clientA, var serverA, _serverStreamA) = Pair();
            (_clientB, var serverB, _serverStreamB) = Pair();
            _receiveBuffer = new byte[size];
            (_pump, _stop) = pump(serverA, serverB);
        }

        public int CountedMessages { get; set; }

        public WebSocket ClientA => _clientA;

        public WebSocket ClientB => _clientB;

        /// <summary>The floor: a bare loop on WebSocket with one fixed buffer, one send per whole message.</summary>
        public static Rig Control(int size) => new(size, (a, b) =>
        {
            var up = BarePumpAsync(a, b, new byte[size]);
            var down = BarePumpAsync(b, a, new byte[size]);
            return (Task.WhenAll(up, down), () =>
            {
                a.Abort();
                b.Abort();
            });
        });

        /// <summary>The relay's own pump (RelayHub.PumpAsync over RelaySocket), one open link stream.</summary>
        public static Rig Relay(int size, RelayOptions? relayOptions = null) => new(size, (a, b) =>
        {
            var options = relayOptions ?? new RelayOptions { PublicHost = "relay.example", BytesPerDay = long.MaxValue / 4 };
            var hub = new RelayHub(options, TimeProvider.System, NullLogger<RelayHub>.Instance,
                new HostedAdmin(options, NullLogger<HostedAdmin>.Instance));
            var dialer = new RelaySocket(a, options, TimeProvider.System);
            var ext = new RelaySocket(b, options, TimeProvider.System);
            var link = new BrowserLink(new string('R', 22), new RelaySocket(WebSocket.CreateFromStream(Stream.Null,
                new WebSocketCreationOptions { IsServer = true }), options, TimeProvider.System), null, null);
            var stream = new RelayStream("S", link, RelayStreamKind.Link, dialer) { Ext = ext, State = StreamState.Open };
            link.Slots[RelayStreamKind.Link] = stream;
            var pumps = Task.WhenAll(hub.PumpAsync(stream, dialer, ext, null, up: true), hub.PumpAsync(stream, ext, dialer, null, up: false));
            return (pumps, () =>
            {
                dialer.Abort();
                ext.Abort();
            });
        });

        public async Task RoundTripsAsync(byte[] payload, int count)
        {
            for (var i = 0; i < count; i++)
            {
                await _clientA.SendAsync(payload, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
                await ReceiveWholeAsync(_clientB);
                await _clientB.SendAsync(payload, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
                await ReceiveWholeAsync(_clientA);
            }
        }

        public void ResetCounters()
        {
            _serverStreamA.Reset();
            _serverStreamB.Reset();
        }

        /// <summary>
        /// Each message crosses one server read and one server write on each side; ≥ 99 % of them must have gone
        /// pending, so the measured sockets suspended (critic F3, round 2 N3, round 3 A3).
        /// </summary>
        public bool SuspendedOnEveryMessage()
        {
            var perSide = CountedMessages / 2 * 99 / 100;
            return _serverStreamA.PendingReads >= perSide && _serverStreamB.PendingReads >= perSide
                && _serverStreamA.PendingWrites >= perSide && _serverStreamB.PendingWrites >= perSide;
        }

        private async Task ReceiveWholeAsync(WebSocket socket)
        {
            var count = 0;
            while (true)
            {
                var result = await socket.ReceiveAsync(_receiveBuffer.AsMemory(count), CancellationToken.None);
                count += result.Count;
                if (result.EndOfMessage)
                {
                    Assert.Equal(_receiveBuffer.Length, count);
                    return;
                }
            }
        }

        private static async Task BarePumpAsync(WebSocket from, WebSocket to, byte[] buffer)
        {
            try
            {
                while (true)
                {
                    var count = 0;
                    ValueWebSocketReceiveResult result;
                    do
                    {
                        result = await from.ReceiveAsync(buffer.AsMemory(count), CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            return;
                        }

                        count += result.Count;
                    }
                    while (!result.EndOfMessage);

                    await to.SendAsync(buffer.AsMemory(0, count), WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
                }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // aborted at the end of the test
            }
        }

        internal static (WebSocket Client, WebSocket Server, HarnessStream ServerStream) Pair()
        {
            var toServer = new Pipe(new PipeOptions(pauseWriterThreshold: 0, useSynchronizationContext: false));
            var toClient = new Pipe(new PipeOptions(pauseWriterThreshold: 0, useSynchronizationContext: false));
            var serverStream = new HarnessStream(toServer.Reader, toClient.Writer);
            var clientStream = new HarnessStream(toClient.Reader, toServer.Writer);
            var server = WebSocket.CreateFromStream(serverStream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.Zero });
            var client = WebSocket.CreateFromStream(clientStream, new WebSocketCreationOptions { IsServer = false, KeepAliveInterval = TimeSpan.Zero });
            return (client, server, serverStream);
        }

        public async ValueTask DisposeAsync()
        {
            _stop();
            _clientA.Abort();
            _clientB.Abort();
            try
            {
                await _pump.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // the pump ended on the abort
            }
        }
    }

    /// <summary>
    /// One side of an in-memory duplex: reads from one pipe, writes to the other. Every write completes asynchronously
    /// (round 2 N3) and pending reads / writes are counted (round 3 A3). One reader and one writer at a time.
    /// </summary>
    internal sealed class HarnessStream(PipeReader reader, PipeWriter writer) : Stream
    {
        public int PendingReads { get; private set; }

        public int PendingWrites { get; private set; }

        public void Reset() => (PendingReads, PendingWrites) = (0, 0);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var pending = reader.ReadAsync(cancellationToken);
            if (!pending.IsCompleted)
            {
                PendingReads++;
            }

            var result = await pending;
            var available = result.Buffer;
            if (available.IsEmpty && result.IsCompleted)
            {
                reader.AdvanceTo(available.End);
                return 0;
            }

            var take = (int)Math.Min(available.Length, buffer.Length);
            available.Slice(0, take).CopyTo(buffer.Span);
            reader.AdvanceTo(available.GetPosition(take));
            return take;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            PendingWrites++;
            await writer.WriteAsync(buffer, cancellationToken);
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

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                reader.Complete();
                writer.Complete();
            }

            base.Dispose(disposing);
        }
    }
}
