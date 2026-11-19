using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Panda.Bridge.Auth;
using Panda.Bridge.Protocol;

namespace Panda.Relay.Conformance;

/// <summary>The load report never runs beside another test (its numbers would measure the neighbours).</summary>
[CollectionDefinition(nameof(LoadReportGroup), DisableParallelization = true)]
public sealed class LoadReportGroup;

/// <summary>
/// plan-relay-conformance D6 (seam S4): an explicit, ungated load report — run with <c>--explicit only</c>. Each row
/// starts a NEW relay as shipped (no runtime override), drives one fixed scenario and appends one JSON line to
/// <c>PANDA_LOAD_REPORT</c> (default <c>load-report.jsonl</c> beside the test assembly). Everything named relay* is
/// measured on the relay's PID over the window only. <c>PANDA_RELAY_AFFINITY=n</c> starts the relay on n cores.
/// </summary>
[Collection(nameof(LoadReportGroup))]
public class LoadReportTests
{
    public const string AffinityVariable = "PANDA_RELAY_AFFINITY";
    public const string ReportVariable = "PANDA_LOAD_REPORT";

    private const int InFlight = 16; // pipelined: messages each stream keeps in flight
    private const int IdleChromes = 1000;
    private const double Mb = 1_000_000; // S4: MB = 10^6 bytes

    private static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(20); // the relay closes a control socket idle 60 s
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Ceiling = TimeSpan.FromMinutes(5); // a stuck scenario fails, never hangs the run

    private static readonly Scenario[] All =
    [
        new("echo-1x1KiB", 1, 1024, "lockstep"),
        new("echo-100x1KiB", 100, 1024, "lockstep"),
        new("echo-100x100KiB", 100, 100 * 1024, "lockstep"),
        new("pipelined-100x1KiB", 100, 1024, "pipelined"),
        new("idle-1000", IdleChromes, 0, "idle"),
    ];

    public static TheoryData<string, int> Runs
    {
        get
        {
            var rows = new TheoryData<string, int>();
            foreach (var scenario in All)
            {
                for (var run = 1; run <= 3; run++)
                {
                    rows.Add(scenario.Name, run);
                }
            }

            return rows;
        }
    }

    [Theory(Explicit = true)]
    [MemberData(nameof(Runs))]
    public async Task Load_report(string scenario, int run)
    {
        var s = All.Single(x => x.Name == scenario);
        List<string> flags = ["--bytes-per-day", "1000000000000", "--streams-per-day", "1000000"];
        if (s.Mode == "idle")
        {
            flags.AddRange(["--trusted-proxy", "127.0.0.1"]); // 1 000 registrations from 198.18.0.0/15, one per address
        }

        var cores = Affinity();
        var relay = await StartAsync(flags, cores);
        (string, object?)[] measured;
        bool? gcServer;
        try
        {
            using var ceiling = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            ceiling.CancelAfter(Ceiling);
            measured = s.Mode == "idle" ? await IdleAsync(relay, ceiling.Token) : await EchoAsync(relay, s, ceiling.Token);
            gcServer = GcServer(relay);
        }
        finally
        {
            // stopped BEFORE the line is written: its G6 check or an exit on its own fails the run with no line (S4,
            // slice critic #3)
            await relay.DisposeAsync();
        }

        var line = Json(
        [
            ("label", RelayUnderTest.Label),
            ("cmd", RelayUnderTest.Cmd is null ? "dotnet panda-relay.dll" : Path.GetFileName(RelayUnderTest.Cmd)), // no path: no user or host name
            ("scenario", s.Name),
            ("streams", s.Streams),
            ("size", s.Size),
            ("mode", s.Mode),
            .. measured,
            ("cores", Environment.ProcessorCount),
            ("affinity", cores),
            ("processorCountEnv", ProcessorCountEnv()),
            ("os", RuntimeInformation.OSDescription),
            ("cpu", CpuName()),
            ("gcServer", gcServer),
            ("run", run),
        ]);
        var file = Environment.GetEnvironmentVariable(ReportVariable) is { Length: > 0 } path ? path : Path.Combine(AppContext.BaseDirectory, "load-report.jsonl");
        await File.AppendAllTextAsync(file, line + "\n", TestContext.Current.CancellationToken);
        TestContext.Current.SendDiagnosticMessage($"load report → {file}: {line}");
    }

    // ------------------------------------------------------------------ the relay

    /// <summary>
    /// <paramref name="cores"/>: the harness sets ITS OWN affinity before the start (a child inherits it, so the runtime
    /// sizes its GC / thread pool for n cores — critic #25), restores it after, and checks the child's (critic #41).
    /// </summary>
    private static async Task<RelayProcess> StartAsync(IEnumerable<string> flags, int? cores)
    {
        if (cores is null)
        {
            return await RelayProcess.StartAsync(flags, Log, asShipped: true);
        }

        // Windows only: a process's affinity mask is inherited by its children. On Linux .NET's setter pins only the
        // main thread (sched_setaffinity(pid)) while the child inherits the forking thread's mask (slice critic #2;
        // UNVERIFIED there, so refused rather than guessed) - on Linux run the whole test host under taskset instead.
        if (OperatingSystem.IsWindows())
        {
            return await StartPinnedAsync(flags, cores.Value);
        }

        throw new PlatformNotSupportedException($"{AffinityVariable} works on Windows only (Linux: run dotnet test under taskset)");
    }

    [SupportedOSPlatform("windows")]
    private static async Task<RelayProcess> StartPinnedAsync(IEnumerable<string> flags, int cores)
    {
        using var self = Process.GetCurrentProcess();
        var saved = self.ProcessorAffinity;
        self.ProcessorAffinity = (nint)((1L << cores) - 1);
        RelayProcess relay;
        try
        {
            relay = await RelayProcess.StartAsync(flags, Log, asShipped: true);
        }
        finally
        {
            self.ProcessorAffinity = saved;
        }

        var got = BitOperations.PopCount((ulong)(long)relay.Process.ProcessorAffinity);
        if (got != cores)
        {
            await relay.DisposeAsync();
            throw new InvalidOperationException($"the relay runs on {got} cores, not the {cores} of {AffinityVariable}");
        }

        return relay;
    }

    private static void Log(string line) => TestContext.Current.SendDiagnosticMessage(line);

    private static int? Affinity()
    {
        if (Environment.GetEnvironmentVariable(AffinityVariable) is not { Length: > 0 } text)
        {
            return null;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= Math.Min(Environment.ProcessorCount, 63)
            ? n
            : throw new InvalidOperationException($"{AffinityVariable} must be a core count from 1 to {Math.Min(Environment.ProcessorCount, 63)}");
    }

    // ------------------------------------------------------------------ scenarios

    /// <summary>Streams of echo: each dialler sends, the extension side sends the same bytes back.</summary>
    private static async Task<(string, object?)[]> EchoAsync(RelayProcess relay, Scenario s, CancellationToken ceiling)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ceiling);
        var chromes = new List<RelayClient.Online>();
        var streams = new List<(ClientWebSocket Dial, ClientWebSocket Ext)>();
        var controls = new ConcurrentQueue<WebSocket>();
        var pinger = PingAsync(controls, stop.Token);
        var loops = new List<Task>();
        try
        {
            for (var i = 0; i < s.Streams; i++) // one Chrome per stream: one slot per kind
            {
                var chrome = await relay.OnlineAsync();
                chromes.Add(chrome);
                streams.Add(await relay.OpenStreamAsync(chrome, "link", chrome.Link.Token));
                controls.Enqueue(chrome.Chrome.Control); // pinged only now: no PONG can meet IncomingAsync
            }

            var meter = new Meter();
            var payload = RandomNumberGenerator.GetBytes(s.Size);
            foreach (var (dial, ext) in streams)
            {
                loops.Add(EchoServerAsync(ext, s.Size, stop.Token));
                loops.Add(s.Mode == "pipelined" ? PipelinedAsync(dial, payload, meter, stop.Token) : LockstepAsync(dial, payload, meter, stop.Token));
            }

            await Task.Delay(WarmUp, stop.Token);
            var window = await MeasureAsync(relay, meter, stop.Token);
            await StillRunningAsync([pinger, .. loops]); // a stream that died in the window fails the run (slice critic #1)
            await stop.CancelAsync();
            await Task.WhenAll(loops.Select(QuietAsync));
            if (meter.Silent() is > 0 and var silent)
            {
                // a stream that stalled without ending (a frame dropped) would only lower the count (slice critic round 2)
                throw new InvalidOperationException($"{silent} of {s.Streams} streams completed no round trip in the window");
            }

            var rtts = meter.Rtts();
            var messages = rtts.Length;
            Array.Sort(rtts);
            return
            [
                ("seconds", Round(window.Seconds)),
                ("messages", messages),
                ("msgPerSec", Round(messages / window.Seconds)),
                ("mbPerSec", Round(messages * (double)s.Size * 2 / Mb / window.Seconds)), // both directions through the relay
                ("rttP50Ms", messages == 0 ? null : Round(Percentile(rtts, 0.50))),
                ("rttP99Ms", messages == 0 ? null : Round(Percentile(rtts, 0.99))),
                ("rttMaxMs", messages == 0 ? null : Round(rtts[^1])),
                ("relayCpuSec", Round(window.RelayCpu.TotalSeconds)),
                ("relayCpuUsPerMsg", messages == 0 ? null : Round(window.RelayCpu.TotalMicroseconds / (2.0 * messages))), // per forwarded message
                ("relayWsStartMb", Round(window.WsStart / Mb)),
                ("relayWsEndMb", Round(window.WsEnd / Mb)),
                ("relayWsPeakMb", Round(window.WsPeak / Mb)),
                ("bytesPerConn", null),
                ("harnessCpuSec", Round(window.HarnessCpu.TotalSeconds)),
            ];
        }
        finally
        {
            await stop.CancelAsync();
            await QuietAsync(pinger);
            await Task.WhenAll(loops.Select(QuietAsync));
            foreach (var (dial, ext) in streams)
            {
                dial.Dispose();
                ext.Dispose();
            }

            foreach (var chrome in chromes)
            {
                await chrome.DisposeAsync();
            }
        }
    }

    /// <summary>1 000 Chromes register (each from its own forwarded address) and stay idle: the relay's memory per connection.</summary>
    private static async Task<(string, object?)[]> IdleAsync(RelayProcess relay, CancellationToken ceiling)
    {
        // no pinger here: the scenario (registrations + Settle, ~6 s) is far shorter than the 60 s idle close, and an
        // unread PONG from it would answer the check below for a socket closed since (slice critic round 3)
        var controls = new ConcurrentQueue<WebSocket>();
        using var sampler = new Sampler(relay.Process);
        using var self = Process.GetCurrentProcess();
        var relayCpu = relay.Process.TotalProcessorTime;
        var harnessCpu = self.TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        await Parallel.ForEachAsync(Enumerable.Range(0, IdleChromes), new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ceiling }, async (i, _) =>
        {
            var chrome = new FakeChrome(relay, ECDsa.Create(ECCurve.NamedCurves.nistP256));
            try
            {
                var ws = await relay.RawUpgradeAsync(RelayClient.ExtOrigin, "/v1/browser", $"X-Forwarded-For: 198.18.{i / 250}.{(i % 250) + 1}");
                var challenge = Assert.IsType<RelayChallenge>(await Wire.ReceiveAsync(ws));
                Assert.True(AuthCrypto.TryDecodeBase64Url(challenge.Nonce, 32, out var nonce));
                await Wire.SendAsync(ws, chrome.Register(nonce));
                Assert.IsType<RelayRegistered>(await Wire.ReceiveAsync(ws));
                controls.Enqueue(ws);
            }
            finally
            {
                await chrome.DisposeAsync(); // its key only: the socket stays open, the relay process closes it at stop
            }
        });
        var seconds = clock.Elapsed.TotalSeconds;
        relayCpu = relay.Process.TotalProcessorTime - relayCpu;
        harnessCpu = self.TotalProcessorTime - harnessCpu;
        await Task.Delay(Settle, ceiling);
        var (start, end, peak) = sampler.Stop();

        // every idle Chrome must still be served: a PING answered by a PONG (State alone never changes on a socket
        // nobody reads - slice critic round 2). A relay may ping too (either direction): its PINGs are skipped. After
        // the sampler: not part of the memory figures.
        await Parallel.ForEachAsync(controls, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ceiling }, async (ws, _) =>
        {
            await Wire.SendAsync(ws, new RelayPing());
            IRelayMessage reply;
            do
            {
                reply = await Wire.ReceiveAsync(ws);
            }
            while (reply is RelayPing);

            Assert.IsType<RelayPong>(reply);
        });

        return
        [
            ("seconds", Round(seconds)),
            ("messages", null),
            ("msgPerSec", null),
            ("mbPerSec", null),
            ("rttP50Ms", null),
            ("rttP99Ms", null),
            ("rttMaxMs", null),
            ("relayCpuSec", Round(relayCpu.TotalSeconds)),
            ("relayCpuUsPerMsg", null), // no messages (critic #28)
            ("relayWsStartMb", Round(start / Mb)),
            ("relayWsEndMb", Round(end / Mb)),
            ("relayWsPeakMb", Round(peak / Mb)),
            ("bytesPerConn", (long)Math.Round((end - start) / (double)IdleChromes)),
            ("harnessCpuSec", Round(harnessCpu.TotalSeconds)),
        ];
    }

    // ------------------------------------------------------------------ the loops

    private static async Task LockstepAsync(WebSocket dial, byte[] payload, Meter meter, CancellationToken ct)
    {
        var rtts = meter.NewList();
        var buffer = new byte[payload.Length];
        while (!ct.IsCancellationRequested)
        {
            var sent = Stopwatch.GetTimestamp();
            await dial.SendAsync(payload, WebSocketMessageType.Binary, endOfMessage: true, ct);
            await ReceiveWholeAsync(dial, buffer, ct);
            if (meter.Recording)
            {
                rtts.Add(Stopwatch.GetElapsedTime(sent).TotalMilliseconds);
            }
        }
    }

    private static async Task PipelinedAsync(WebSocket dial, byte[] payload, Meter meter, CancellationToken ct)
    {
        var rtts = meter.NewList();
        using var window = new SemaphoreSlim(InFlight);
        var sentAt = new ConcurrentQueue<long>(); // FIFO: a stream keeps its order
        var receiver = Task.Run(async () =>
        {
            var buffer = new byte[payload.Length];
            while (true)
            {
                await ReceiveWholeAsync(dial, buffer, ct);
                if (!sentAt.TryDequeue(out var sent))
                {
                    throw new InvalidOperationException("an echo arrived for nothing sent");
                }

                if (meter.Recording)
                {
                    rtts.Add(Stopwatch.GetElapsedTime(sent).TotalMilliseconds);
                }

                window.Release();
            }
        }, ct);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var slot = window.WaitAsync(ct);
                if (await Task.WhenAny(slot, receiver) == receiver)
                {
                    await receiver; // its own exception; a receiver that died must end this loop too (slice critic #1)
                    throw new InvalidOperationException("the echo receiver ended");
                }

                await slot;
                sentAt.Enqueue(Stopwatch.GetTimestamp());
                await dial.SendAsync(payload, WebSocketMessageType.Binary, endOfMessage: true, ct);
            }
        }
        finally
        {
            await QuietAsync(receiver);
        }
    }

    private static async Task EchoServerAsync(WebSocket ext, int size, CancellationToken ct)
    {
        var buffer = new byte[size];
        while (!ct.IsCancellationRequested)
        {
            await ReceiveWholeAsync(ext, buffer, ct);
            await ext.SendAsync(buffer, WebSocketMessageType.Binary, endOfMessage: true, ct);
        }
    }

    /// <summary>One whole binary message of exactly <paramref name="buffer"/>'s length.</summary>
    private static async Task ReceiveWholeAsync(WebSocket ws, byte[] buffer, CancellationToken ct)
    {
        var n = 0;
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer.AsMemory(n), ct);
            if (r.MessageType == WebSocketMessageType.Close && ct.IsCancellationRequested)
            {
                // the window ended: the other side was aborted and the relay closed this one 1000 — the scenario's end
                throw new OperationCanceledException(ct);
            }

            if (r.MessageType != WebSocketMessageType.Binary)
            {
                throw new InvalidOperationException($"expected a binary message, got {r.MessageType}");
            }

            n += r.Count;
            if (r.EndOfMessage)
            {
                if (n != buffer.Length)
                {
                    throw new InvalidOperationException($"a message of {n} bytes, not {buffer.Length}");
                }

                return;
            }

            if (n == buffer.Length)
            {
                throw new InvalidOperationException($"a message longer than {buffer.Length} bytes");
            }
        }
    }

    private static async Task PingAsync(ConcurrentQueue<WebSocket> controls, CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(PingEvery, ct);
            foreach (var control in controls)
            {
                await Wire.SendAsync(control, new RelayPing()); // the PONGs stay unread: nothing else reads these sockets now
            }
        }
    }

    /// <summary>
    /// Before the window's end is signalled, every loop must still run: one that ended (a reset socket, a Close from
    /// the relay, an exception) would otherwise be swallowed by <see cref="QuietAsync"/> and the line would read as a
    /// slower relay instead of a broken one. Rethrows the loop's own exception.
    /// </summary>
    private static async Task StillRunningAsync(IEnumerable<Task> loops)
    {
        if (loops.FirstOrDefault(t => t.IsCompleted) is { } ended)
        {
            await ended;
            throw new InvalidOperationException("a load loop ended before the window did");
        }
    }

    /// <summary>Awaits a loop that ends by cancellation or by its socket being aborted at the end of the window.</summary>
    private static async Task QuietAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            // the scenario's end
        }
    }

    // ------------------------------------------------------------------ measuring

    private static async Task<WindowResult> MeasureAsync(RelayProcess relay, Meter meter, CancellationToken ct)
    {
        using var self = Process.GetCurrentProcess();
        using var sampler = new Sampler(relay.Process);
        var relayCpu = relay.Process.TotalProcessorTime;
        var harnessCpu = self.TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        meter.Recording = true;
        await Task.Delay(Window, ct);
        meter.Recording = false;
        var seconds = clock.Elapsed.TotalSeconds;
        relayCpu = relay.Process.TotalProcessorTime - relayCpu;
        harnessCpu = self.TotalProcessorTime - harnessCpu;
        var (start, end, peak) = sampler.Stop();
        return new WindowResult(seconds, relayCpu, harnessCpu, start, end, peak);
    }

    private sealed record WindowResult(double Seconds, TimeSpan RelayCpu, TimeSpan HarnessCpu, long WsStart, long WsEnd, long WsPeak);

    private sealed record Scenario(string Name, int Streams, int Size, string Mode);

    /// <summary>Round trips completed while <see cref="Recording"/> is on; one list per loop (no lock on the hot path).</summary>
    private sealed class Meter
    {
        private readonly ConcurrentBag<List<double>> _lists = [];
        private volatile bool _recording;

        public bool Recording
        {
            get => _recording;
            set => _recording = value;
        }

        public List<double> NewList()
        {
            var list = new List<double>();
            _lists.Add(list);
            return list;
        }

        /// <summary>Streams with no RTT recorded in the window (call after the loops ended).</summary>
        public int Silent() => _lists.Count(l => l.Count == 0);

        /// <summary>Every RTT recorded (call after the loops ended).</summary>
        public double[] Rtts() => [.. _lists.SelectMany(l => l)];
    }

    /// <summary>The relay's working set at start, at stop and its peak in between (sampled every 100 ms).</summary>
    private sealed class Sampler : IDisposable
    {
        private readonly Process _process;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private readonly long _start;
        private long _peak;

        public Sampler(Process process)
        {
            _process = process;
            _start = Read();
            _peak = _start;
            _loop = LoopAsync();
        }

        public (long Start, long End, long Peak) Stop()
        {
            _stop.Cancel();
            _loop.GetAwaiter().GetResult();
            var end = Read();
            return (_start, end, Math.Max(_peak, end));
        }

        public void Dispose()
        {
            _stop.Cancel();
            _stop.Dispose();
        }

        private long Read()
        {
            _process.Refresh();
            return _process.WorkingSet64;
        }

        private async Task LoopAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(100, _stop.Token);
                    _peak = Math.Max(_peak, Read());
                }
            }
            catch (OperationCanceledException)
            {
                // stopped
            }
        }
    }

    // ------------------------------------------------------------------ the line

    private static double Round(double value) => Math.Round(value, 3);

    private static double Percentile(double[] sorted, double p) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)];

    private static string? ProcessorCountEnv()
    {
        foreach (var name in new[] { "DOTNET_PROCESSOR_COUNT", "GOMAXPROCS" })
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                return $"{name}={value}";
            }
        }

        return null;
    }

    /// <summary>What the relay ran with: the override it was given, else the inherited variable, else the C# relay's runtimeconfig.</summary>
    private static bool? GcServer(RelayProcess relay)
    {
        if (RelayUnderTest.Cmd is not null)
        {
            return null; // not the C# relay: unknown, whatever DOTNET_gcServer says (slice critic #6, D6)
        }

        var given = relay.GcServerOverride ?? Environment.GetEnvironmentVariable("DOTNET_gcServer");
        if (given is { Length: > 0 })
        {
            return given != "0";
        }

        var config = Path.ChangeExtension(RelayUnderTest.Resolve().Prefix[0], ".runtimeconfig.json");
        using var json = JsonDocument.Parse(File.ReadAllText(config));
        return json.RootElement.GetProperty("runtimeOptions").TryGetProperty("configProperties", out var props)
               && props.TryGetProperty("System.GC.Server", out var server) && server.GetBoolean();
    }

    private static string? CpuName()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim();
        }

        if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
        {
            var model = File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal));
            return model?[(model.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
        }

        return null;
    }

    /// <summary>One JSON object, fields in the order given, invariant numbers (S4).</summary>
    private static string Json((string Name, object? Value)[] fields)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in fields)
            {
                switch (value)
                {
                    case null: writer.WriteNull(name); break;
                    case string text: writer.WriteString(name, text); break;
                    case bool flag: writer.WriteBoolean(name, flag); break;
                    case int number: writer.WriteNumber(name, number); break;
                    case long number: writer.WriteNumber(name, number); break;
                    case double number: writer.WriteNumber(name, number); break;
                    default: throw new InvalidOperationException($"no JSON form for {name}");
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
