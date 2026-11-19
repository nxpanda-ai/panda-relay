using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using Panda.TestSupport;
using Xunit.Sdk;

namespace Panda.Relay.Conformance;

/// <summary>
/// plan-relay-conformance D2 (seam S1): the relay under test as a separate process, reached only over HTTP / WebSocket.
/// <list type="bullet">
/// <item>argv: <c>--public-host r&lt;8 hex&gt;.relay.test:443 --listen 127.0.0.1 --port &lt;p&gt;</c> + the test's flags. The host is
/// unique per process, so a register signed for it succeeds only on THIS process (the ready probe).</item>
/// <item>ready = alive, a probe Chrome registers, still alive (30 s); an early exit restarts on a new port ≤ 2 times.</item>
/// <item>stop = close the sockets the helpers opened, wait for quiet output, kill, wait for exit, read the output to EOF,
/// then fail if any secret used against it appears there (G6).</item>
/// </list>
/// </summary>
internal sealed class RelayProcess : IRelayEndpoint, IAsyncDisposable
{
    public static readonly TimeSpan ReadyLimit = TimeSpan.FromSeconds(30);
    private const int Starts = 3; // the first start + at most 2 restarts on a new port (critic #30)

    private readonly Process _process;
    private readonly List<string> _output = [];
    private readonly ConcurrentQueue<WebSocket> _sockets = new();
    private readonly ConcurrentDictionary<string, byte> _secrets = new(StringComparer.Ordinal);
    private long _lastOutput = Stopwatch.GetTimestamp();
    private int _disposed;

    private RelayProcess(Process process, int port, string publicHost)
    {
        _process = process;
        Port = port;
        PublicHost = publicHost;
        _process.OutputDataReceived += (_, e) => OnLine(e.Data);
        _process.ErrorDataReceived += (_, e) => OnLine(e.Data);
    }

    /// <summary>A flag value replaced by a free port picked at each start (e.g. <c>--internal-port</c>); read it back with <see cref="InternalPort"/>.</summary>
    public const string SecondPort = "{second-port}";

    /// <summary>Replaced in any flag by this process's public host name, without its port (e.g. a hosted MCP URL).</summary>
    public const string PublicHostName = "{public-host-name}";

    public int Port { get; }

    /// <summary>The port that replaced <see cref="SecondPort"/> in this process's flags.</summary>
    public int InternalPort => SecondPortValue ?? throw new InvalidOperationException("the relay was started without a second port");

    private int? SecondPortValue { get; init; }

    public string PublicHost { get; }

    public Uri Ws => new($"ws://127.0.0.1:{Port}");

    public Uri Http => new($"http://127.0.0.1:{Port}");

    /// <summary>The relay's PID (the measured process: never a wrapper, RelayUnderTest refuses scripts).</summary>
    public Process Process => _process;

    /// <summary>Every stdout / stderr line so far.</summary>
    public IReadOnlyList<string> Output
    {
        get
        {
            lock (_output)
            {
                return [.. _output];
            }
        }
    }

    public void Opened(WebSocket socket) => _sockets.Enqueue(socket);

    public void Secret(string value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            _secrets.TryAdd(value, 0);
        }
    }

    /// <summary>
    /// Starts a relay with <paramref name="flags"/> and waits until it is ready; <paramref name="log"/> hears the restarts.
    /// <paramref name="asShipped"/>: no runtime override (the load report, D6) — else workstation GC (see NewProcess).
    /// </summary>
    public static async Task<RelayProcess> StartAsync(IEnumerable<string>? flags = null, Action<string>? log = null, bool asShipped = false)
    {
        var ct = TestContext.Current.CancellationToken;
        var early = new List<string>();
        for (var start = 1; start <= Starts; start++)
        {
            var relay = Launch(flags ?? [], asShipped);
            switch (await relay.WaitReadyAsync(ct))
            {
                case Ready.Yes:
                    return relay;
                case Ready.ExitedEarly:
                    var exit = relay._process.ExitCode;
                    await relay.StopAsync(checkSecrets: false);
                    early.Add($"start {start} on port {relay.Port}: exit {exit}: {string.Join(" | ", relay.Output)}");
                    log?.Invoke($"relay exited before it was ready (restarting on a new port): {early[^1]}");
                    continue;
                default:
                    await relay.StopAsync(checkSecrets: false);
                    throw new TimeoutException($"{RelayUnderTest.Describe()} was not ready within {ReadyLimit.TotalSeconds:0} s on port {relay.Port}: {string.Join(" | ", relay.Output)}");
            }
        }

        throw new InvalidOperationException($"{RelayUnderTest.Describe()} exited before it was ready {Starts} times:\n{string.Join("\n", early)}");
    }

    /// <summary>Runs the executable with exactly <paramref name="args"/> (G5) until it exits: its exit code and output.</summary>
    public static async Task<(int ExitCode, IReadOnlyList<string> Output)> RunToExitAsync(IEnumerable<string> args, TimeSpan limit)
    {
        var (file, prefix) = RelayUnderTest.Resolve();
        using var process = NewProcess(file, [.. prefix, .. args]);
        var lines = new List<string>();
        void Add(string? line)
        {
            if (line is not null)
            {
                lock (lines)
                {
                    lines.Add(line);
                }
            }
        }

        process.OutputDataReceived += (_, e) => Add(e.Data);
        process.ErrorDataReceived += (_, e) => Add(e.Data);
        Begin(process);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(limit);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw new TimeoutException($"the relay did not exit within {limit.TotalSeconds:0} s");
        }

        process.WaitForExit(); // the redirected output to EOF
        lock (lines)
        {
            return (process.ExitCode, [.. lines]);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(checkSecrets: true);

    private static RelayProcess Launch(IEnumerable<string> flags, bool asShipped)
    {
        var (file, prefix) = RelayUnderTest.Resolve();
        var port = TestPorts.Next();
        int? second = null;
        var name = $"r{RandomNumberGenerator.GetHexString(8, lowercase: true)}.relay.test";
        var host = name + ":443";
        var args = flags.Select(f =>
        {
            if (f != SecondPort)
            {
                return f.Replace(PublicHostName, name, StringComparison.Ordinal);
            }

            second ??= TestPorts.Next(); // picked again at every start, like the public port (critic T2-T5 #8)
            return second.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }).ToList();
        var process = NewProcess(file, [.. prefix, "--public-host", host, "--listen", "127.0.0.1", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), .. args], asShipped);
        var relay = new RelayProcess(process, port, host) { SecondPortValue = second, GcServerOverride = asShipped ? null : "0" };
        Begin(process);
        return relay;
    }

    /// <summary>The <c>DOTNET_gcServer</c> value this process was given (null = none: the relay runs as shipped) — what D6 reports.</summary>
    public string? GcServerOverride { get; private init; }

    private static Process NewProcess(string file, IEnumerable<string> args, bool asShipped = false)
    {
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        // Test environment, not contract (plan D2): up to ~30 relays live together. The C# relay ships with server GC
        // (a GC thread per core, 48 here); that many stalled relays > 10 s when they ran beside the solution's other
        // suites (UNVERIFIED cause, plan Status). Workstation GC for the suite; the load report passes asShipped (D6).
        // A .NET 9+ environment variable wins over runtimeconfig.json; a non-.NET relay ignores it.
        if (!asShipped)
        {
            info.Environment["DOTNET_gcServer"] = "0";
        }

        return new Process { StartInfo = info };
    }

    private static void Begin(Process process)
    {
        if (!process.Start())
        {
            throw new InvalidOperationException($"could not start {process.StartInfo.FileName}");
        }

        try
        {
            ChildJob.Add(process);
        }
        catch (Win32Exception)
        {
            process.Kill(entireProcessTree: true); // never a relay outside the job (AC5)
            throw;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    private enum Ready
    {
        Yes,
        ExitedEarly,
        TimedOut,
    }

    private async Task<Ready> WaitReadyAsync(CancellationToken ct)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(ReadyLimit.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_process.HasExited)
            {
                return Ready.ExitedEarly;
            }

            if (await ProbeAsync(ct))
            {
                // a relay that lost its port to another process exits; the probe may have reached that other one —
                // but a register signed for THIS host succeeds only here, so alive + registered = this process serves
                return _process.HasExited ? Ready.ExitedEarly : Ready.Yes;
            }

            await Task.Delay(100, ct);
        }

        return _process.HasExited ? Ready.ExitedEarly : Ready.TimedOut;
    }

    /// <summary>One throwaway Chrome signed for this process's unique host: true when it registers.</summary>
    private async Task<bool> ProbeAsync(CancellationToken ct)
    {
        try
        {
            await using var probe = await this.ChromeAsync();
            await Wire.CloseAsync(probe.Control, (int)WebSocketCloseStatus.NormalClosure);
            return true;
        }
        catch (WebSocketException)
        {
            return false; // nothing listens yet (refused), or the upgrade failed
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false; // a connect or receive timed out
        }
        catch (XunitException)
        {
            return false; // an answer that is not a registration: not this relay (yet)
        }
    }

    private void OnLine(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_output)
        {
            _output.Add(line);
        }

        Interlocked.Exchange(ref _lastOutput, Stopwatch.GetTimestamp());
    }

    private async Task StopAsync(bool checkSecrets)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await CloseSocketsAsync();
        await QuietAsync();
        var exitedOnItsOwn = _process.HasExited;
        try
        {
            if (!exitedOnItsOwn)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // it exited between the check and the kill
        }

        using (var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            await _process.WaitForExitAsync(limit.Token);
        }

        _process.WaitForExit(); // the redirected output to EOF
        var exitCode = _process.ExitCode;
        _process.Dispose();
        if (checkSecrets)
        {
            CheckSecrets();
            if (exitedOnItsOwn)
            {
                // a relay that died during its tests (never its output here: it may hold a secret — G6)
                throw new XunitException($"{RelayUnderTest.Describe()} exited on its own (code {exitCode}) before its tests ended");
            }
        }
    }

    /// <summary>Normal closes, so a relay that would log a secret when a socket closes gets to write that line (critic #31).</summary>
    private async Task CloseSocketsAsync()
    {
        while (_sockets.TryDequeue(out var ws))
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, limit.Token);
                }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException)
            {
                // already gone: nothing left to close
            }
            finally
            {
                ws.Dispose();
            }
        }
    }

    /// <summary>Waits until no output line came for 500 ms after the closes (at most 3 s): the relay's last log lines get written.</summary>
    private async Task QuietAsync()
    {
        Interlocked.Exchange(ref _lastOutput, Stopwatch.GetTimestamp()); // quiet is measured from now: the closes just went out
        var until = Stopwatch.GetTimestamp() + (3 * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < until && !_process.HasExited
               && Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastOutput)) < TimeSpan.FromMilliseconds(500))
        {
            await Task.Delay(50, CancellationToken.None);
        }
    }

    /// <summary>G6 / AC6: no secret used against this relay appears in its output. The message never prints a secret.</summary>
    private void CheckSecrets()
    {
        var lines = Output;
        var secrets = _secrets.Keys.ToArray();
        for (var i = 0; i < lines.Count; i++)
        {
            for (var s = 0; s < secrets.Length; s++)
            {
                if (lines[i].Contains(secrets[s], StringComparison.Ordinal))
                {
                    throw new XunitException($"{RelayUnderTest.Describe()} wrote a secret (#{s + 1} of {secrets.Length}, {secrets[s].Length} chars) in output line {i + 1} of {lines.Count}");
                }
            }
        }
    }
}
