using System.Net;
using System.Net.WebSockets;
using Panda.Bridge.Protocol;
using Panda.Bridge.Relay;

namespace Panda.Relay.Tests;

/// <summary>
/// plan-relay P4 hardening: Origin rules (a web page must not reach the relay from the user's IP), shutdown, the
/// quota windows' memory bound, and the stream quota spent at STREAM_OPEN.
/// </summary>
public class HardeningTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/v1/browser", null)]
    [InlineData("/v1/browser", "https://evil.example")]
    [InlineData("/v1/browser", "null")]
    [InlineData("/v1/browser", "chrome-extension://abc")]
    [InlineData("/v1/stream?accept=AAAAAAAAAAAAAAAAAAAAAA", null)]
    [InlineData("/v1/stream?accept=AAAAAAAAAAAAAAAAAAAAAA", "https://evil.example")]
    [InlineData("/v1/stream?dial=AAAAAAAAAAAAAAAAAAAAAA&kind=link", "https://evil.example")]
    [InlineData("/v1/stream?dial=AAAAAAAAAAAAAAAAAAAAAA&kind=link", RelayFixture.ExtOrigin)]
    public async Task An_upgrade_with_the_wrong_Origin_for_its_path_is_403(string path, string? origin)
    {
        await using var relay = await RelayFixture.StartAsync();
        Assert.Equal(HttpStatusCode.Forbidden, await relay.RefusedUpgradeAsync(path, origin));
    }

    [Fact]
    public async Task Upgrades_refused_for_their_Origin_spend_no_IP_limit()
    {
        await using var relay = await RelayFixture.StartAsync(o =>
        {
            o.RegistrationsPerIpPerHour = 1;
            o.RefusedDialsPerIpPerHour = 1;
        });
        var id = StreamTests.Identity();
        using var key = id.Key;
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Forbidden, await relay.RefusedUpgradeAsync("/v1/browser", "https://evil.example"));
        }

        await using var chrome = await relay.ChromeAsync(id.Link.Hash, id.Pair.Hash, id.Key); // registration not spent
        for (var i = 0; i < 3; i++)
        {
            // Online relayId + wrong token: without the Origin rule each would be a counted 4501.
            Assert.Equal(HttpStatusCode.Forbidden,
                await relay.RefusedUpgradeAsync($"/v1/stream?dial={id.RelayId}&kind=link", "https://evil.example", id.Pair.Token));
        }

        using var wrong = await relay.DialAsync(id.RelayId, "link", id.Pair.Token);
        Assert.Equal(RelayCloseCodes.DialRefused, (await Wire.ClosedAsync(wrong)).Code); // the first refused dial: not 4530
    }

    [Fact]
    public async Task The_host_stopping_closes_every_control_socket_1001_and_ends_WaitForShutdown()
    {
        await using var relay = await RelayFixture.StartAsync();
        await using var chrome = await relay.ChromeAsync();
        var shutdown = relay.Server.WaitForShutdownAsync(Ct);
        Assert.False(shutdown.IsCompleted);

        relay.Server.Lifetime.StopApplication(); // what the console lifetime does on SIGTERM
        Assert.Equal((int)WebSocketCloseStatus.EndpointUnavailable, (await Wire.ClosedAsync(chrome.Control)).Code);
        await shutdown.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await Assert.ThrowsAsync<WebSocketException>(() => relay.ConnectAsync("/v1/browser")); // no longer listening
    }

    [Fact]
    public async Task The_stream_quota_is_spent_at_STREAM_OPEN_so_two_waiting_streams_cannot_both_pass_it()
    {
        await using var relay = await RelayFixture.StartAsync(o => o.StreamsPerDay = 1);
        await using var chrome = await StreamTests.OnlineAsync(relay);
        using var link = await relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Link.Token);
        var linkIncoming = await chrome.Chrome.IncomingAsync();
        using var pair = await relay.DialAsync(chrome.Chrome.RelayId, "pair", chrome.Pair.Token);
        var pairIncoming = await chrome.Chrome.IncomingAsync();

        using var linkExt = await chrome.Chrome.AcceptAsync(linkIncoming.StreamId);
        Assert.IsType<RelayStreamOpen>(await Wire.ReceiveAsync(link));
        using var pairExt = await chrome.Chrome.AcceptAsync(pairIncoming.StreamId);
        Assert.Equal((RelayCloseCodes.QuotaExceeded, "43200"), await Wire.ClosedAsync(pair));
        Assert.Equal(RelayCloseCodes.QuotaExceeded, (await Wire.ClosedAsync(pairExt)).Code);
    }

    [Fact]
    public async Task Crossing_the_byte_quota_also_closes_a_waiting_stream_of_that_Chrome_4529()
    {
        await using var relay = await RelayFixture.StartAsync(o => o.BytesPerDay = 1000);
        await using var chrome = await StreamTests.OnlineAsync(relay);
        var (dial, ext) = await StreamTests.OpenStreamAsync(relay, chrome, "link", chrome.Link.Token);
        using var d = dial;
        using var e = ext;
        using var waiting = await relay.DialAsync(chrome.Chrome.RelayId, "pair", chrome.Pair.Token);
        var incoming = await chrome.Chrome.IncomingAsync();

        await Wire.SendBinaryAsync(dial, new byte[1001]);
        Assert.Equal(RelayCloseCodes.QuotaExceeded, (await Wire.ClosedAsync(waiting)).Code);
        using var late = await chrome.Chrome.AcceptAsync(incoming.StreamId);
        Assert.Equal(RelayCloseCodes.NotOnline, (await Wire.ClosedAsync(late)).Code);
    }
}

/// <summary>plan-relay P4: the per-IP and per-Chrome windows (S6) — buckets, no growth from checks, one live window.</summary>
public class QuotaWindowTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private static RelayQuotas Quotas(Action<RelayOptions>? configure = null)
    {
        var options = new RelayOptions { PublicHost = RelayFixture.PublicHost };
        configure?.Invoke(options);
        return new RelayQuotas(options);
    }

    private static IPAddress Ip(int i) => new([203, 0, (byte)(i >> 8), (byte)i]);

    private static IPAddress Bucket(string ip) => RelayQuotas.Bucket(IPAddress.Parse(ip));

    [Theory]
    [InlineData("2001:db8:1:2:aaaa::1", "2001:db8:1:2:bbbb::9")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    public void Addresses_of_one_bucket_share_it(string a, string b) => Assert.Equal(Bucket(a), Bucket(b));

    [Fact]
    public void Each_IPv6_64_and_each_IPv4_address_is_its_own_bucket()
    {
        Assert.NotEqual(Bucket("2001:db8:1:2::1"), Bucket("2001:db8:1:3::1"));
        Assert.NotEqual(Bucket("203.0.113.7"), Bucket("203.0.113.8"));
    }

    [Fact]
    public void Checks_never_create_entries()
    {
        var quotas = Quotas();
        for (var i = 0; i < 1000; i++)
        {
            Assert.False(quotas.DialerLimited(Ip(i), Noon, out _));
            Assert.False(quotas.Blocked($"relay{i}", Noon, out _));
        }

        Assert.Equal((0, 0), (quotas.TrackedIps, quotas.TrackedChromes));
    }

    [Theory]
    [InlineData(0, 1000)]
    public void A_limit_below_one_unit_blocks_a_Chrome_with_no_counts_yet(int streams, long bytes)
    {
        var quotas = Quotas(o =>
        {
            o.StreamsPerDay = streams;
            o.BytesPerDay = bytes;
        });
        Assert.True(quotas.Blocked("relay", Noon, out var retryAfter));
        Assert.Equal(43200, retryAfter);
    }

    [Fact]
    public void A_new_window_forgets_every_entry_of_the_old_one()
    {
        var quotas = Quotas(o => o.RefusedDialsPerIpPerHour = 1);
        quotas.CountRefusedDial(Ip(1), Noon);
        quotas.CountRefusedDial(Ip(2), Noon);
        quotas.CountStream("a", Noon);
        quotas.CountStream("b", Noon);
        Assert.True(quotas.DialerLimited(Ip(1), Noon, out _));

        Assert.False(quotas.DialerLimited(Ip(1), Noon.AddHours(1), out _));
        quotas.CountRefusedDial(Ip(3), Noon.AddHours(1));
        Assert.Equal(1, quotas.TrackedIps);
        quotas.CountStream("c", Noon.AddDays(1));
        Assert.Equal(1, quotas.TrackedChromes);
    }

    [Fact]
    public void Past_the_tracking_cap_new_IPs_share_one_overflow_bucket()
    {
        var quotas = Quotas(o =>
        {
            o.MaxTrackedIps = 2;
            o.RegistrationsPerIpPerHour = 1;
        });
        Assert.True(quotas.TryCountRegistration(Ip(1), Noon, out _));
        Assert.True(quotas.TryCountRegistration(Ip(2), Noon, out _));
        Assert.True(quotas.TryCountRegistration(Ip(3), Noon, out _)); // the overflow bucket's first
        Assert.False(quotas.TryCountRegistration(Ip(4), Noon, out _)); // the overflow bucket is full
        Assert.True(quotas.TryCountRegistration(Ip(1), Noon.AddHours(1), out _)); // a new hour starts empty
        Assert.Equal(1, quotas.TrackedIps);
    }

    // plan-relay-prefix (A 2026-11-10): one IPv6 /48 holds at most MaxBucketsPerPrefix /64 buckets an hour; the rest of
    // its /64s share one bucket of that /48. RelayHub passes bucketed IPs, so the tests do too.
    private static IPAddress V6(int prefix48, int n64) => Bucket($"2001:db8:{prefix48:x}:{n64:x}::1");

    [Fact]
    public void Past_its_cap_a_48_s_new_64s_share_one_bucket_and_its_first_64s_keep_their_own()
    {
        var quotas = Quotas(o => { o.MaxBucketsPerPrefix = 2; o.RegistrationsPerIpPerHour = 2; });
        Assert.True(quotas.TryCountRegistration(V6(1, 0), Noon, out _)); // 2001:db8:1:: = bytes 6..7 zero (critic r2)
        Assert.True(quotas.TryCountRegistration(V6(1, 1), Noon, out _));
        Assert.True(quotas.TryCountRegistration(V6(1, 2), Noon, out _)); // the /48's shared bucket: 1
        Assert.True(quotas.TryCountRegistration(V6(1, 3), Noon, out _)); // shared: 2
        Assert.False(quotas.TryCountRegistration(V6(1, 4), Noon, out _)); // shared: full
        Assert.True(quotas.TryCountRegistration(V6(1, 0), Noon, out _)); // its own bucket, never the /48's (no collision)
        Assert.True(quotas.TryCountRegistration(V6(1, 1), Noon, out _));
        Assert.True(quotas.TryCountRegistration(V6(2, 9), Noon, out _)); // another /48 owns a bucket
        Assert.Equal(4, quotas.TrackedIps); // 2 + the shared one + the other /48's
    }

    [Fact]
    public void IPv4_addresses_never_share_a_prefix_bucket()
    {
        var quotas = Quotas(o => { o.MaxBucketsPerPrefix = 1; o.RegistrationsPerIpPerHour = 1; });
        for (var i = 1; i <= 5; i++)
        {
            Assert.True(quotas.TryCountRegistration(Ip(i), Noon, out _), $"203.0.0.{i}");
        }

        Assert.True(quotas.TryCountRegistration(Bucket("::ffff:198.51.100.1"), Noon, out _));
        Assert.True(quotas.TryCountRegistration(Bucket("::ffff:198.51.100.2"), Noon, out _));
    }

    [Fact]
    public void A_check_spends_no_slot_of_a_48()
    {
        var quotas = Quotas(o => { o.MaxBucketsPerPrefix = 2; o.RefusedDialsPerIpPerHour = 1; });
        for (var i = 0; i < 5; i++)
        {
            Assert.False(quotas.DialerLimited(V6(1, i), Noon, out _));
        }

        quotas.CountRefusedDial(V6(1, 10), Noon); // the /48's first /64: its own bucket, now at its limit
        Assert.True(quotas.DialerLimited(V6(1, 10), Noon, out _));
        Assert.False(quotas.DialerLimited(V6(1, 11), Noon, out _)); // not tracked yet: the checks spent no slot
        Assert.Equal(1, quotas.TrackedIps);
    }

    [Fact]
    public void A_check_on_a_new_64_of_a_capped_48_sees_the_shared_bucket()
    {
        var quotas = Quotas(o => { o.MaxBucketsPerPrefix = 1; o.RefusedDialsPerIpPerHour = 1; });
        quotas.CountRefusedDial(V6(1, 1), Noon); // own
        quotas.CountRefusedDial(V6(1, 2), Noon); // the /48's shared bucket, now at its limit
        Assert.True(quotas.DialerLimited(V6(1, 9), Noon, out _)); // rotating /64s inside the /48 does not dodge it (critic)
        Assert.False(quotas.DialerLimited(V6(2, 9), Noon, out _)); // another /48 is not affected
    }

    [Fact]
    public void A_check_sees_the_overflow_when_shared_buckets_fill_the_table()
    {
        var quotas = Quotas(o => { o.MaxTrackedIps = 3; o.MaxBucketsPerPrefix = 1; o.RefusedDialsPerIpPerHour = 1; });
        quotas.CountRefusedDial(V6(1, 1), Noon); // own
        quotas.CountRefusedDial(V6(1, 2), Noon); // the /48's shared bucket
        quotas.CountRefusedDial(Ip(1), Noon); // own: the table (3) is full
        quotas.CountRefusedDial(Ip(2), Noon); // the overflow, now at its limit
        Assert.True(quotas.DialerLimited(Ip(9), Noon, out _)); // a new IP of no tracked /48 is checked against the overflow
        Assert.True(quotas.DialerLimited(V6(7, 1), Noon, out _));
    }

    [Fact]
    public void On_a_full_table_a_48_that_has_a_shared_bucket_counts_into_it_not_the_overflow()
    {
        var quotas = Quotas(o => { o.MaxTrackedIps = 3; o.MaxBucketsPerPrefix = 1; o.RegistrationsPerIpPerHour = 2; });
        Assert.True(quotas.TryCountRegistration(V6(1, 1), Noon, out _)); // own
        Assert.True(quotas.TryCountRegistration(V6(1, 2), Noon, out _)); // shared: 1
        Assert.True(quotas.TryCountRegistration(Ip(1), Noon, out _)); // own: the table is full
        Assert.True(quotas.TryCountRegistration(V6(1, 3), Noon, out _)); // shared: 2
        Assert.False(quotas.TryCountRegistration(V6(1, 4), Noon, out _)); // shared: full — not the overflow
        Assert.True(quotas.TryCountRegistration(Ip(2), Noon, out _)); // the overflow is untouched: 1
        Assert.True(quotas.TryCountRegistration(Ip(3), Noon, out _)); // 2
    }

    [Fact]
    public void A_new_hour_gives_a_full_48_its_slots_back()
    {
        var quotas = Quotas(o => { o.MaxBucketsPerPrefix = 1; o.RegistrationsPerIpPerHour = 1; });
        Assert.True(quotas.TryCountRegistration(V6(1, 1), Noon, out _));
        Assert.True(quotas.TryCountRegistration(V6(1, 2), Noon, out _)); // shared
        Assert.False(quotas.TryCountRegistration(V6(1, 3), Noon, out _)); // shared: full
        var next = Noon.AddHours(1);
        Assert.True(quotas.TryCountRegistration(V6(1, 3), next, out _)); // own again
        Assert.True(quotas.TryCountRegistration(V6(1, 4), next, out _)); // a fresh shared bucket
        Assert.False(quotas.TryCountRegistration(V6(1, 5), next, out _));
    }

    [Fact]
    public void A_48_reaching_its_cap_on_a_full_table_adds_no_bucket_past_the_overflow()
    {
        var quotas = Quotas(o => { o.MaxTrackedIps = 2; o.MaxBucketsPerPrefix = 1; });
        Assert.True(quotas.TryCountRegistration(V6(1, 1), Noon, out _));
        Assert.True(quotas.TryCountRegistration(Ip(1), Noon, out _)); // the table is full
        Assert.True(quotas.TryCountRegistration(V6(1, 2), Noon, out _)); // capped /48, full table: the overflow
        Assert.True(quotas.TryCountRegistration(Ip(2), Noon, out _)); // the overflow
        Assert.True(quotas.TryCountRegistration(V6(1, 3), Noon, out _));
        Assert.True(quotas.TrackedIps <= 3, $"TrackedIps {quotas.TrackedIps}");
    }

    [Fact]
    public void MaxBucketsPerPrefix_below_one_is_refused()
    {
        var options = new RelayOptions { PublicHost = RelayFixture.PublicHost, MaxBucketsPerPrefix = 0 };
        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }
}
