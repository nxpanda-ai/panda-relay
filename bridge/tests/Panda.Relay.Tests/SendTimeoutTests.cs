using System.Net.WebSockets;

namespace Panda.Relay.Tests;

/// <summary>
/// plan-zero-alloc-relay R2: characterization of <see cref="RelayOptions.SendTimeout"/> on real Kestrel — a peer that
/// stops reading is cut off once SendTimeout passes on the relay's clock, and not before; the stuck send itself is freed
/// (its pump reads the dialer again), not only the ext receive (critic R2).
/// </summary>
public class SendTimeoutTests
{
    [Fact]
    public async Task A_peer_that_stops_reading_is_cut_off_once_SendTimeout_passes_and_not_before()
    {
        await using var relay = await RelayFixture.StartAsync(o => o.ControlIdleTimeout = TimeSpan.FromHours(1));
        await using var chrome = await StreamTests.OnlineAsync(relay);
        var (dial, ext) = await StreamTests.OpenStreamAsync(relay, chrome, "link", chrome.Link.Token);
        using var d = dial;
        using var e = ext; // never read from here on: the relay's send to it fills the TCP buffers and sticks

        var dialClosed = ClosedAsync(dial);
        long sent = 0;
        var pusher = Task.Run(async () =>
        {
            var chunk = new byte[256 * 1024];
            try
            {
                while (true)
                {
                    await dial.SendAsync(chunk, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
                    Interlocked.Add(ref sent, chunk.Length);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
            {
                // the dial socket was aborted at the end of the test
            }
        }, TestContext.Current.CancellationToken);

        // stuck: the relay stopped reading the dialer because its send to ext does not complete
        await StalledAsync(() => Interlocked.Read(ref sent));
        Assert.False(dialClosed.IsCompleted, "the relay cut the stream before SendTimeout");

        // negative control: nothing but our timer cuts it — 8 s of wall clock covers Kestrel's own data-rate check
        // (240 B/s after a 5 s grace, if it applied to upgraded connections)
        relay.Time.Advance(relay.Server.Options.SendTimeout - TimeSpan.FromSeconds(1));
        await Task.Delay(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
        Assert.False(dialClosed.IsCompleted, "the stream was cut before SendTimeout on the relay's clock");
        var stuckAt = Interlocked.Read(ref sent);

        relay.Time.Advance(TimeSpan.FromSeconds(1));
        var code = await dialClosed.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, code); // the stuck side is aborted, the other side closed 1000

        // only the stuck pump reads the dialer: it drains again only once its send returned (D3's fallback trigger)
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (Interlocked.Read(ref sent) == stuckAt)
        {
            Assert.True(DateTime.UtcNow < deadline, "the stuck send was never freed: its pump never read the dialer again");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        dial.Abort();
        await pusher;
    }

    private static async Task<WebSocketCloseStatus?> ClosedAsync(WebSocket ws)
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return ws.CloseStatus;
            }
        }
    }

    /// <summary>Waits until <paramref name="progress"/> has not moved for 1 s (at most 60 s).</summary>
    private static async Task StalledAsync(Func<long> progress)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        var last = progress();
        var since = DateTime.UtcNow;
        while (DateTime.UtcNow - since < TimeSpan.FromSeconds(1))
        {
            Assert.True(DateTime.UtcNow < deadline, $"the dialer never stalled ({progress()} B sent)");
            await Task.Delay(50, TestContext.Current.CancellationToken);
            var now = progress();
            if (now != last)
            {
                (last, since) = (now, DateTime.UtcNow);
            }
        }
    }
}
