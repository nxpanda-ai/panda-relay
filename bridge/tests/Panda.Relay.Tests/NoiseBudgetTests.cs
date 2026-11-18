using System.Net.WebSockets;
using Panda.Bridge.Protocol;

namespace Panda.Relay.Tests;

/// <summary>
/// plan-relay-deploy C6 (D5): /v1/stream upgrades that are not admitted spend a per-IP "noise" budget. A 4504 counts
/// once per (IP, relayId) per hour, an unknown / late / wrong-owner accept and a malformed request each time. Over the
/// budget, that IP's dials and unknown accepts are closed 4530 with the seconds left in the hour (never an HTTP 429:
/// a failed upgrade resets the bridge's strike count, plan critic round 2 N1); a known pending accept still opens
/// (critic round 3 X2); registrations are not affected.
/// </summary>
public class NoiseBudgetTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 12, 12, 59, 0, TimeSpan.Zero); // 60 s left in the hour

    private static Task<RelayFixture> BudgetAsync(int budget, Action<RelayOptions>? configure = null) =>
        RelayFixture.StartAsync(o =>
        {
            o.NoisePerIpPerHour = budget;
            configure?.Invoke(o);
        }, At);

    private static async Task<int> DialCodeAsync(RelayFixture relay, string relayId)
    {
        using var ws = await relay.DialAsync(relayId, "link", null);
        return (await Wire.ClosedAsync(ws)).Code;
    }

    private static async Task<(int Code, string Reason)> AcceptClosedAsync(RelayFixture relay, string streamId)
    {
        using var ws = await relay.ConnectAsync($"/v1/stream?accept={streamId}");
        return await Wire.ClosedAsync(ws);
    }

    private static string OfflineId() => StreamTests.Identity().RelayId;

    [Fact]
    public async Task Retrying_offline_relayIds_costs_one_unit_per_relayId()
    {
        // 20 offline targets × 10 retries = 200 refusals, but 20 units: an honest bridge behind one NAT stays under
        await using var relay = await BudgetAsync(budget: 21);
        var ids = Enumerable.Range(0, 20).Select(_ => OfflineId()).ToList();
        for (var round = 0; round < 10; round++)
        {
            foreach (var id in ids)
            {
                Assert.Equal(RelayCloseCodes.NotOnline, await DialCodeAsync(relay, id));
            }
        }

        await using var chrome = await StreamTests.OnlineAsync(relay); // the IP can still reach an online Chrome
        var (dial, ext) = await StreamTests.OpenStreamAsync(relay, chrome, "link", chrome.Link.Token);
        dial.Dispose();
        ext.Dispose();
    }

    [Fact]
    public async Task Distinct_offline_relayIds_over_the_budget_close_that_IPs_dials_4530_with_the_seconds_left()
    {
        await using var relay = await BudgetAsync(budget: 3);
        await using var chrome = await StreamTests.OnlineAsync(relay);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(RelayCloseCodes.NotOnline, await DialCodeAsync(relay, OfflineId()));
        }

        using var dial = await relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Link.Token); // even a good dial
        Assert.Equal((RelayCloseCodes.DialerLimited, "60"), await Wire.ClosedAsync(dial));

        relay.Time.Advance(TimeSpan.FromSeconds(60)); // a new hour (the idle Chrome's control socket closes too)
        await using var again = await StreamTests.OnlineAsync(relay);
        var (d, e) = await StreamTests.OpenStreamAsync(relay, again, "link", again.Link.Token);
        d.Dispose();
        e.Dispose();
    }

    [Fact]
    public async Task Unknown_accepts_count_each_time_and_over_the_budget_are_closed_4530()
    {
        await using var relay = await BudgetAsync(budget: 2);
        Assert.Equal(RelayCloseCodes.NotOnline, (await AcceptClosedAsync(relay, "AAAAAAAAAAAAAAAAAAAAAA")).Code);
        Assert.Equal(RelayCloseCodes.NotOnline, (await AcceptClosedAsync(relay, "AAAAAAAAAAAAAAAAAAAAAA")).Code);
        Assert.Equal((RelayCloseCodes.DialerLimited, "60"), await AcceptClosedAsync(relay, "AAAAAAAAAAAAAAAAAAAAAA"));
    }

    [Fact]
    public async Task A_wrong_owner_accept_counts()
    {
        // C1 critic #2: the same refusal branch as an unknown streamId
        await using var relay = await BudgetAsync(budget: 1);
        await using var chrome = await StreamTests.OnlineAsync(relay);
        using var dial = await relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Link.Token);
        var incoming = await chrome.Chrome.IncomingAsync();
        using (var wrong = await chrome.Chrome.AcceptAsync(incoming.StreamId, $"&id={OfflineId()}"))
        {
            Assert.Equal(RelayCloseCodes.NotOnline, (await Wire.ClosedAsync(wrong)).Code);
        }

        Assert.Equal(RelayCloseCodes.DialerLimited, await DialCodeAsync(relay, chrome.Chrome.RelayId));
    }

    [Fact]
    public async Task Malformed_stream_requests_count_each_time()
    {
        await using var relay = await BudgetAsync(budget: 2);
        for (var i = 0; i < 2; i++)
        {
            using var ws = await relay.ConnectAsync("/v1/stream?dial=short&kind=link");
            Assert.Equal(RelayCloseCodes.ProtocolError, (await Wire.ClosedAsync(ws)).Code);
        }

        Assert.Equal(RelayCloseCodes.DialerLimited, await DialCodeAsync(relay, OfflineId()));
    }

    [Fact]
    public async Task Over_the_budget_a_known_pending_accept_still_opens_and_registrations_still_work()
    {
        await using var relay = await BudgetAsync(budget: 1);
        await using var chrome = await StreamTests.OnlineAsync(relay);
        using var dial = await relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Link.Token);
        var incoming = await chrome.Chrome.IncomingAsync();
        Assert.Equal(RelayCloseCodes.NotOnline, await DialCodeAsync(relay, OfflineId())); // the budget is now spent

        using var ext = await chrome.Chrome.AcceptAsync(incoming.StreamId);
        Assert.IsType<RelayStreamOpen>(await Wire.ReceiveAsync(dial));
        await using var other = await relay.ChromeAsync(); // /v1/browser is not counted or blocked by the noise budget
    }

    [Fact]
    public async Task A_full_seen_set_counts_every_4504()
    {
        // critic round 3 X3: the (IP, relayId) set is capped; past the cap the dedup stops (strict), memory stays bounded
        await using var relay = await BudgetAsync(budget: 3, o => o.MaxTrackedNoise = 1);
        var id = OfflineId();
        Assert.Equal(RelayCloseCodes.NotOnline, await DialCodeAsync(relay, OfflineId())); // fills the set
        Assert.Equal(RelayCloseCodes.NotOnline, await DialCodeAsync(relay, id));
        Assert.Equal(RelayCloseCodes.NotOnline, await DialCodeAsync(relay, id)); // counted again: 3 units
        Assert.Equal(RelayCloseCodes.DialerLimited, await DialCodeAsync(relay, id));
    }

    [Fact]
    public async Task The_internal_listener_spends_no_noise()
    {
        await using var relay = await BudgetAsync(budget: 1, o => o.InternalPort = 0);
        for (var i = 0; i < 3; i++)
        {
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{relay.Server.InternalPort}/v1/stream?dial={OfflineId()}&kind=link"), TestContext.Current.CancellationToken);
            Assert.Equal(RelayCloseCodes.NotOnline, (await Wire.ClosedAsync(ws)).Code);
        }

        Assert.Equal(RelayCloseCodes.NotOnline, await DialCodeAsync(relay, OfflineId())); // the public bucket is untouched
    }
}
