using System.Net.WebSockets;
using Panda.Bridge.Protocol;

namespace Panda.Relay.Tests;

/// <summary>
/// 2026-11-18 (A: "Log VPS bị nhiễu do lỗi 4504 => fix cái này trước"): a bridge redials an offline Chrome once a minute
/// forever, and each 4504 wrote one line — 5840 of 6232 relay lines in a day on the VPS. Now a 4504 is logged once
/// per (relayId, kind) per clock hour, with how many that pair had in the previous hour; the dial itself is refused
/// exactly as before, and every other refusal is still logged every time. Past <see cref="RelayOptions.MaxTrackedNoise"/>
/// remembered pairs every 4504 is logged (memory bound; never silent).
/// </summary>
public class NotOnlineLogTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 12, 12, 10, 0, TimeSpan.Zero);

    private static string OfflineId() => RelayClient.Identity().RelayId;

    private static async Task<int> DialCodeAsync(RelayFixture relay, string relayId, string kind = "link", string? token = null)
    {
        using var ws = await relay.DialAsync(relayId, kind, token);
        return (await Wire.ClosedAsync(ws)).Code; // the relay logs before it closes
    }

    private static List<string> Refusals(RelayFixture relay, string relayId, int code) =>
        [.. relay.Logs.Lines.Where(l => l.Contains($"for {relayId} refused {code}", StringComparison.Ordinal))];

    [Fact]
    public async Task An_offline_relayId_redialed_in_one_hour_is_logged_once_and_every_dial_is_still_refused_4504()
    {
        await using var relay = await RelayFixture.StartAsync(now: At);
        var id = OfflineId();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(RelayCloseCodes.NotOnline, await DialCodeAsync(relay, id));
        }

        var line = Assert.Single(Refusals(relay, id, RelayCloseCodes.NotOnline));
        Assert.Contains("0 in the previous hour", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_relayId_and_each_kind_gets_its_own_line()
    {
        await using var relay = await RelayFixture.StartAsync(now: At);
        var (a, b) = (OfflineId(), OfflineId());
        foreach (var (id, kind) in new[] { (a, "link"), (a, "link"), (b, "link"), (a, "pair"), (a, "pair") })
        {
            Assert.Equal(RelayCloseCodes.NotOnline, await DialCodeAsync(relay, id, kind));
        }

        var forA = Refusals(relay, a, RelayCloseCodes.NotOnline);
        Assert.Equal(2, forA.Count);
        Assert.Single(forA, l => l.Contains("dial Link", StringComparison.Ordinal));
        Assert.Single(forA, l => l.Contains("dial Pair", StringComparison.Ordinal));
        Assert.Single(Refusals(relay, b, RelayCloseCodes.NotOnline));
    }

    [Fact]
    public async Task The_next_clock_hour_logs_it_again_with_the_previous_hours_count()
    {
        await using var relay = await RelayFixture.StartAsync(now: At);
        var id = OfflineId();
        for (var i = 0; i < 3; i++)
        {
            await DialCodeAsync(relay, id);
        }

        relay.Time.Advance(TimeSpan.FromMinutes(49)); // 12:59 — still the same hour
        await DialCodeAsync(relay, id);
        Assert.Single(Refusals(relay, id, RelayCloseCodes.NotOnline));

        relay.Time.Advance(TimeSpan.FromMinutes(1)); // 13:00 — a new hour
        await DialCodeAsync(relay, id);
        await DialCodeAsync(relay, id);
        var lines = Refusals(relay, id, RelayCloseCodes.NotOnline);
        Assert.Equal(2, lines.Count);
        Assert.Contains("4 in the previous hour", lines[1], StringComparison.Ordinal);

        relay.Time.Advance(TimeSpan.FromHours(2)); // 15:00 — an hour with none in between: no stale count
        await DialCodeAsync(relay, id);
        lines = Refusals(relay, id, RelayCloseCodes.NotOnline);
        Assert.Equal(3, lines.Count);
        Assert.Contains("0 in the previous hour", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Another_refusal_is_still_logged_every_time()
    {
        await using var relay = await RelayFixture.StartAsync(now: At);
        await using var chrome = await RelayClient.OnlineAsync(relay);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(RelayCloseCodes.DialRefused, await DialCodeAsync(relay, chrome.Chrome.RelayId, "link", "wrong"));
        }

        Assert.Equal(3, Refusals(relay, chrome.Chrome.RelayId, RelayCloseCodes.DialRefused).Count);
    }

    [Fact]
    public async Task Past_the_tracked_limit_every_4504_is_logged()
    {
        await using var relay = await RelayFixture.StartAsync(o => o.MaxTrackedNoise = 1, At);
        var (first, second) = (OfflineId(), OfflineId());
        await DialCodeAsync(relay, first);
        await DialCodeAsync(relay, first);
        await DialCodeAsync(relay, second); // the table is full: not remembered
        await DialCodeAsync(relay, second);
        Assert.Single(Refusals(relay, first, RelayCloseCodes.NotOnline));
        var untracked = Refusals(relay, second, RelayCloseCodes.NotOnline);
        Assert.Equal(2, untracked.Count);
        // diff critic #1: the plain line, so an operator can tell "table full" from "first this hour"
        Assert.All(untracked, l => Assert.DoesNotContain("once an hour", l, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_hosted_bridges_redials_on_the_internal_listener_are_logged_once_too()
    {
        // diff critic #5: production's noise came from here — no IP bucket (the dial is not counted on an IP)
        await using var relay = await RelayFixture.StartAsync(o => o.InternalPort = 0, At);
        var id = OfflineId();
        for (var i = 0; i < 3; i++)
        {
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(
                new Uri($"ws://127.0.0.1:{relay.Server.InternalPort}/v1/stream?dial={id}&kind=link"),
                TestContext.Current.CancellationToken);
            Assert.Equal(RelayCloseCodes.NotOnline, (await Wire.ClosedAsync(ws)).Code);
        }

        var line = Assert.Single(Refusals(relay, id, RelayCloseCodes.NotOnline));
        Assert.Contains("0 in the previous hour", line, StringComparison.Ordinal);
    }
}
