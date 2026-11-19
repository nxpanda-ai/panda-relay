using System.Net.WebSockets;
using Panda.Bridge.Protocol;

namespace Panda.Relay.Tests;

/// <summary>plan-relay P4, seam S6 + S1: per-Chrome quotas (4529), per-IP limits (4530) and the dial check order.</summary>
public class QuotaTests
{
    private static readonly DateTimeOffset OneMinuteToMidnight = new(2026, 10, 12, 23, 59, 0, TimeSpan.Zero);

    // These tests move the fake clock past a quota window; the control socket must not hit its idle timeout meanwhile.
    private static readonly TimeSpan NoIdle = TimeSpan.FromDays(2);

    [Fact]
    public async Task Crossing_the_byte_quota_closes_every_stream_of_that_Chrome_4529_and_refuses_new_dials_until_the_day_ends()
    {
        await using var relay = await RelayFixture.StartAsync(o => { o.BytesPerDay = 1000; o.ControlIdleTimeout = NoIdle; }, now: OneMinuteToMidnight);
        await using var chrome = await RelayClient.OnlineAsync(relay);
        await using var other = await RelayClient.OnlineAsync(relay);
        var (dial, ext) = await RelayClient.OpenStreamAsync(relay, chrome, "link", chrome.Link.Token);
        var (otherDial, otherExt) = await RelayClient.OpenStreamAsync(relay, other, "link", other.Link.Token);
        using var d = dial;
        using var e = ext;
        using var od = otherDial;
        using var oe = otherExt;

        await Wire.SendBinaryAsync(dial, new byte[600]); // dialer → extension
        Assert.Equal(600, (await Wire.ReceiveRawAsync(ext)).Bytes.Length);
        await Wire.SendBinaryAsync(ext, new byte[600]); // extension → dialer: 1200 > 1000, both directions count
        Assert.Equal((RelayCloseCodes.QuotaExceeded, "60"), await Wire.ClosedAsync(dial));
        Assert.Equal((RelayCloseCodes.QuotaExceeded, "60"), await Wire.ClosedAsync(ext));

        using var refused = await relay.DialAsync(chrome.Chrome.RelayId, "pair", chrome.Pair.Token);
        Assert.Equal((RelayCloseCodes.QuotaExceeded, "60"), await Wire.ClosedAsync(refused));

        await Wire.SendBinaryAsync(otherDial, new byte[900]); // another Chrome's quota is its own (R6)
        Assert.Equal(900, (await Wire.ReceiveRawAsync(otherExt)).Bytes.Length);

        relay.Time.Advance(TimeSpan.FromSeconds(60)); // a new UTC day
        var (again, againExt) = await RelayClient.OpenStreamAsync(relay, chrome, "link", chrome.Link.Token);
        again.Dispose();
        againExt.Dispose();
    }

    [Fact]
    public async Task The_stream_quota_refuses_the_next_dial_4529_until_the_day_ends()
    {
        await using var relay = await RelayFixture.StartAsync(o => { o.StreamsPerDay = 2; o.ControlIdleTimeout = NoIdle; }, now: OneMinuteToMidnight);
        await using var chrome = await RelayClient.OnlineAsync(relay);
        for (var i = 0; i < 2; i++)
        {
            var (dial, ext) = await RelayClient.OpenStreamAsync(relay, chrome, "link", chrome.Link.Token);
            await dial.CloseAsync(WebSocketCloseStatus.NormalClosure, "", TestContext.Current.CancellationToken);
            await Wire.ClosedAsync(ext);
            dial.Dispose();
            ext.Dispose();
        }

        using var third = await relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Link.Token);
        Assert.Equal((RelayCloseCodes.QuotaExceeded, "60"), await Wire.ClosedAsync(third));
        relay.Time.Advance(TimeSpan.FromMinutes(1));
        var (next, nextExt) = await RelayClient.OpenStreamAsync(relay, chrome, "link", chrome.Link.Token);
        next.Dispose();
        nextExt.Dispose();
    }

    [Fact]
    public async Task Tokenless_dials_never_spend_a_Chromes_quota()
    {
        await using var relay = await RelayFixture.StartAsync(o =>
        {
            o.StreamsPerDay = 1;
            o.RefusedDialsPerIpPerHour = 1000;
        });
        await using var chrome = await RelayClient.OnlineAsync(relay);
        for (var i = 0; i < 5; i++)
        {
            using var squatter = await relay.DialAsync(chrome.Chrome.RelayId, "link", null);
            Assert.Equal(RelayCloseCodes.DialRefused, (await Wire.ClosedAsync(squatter)).Code);
        }

        var (dial, ext) = await RelayClient.OpenStreamAsync(relay, chrome, "link", chrome.Link.Token);
        dial.Dispose();
        ext.Dispose();
    }

    [Fact]
    public async Task Refused_dials_over_the_IP_limit_close_even_a_valid_dial_4530_until_the_hour_ends()
    {
        await using var relay = await RelayFixture.StartAsync(o => { o.RefusedDialsPerIpPerHour = 2; o.ControlIdleTimeout = NoIdle; },
            now: new DateTimeOffset(2026, 10, 12, 12, 58, 30, TimeSpan.Zero));
        await using var chrome = await RelayClient.OnlineAsync(relay);
        for (var i = 0; i < 2; i++)
        {
            using var wrong = await relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Pair.Token);
            Assert.Equal(RelayCloseCodes.DialRefused, (await Wire.ClosedAsync(wrong)).Code);
        }

        using var limited = await relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Link.Token);
        Assert.Equal((RelayCloseCodes.DialerLimited, "90"), await Wire.ClosedAsync(limited));
        using var offline = await relay.DialAsync("AAAAAAAAAAAAAAAAAAAAAA", "link", chrome.Link.Token);
        Assert.Equal(RelayCloseCodes.DialerLimited, (await Wire.ClosedAsync(offline)).Code); // 4530 comes before 4504

        relay.Time.Advance(TimeSpan.FromSeconds(90));
        var (dial, ext) = await RelayClient.OpenStreamAsync(relay, chrome, "link", chrome.Link.Token);
        dial.Dispose();
        ext.Dispose();
    }

    [Fact]
    public async Task Check_order_is_4504_then_4501_then_4529_then_4509()
    {
        await using var relay = await RelayFixture.StartAsync(o =>
        {
            o.StreamsPerDay = 1;
            o.RefusedDialsPerIpPerHour = 1000;
        });
        await using var chrome = await RelayClient.OnlineAsync(relay);
        var (dial, ext) = await RelayClient.OpenStreamAsync(relay, chrome, "link", chrome.Link.Token); // slot taken, quota used
        using var d = dial;
        using var e = ext;

        using var wrongToken = await relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Pair.Token);
        Assert.Equal(RelayCloseCodes.DialRefused, (await Wire.ClosedAsync(wrongToken)).Code); // 4501 before 4529
        using var overQuota = await relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Link.Token);
        Assert.Equal(RelayCloseCodes.QuotaExceeded, (await Wire.ClosedAsync(overQuota)).Code); // 4529 before 4509
        var id = RelayClient.Identity();
        using var key = id.Key;
        using var offline = await relay.DialAsync(id.RelayId, "link", "not-a-token");
        Assert.Equal(RelayCloseCodes.NotOnline, (await Wire.ClosedAsync(offline)).Code); // 4504 before 4501
    }
}
