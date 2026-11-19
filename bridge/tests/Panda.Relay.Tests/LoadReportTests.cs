using System.Diagnostics;
using System.Net.WebSockets;

namespace Panda.Relay.Tests;

/// <summary>
/// plan-zero-alloc-relay R4: a loopback load through the real relay (Kestrel), reported, not gated — run on purpose
/// (explicit): process-wide bytes allocated per forwarded message (relay + Kestrel + this harness and its
/// ClientWebSockets, not split) for 1 and 100 streams of lockstep messages — 100 KiB ones take the pooled-array path
/// (critic F9). Compare a build of the code before the plan with one after it.
/// </summary>
public class LoadReportTests
{
    [Theory(Explicit = true)]
    [InlineData(1, 10, 1024)]
    [InlineData(100, 10, 1024)]
    [InlineData(100, 10, 100 * 1024)]
    public async Task Loopback_load_reports_bytes_allocated_per_forwarded_message(int streams, int seconds, int size)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var relay = await RelayFixture.StartAsync(o =>
        {
            o.ControlIdleTimeout = TimeSpan.FromHours(1);
            o.BytesPerDay = long.MaxValue / 4;
            o.StreamsPerDay = int.MaxValue;
        });
        var chromes = new List<IAsyncDisposable>();
        var pairs = new List<(ClientWebSocket Dial, ClientWebSocket Ext)>();
        for (var i = 0; i < streams; i++) // one link slot per Chrome: one Chrome per stream
        {
            var chrome = await RelayClient.OnlineAsync(relay);
            chromes.Add(chrome);
            pairs.Add(await RelayClient.OpenStreamAsync(relay, chrome, "link", chrome.Link.Token));
        }

        var payload = new byte[size];
        Random.Shared.NextBytes(payload);

        // warm-up, then the measured window
        await RunAsync(pairs, payload, TimeSpan.FromSeconds(3), ct);
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var messages = await RunAsync(pairs, payload, TimeSpan.FromSeconds(seconds), ct);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{streams} stream(s) x {size} B, {seconds} s: {messages} messages, {messages / (double)seconds:F0} msg/s, {allocated / (double)messages:F1} B/msg (process-wide)");
        foreach (var (dial, ext) in pairs)
        {
            dial.Abort();
            ext.Abort();
        }

        foreach (var chrome in chromes)
        {
            await chrome.DisposeAsync();
        }
    }

    private static async Task<long> RunAsync(List<(ClientWebSocket Dial, ClientWebSocket Ext)> pairs, byte[] payload, TimeSpan duration,
        CancellationToken ct)
    {
        var until = Stopwatch.StartNew();
        var counts = await Task.WhenAll(pairs.Select(p => Task.Run(async () =>
        {
            var buffer = new byte[payload.Length];
            long count = 0;
            while (until.Elapsed < duration)
            {
                await p.Dial.SendAsync(payload, WebSocketMessageType.Binary, true, ct);
                await ReceiveWholeAsync(p.Ext, buffer, ct);
                await p.Ext.SendAsync(payload, WebSocketMessageType.Binary, true, ct);
                await ReceiveWholeAsync(p.Dial, buffer, ct);
                count += 2;
            }

            return count;
        }, ct)));
        return counts.Sum();
    }

    private static async Task ReceiveWholeAsync(WebSocket ws, byte[] buffer, CancellationToken ct)
    {
        var count = 0;
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer.AsMemory(count), ct);
            count += result.Count;
            if (result.EndOfMessage)
            {
                Assert.Equal(buffer.Length, count);
                return;
            }
        }
    }
}
