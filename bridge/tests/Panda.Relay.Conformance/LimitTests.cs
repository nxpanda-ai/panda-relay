using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using Panda.Bridge.Protocol;
using Panda.TestSupport;

namespace Panda.Relay.Conformance;

/// <summary>Helpers for G4: relays started with limit flags, and the clock rules of their reasons.</summary>
internal static class Limits
{
    public static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A quota / limit reason: decimal seconds to the end of the UTC day / clock hour (± 5 s of now).</summary>
    public static void AssertSecondsLeft((int Code, string Reason) closed, int code, TimeSpan period)
    {
        Assert.Equal(code, closed.Code);
        Assert.True(int.TryParse(closed.Reason, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds), $"reason '{closed.Reason}' is not decimal seconds");
        var now = DateTimeOffset.UtcNow;
        var start = period == TimeSpan.FromDays(1)
            ? new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero)
            : new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        var left = (start + period - now).TotalSeconds;
        Assert.InRange(seconds, left - 5, left + 5);
    }

    /// <summary>A test whose counts must not cross a clock hour / UTC day waits that boundary out (critic #29 / #39).</summary>
    public static async Task AwayFromBoundaryAsync(TimeSpan needed)
    {
        var now = DateTimeOffset.UtcNow;
        var hourEnd = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero).AddHours(1);
        if (hourEnd - now < needed)
        {
            await Task.Delay(hourEnd - now + TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        }
    }

    /// <summary>A relay with <paramref name="flags"/>, started at least 2 minutes before the next clock hour.</summary>
    public static Task<RelayProcess> StartAsync(params string[] flags) => StartAsync(TimeSpan.FromMinutes(2), flags);

    /// <summary>
    /// A relay with <paramref name="flags"/>, started at least <paramref name="margin"/> before the next clock hour: the
    /// worst start (3 × 30 s) plus the test's own counting (critic T2-T5, trade-off #3).
    /// </summary>
    public static async Task<RelayProcess> StartAsync(TimeSpan margin, params string[] flags)
    {
        await AwayFromBoundaryAsync(margin);
        return await RelayProcess.StartAsync(flags, line => TestContext.Current.SendDiagnosticMessage(line));
    }

    /// <summary>Upgrades refused for their Origin (403) on every socket path: they must spend no per-IP limit (HardeningTests).</summary>
    public static async Task RefusedForTheirOriginAsync(IRelayEndpoint relay, string relayId, string token)
    {
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Forbidden, await relay.RefusedUpgradeAsync("/v1/browser", "https://evil.example"));
            Assert.Equal(HttpStatusCode.Forbidden, await relay.RefusedUpgradeAsync($"/v1/stream?dial={relayId}&kind=link", "https://evil.example", token));
            Assert.Equal(HttpStatusCode.Forbidden, await relay.RefusedUpgradeAsync("/v1/stream?accept=AAAAAAAAAAAAAAAAAAAAAA", "https://evil.example"));
        }
    }

    /// <summary>A dial with no token to <paramref name="relayId"/>: its close code.</summary>
    public static async Task<int> DialCodeAsync(IRelayEndpoint relay, string relayId)
    {
        using var ws = await relay.DialAsync(relayId, "link", null);
        return (await Wire.ClosedAsync(ws)).Code;
    }

    public static string OfflineId()
    {
        var id = RelayClient.Identity();
        id.Key.Dispose();
        return id.RelayId;
    }
}

/// <summary>G4: the per-Chrome daily quotas (S6) set through <c>--bytes-per-day</c> / <c>--streams-per-day</c>.</summary>
public class QuotaTests
{
    [Fact]
    public async Task Crossing_the_byte_quota_closes_every_stream_of_that_Chrome_4529_and_refuses_its_new_dials()
    {
        await using var relay = await Limits.StartAsync("--bytes-per-day", "1000");
        await using var chrome = await relay.OnlineAsync();
        await using var other = await relay.OnlineAsync();
        var (dial, ext) = await relay.OpenStreamAsync(chrome, "link", chrome.Link.Token);
        var (otherDial, otherExt) = await relay.OpenStreamAsync(other, "link", other.Link.Token);
        using var d = dial;
        using var e = ext;
        using var od = otherDial;
        using var oe = otherExt;

        await Wire.SendBinaryAsync(dial, new byte[600]);
        Assert.Equal(600, (await Wire.ReceiveRawAsync(ext)).Bytes.Length);
        await Wire.SendBinaryAsync(ext, new byte[600]); // 1200 > 1000: both directions count
        Limits.AssertSecondsLeft(await Wire.ClosedAsync(dial), RelayCloseCodes.QuotaExceeded, TimeSpan.FromDays(1));
        Assert.Equal(RelayCloseCodes.QuotaExceeded, (await Wire.ClosedAsync(ext)).Code);

        using var refused = await relay.DialAsync(chrome.Chrome.RelayId, "pair", chrome.Pair.Token);
        Limits.AssertSecondsLeft(await Wire.ClosedAsync(refused), RelayCloseCodes.QuotaExceeded, TimeSpan.FromDays(1));

        await Wire.SendBinaryAsync(otherDial, new byte[900]); // another Chrome's quota is its own
        Assert.Equal(900, (await Wire.ReceiveRawAsync(otherExt)).Bytes.Length);
    }

    [Fact]
    public async Task Crossing_the_byte_quota_also_closes_a_waiting_stream_of_that_Chrome_4529()
    {
        await using var relay = await Limits.StartAsync("--bytes-per-day", "1000");
        await using var chrome = await relay.OnlineAsync();
        var (dial, ext) = await relay.OpenStreamAsync(chrome, "link", chrome.Link.Token);
        using var d = dial;
        using var e = ext;
        using var waiting = await relay.DialAsync(chrome.Chrome.RelayId, "pair", chrome.Pair.Token);
        var incoming = await chrome.Chrome.IncomingAsync();

        await Wire.SendBinaryAsync(dial, new byte[1001]);
        Assert.Equal(RelayCloseCodes.QuotaExceeded, (await Wire.ClosedAsync(waiting)).Code);
        using var late = await chrome.Chrome.AcceptAsync(incoming.StreamId);
        Assert.Equal(RelayCloseCodes.NotOnline, (await Wire.ClosedAsync(late)).Code);
    }

    [Fact]
    public async Task The_stream_quota_is_spent_at_STREAM_OPEN_so_two_waiting_streams_cannot_both_pass_it()
    {
        await using var relay = await Limits.StartAsync("--streams-per-day", "1");
        await using var chrome = await relay.OnlineAsync();
        using var link = await relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Link.Token);
        var linkIncoming = await chrome.Chrome.IncomingAsync();
        using var pair = await relay.DialAsync(chrome.Chrome.RelayId, "pair", chrome.Pair.Token);
        var pairIncoming = await chrome.Chrome.IncomingAsync();

        using var linkExt = await chrome.Chrome.AcceptAsync(linkIncoming.StreamId);
        Assert.IsType<RelayStreamOpen>(await Wire.ReceiveAsync(link));
        using var pairExt = await chrome.Chrome.AcceptAsync(pairIncoming.StreamId);
        Limits.AssertSecondsLeft(await Wire.ClosedAsync(pair), RelayCloseCodes.QuotaExceeded, TimeSpan.FromDays(1));
        Assert.Equal(RelayCloseCodes.QuotaExceeded, (await Wire.ClosedAsync(pairExt)).Code);

        using var third = await relay.DialAsync(chrome.Chrome.RelayId, "pair", chrome.Pair.Token);
        Assert.Equal(RelayCloseCodes.QuotaExceeded, (await Wire.ClosedAsync(third)).Code);
    }

    [Fact]
    public async Task Tokenless_dials_never_spend_a_Chromes_quota()
    {
        await using var relay = await Limits.StartAsync("--streams-per-day", "1");
        await using var chrome = await relay.OnlineAsync();
        for (var i = 0; i < 5; i++)
        {
            using var squatter = await relay.DialAsync(chrome.Chrome.RelayId, "link", null);
            Assert.Equal(RelayCloseCodes.DialRefused, (await Wire.ClosedAsync(squatter)).Code);
        }

        var (dial, ext) = await relay.OpenStreamAsync(chrome, "link", chrome.Link.Token);
        dial.Dispose();
        ext.Dispose();
    }

    [Fact]
    public async Task Check_order_is_4504_then_4501_then_4529_then_4509()
    {
        await using var relay = await Limits.StartAsync("--streams-per-day", "1");
        await using var chrome = await relay.OnlineAsync();
        var (dial, ext) = await relay.OpenStreamAsync(chrome, "link", chrome.Link.Token); // slot taken, quota used
        using var d = dial;
        using var e = ext;

        using var wrongToken = await relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Pair.Token);
        Assert.Equal(RelayCloseCodes.DialRefused, (await Wire.ClosedAsync(wrongToken)).Code); // 4501 before 4529
        using var overQuota = await relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Link.Token);
        Assert.Equal(RelayCloseCodes.QuotaExceeded, (await Wire.ClosedAsync(overQuota)).Code); // 4529 before 4509
        using var offline = await relay.DialAsync(Limits.OfflineId(), "link", "not-a-token");
        Assert.Equal(RelayCloseCodes.NotOnline, (await Wire.ClosedAsync(offline)).Code); // 4504 before 4501
    }
}

/// <summary>G4: refused dials (4501) per IP — 600 per clock hour (no CLI flag; S1 4530), on a relay of its own.</summary>
public class RefusedDialLimitTests
{
    private const int PerHour = 600; // RelayOptions.RefusedDialsPerIpPerHour

    [Fact]
    public async Task Refused_dials_over_the_IP_limit_close_even_a_valid_dial_and_an_offline_one_4530_but_never_on_the_internal_listener()
    {
        await using var relay = await Limits.StartAsync(TimeSpan.FromMinutes(4), "--internal-port", RelayProcess.SecondPort);
        await using var chrome = await relay.OnlineAsync();
        await Limits.RefusedForTheirOriginAsync(relay, chrome.Chrome.RelayId, chrome.Pair.Token); // counted, they would make dial 600 the 4530
        for (var i = 0; i < PerHour; i++)
        {
            using var wrong = await relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Pair.Token);
            Assert.Equal(RelayCloseCodes.DialRefused, (await Wire.ClosedAsync(wrong)).Code);
        }

        using (var limited = await relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Link.Token))
        {
            Limits.AssertSecondsLeft(await Wire.ClosedAsync(limited), RelayCloseCodes.DialerLimited, TimeSpan.FromHours(1));
        }

        Assert.Equal(RelayCloseCodes.DialerLimited, await Limits.DialCodeAsync(relay, Limits.OfflineId())); // 4530 before 4504

        var inside = new InternalListenerTests.InternalEndpoint(relay, relay.InternalPort); // the same 127.0.0.1, not limited there
        using (var wrong = await inside.DialAsync(chrome.Chrome.RelayId, "link", chrome.Pair.Token))
        {
            Assert.Equal(RelayCloseCodes.DialRefused, (await Wire.ClosedAsync(wrong)).Code);
        }

        using var dial = await inside.DialAsync(chrome.Chrome.RelayId, "link", chrome.Link.Token);
        var incoming = await chrome.Chrome.IncomingAsync();
        using var ext = await chrome.Chrome.AcceptAsync(incoming.StreamId);
        Assert.IsType<RelayStreamOpen>(await Wire.ReceiveAsync(dial));
    }
}

/// <summary>G4: the per-IP noise budget (<c>--noise-per-ip-per-hour</c>, plan-relay-deploy C6).</summary>
public class NoiseBudgetTests
{
    private static async Task<(int Code, string Reason)> UnknownAcceptAsync(IRelayEndpoint relay)
    {
        using var ws = await relay.ConnectAsync("/v1/stream?accept=AAAAAAAAAAAAAAAAAAAAAA");
        return await Wire.ClosedAsync(ws);
    }

    [Fact]
    public async Task Retrying_one_offline_relayId_costs_one_unit_and_distinct_ones_over_the_budget_are_4530()
    {
        await using var relay = await Limits.StartAsync("--noise-per-ip-per-hour", "2");
        var id = Limits.OfflineId();
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(RelayCloseCodes.NotOnline, await Limits.DialCodeAsync(relay, id)); // one unit, however often
        }

        Assert.Equal(RelayCloseCodes.NotOnline, await Limits.DialCodeAsync(relay, Limits.OfflineId())); // the second unit
        using var dial = await relay.DialAsync(Limits.OfflineId(), "link", null);
        Limits.AssertSecondsLeft(await Wire.ClosedAsync(dial), RelayCloseCodes.DialerLimited, TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task Unknown_accepts_count_each_time()
    {
        await using var relay = await Limits.StartAsync("--noise-per-ip-per-hour", "2");
        Assert.Equal(RelayCloseCodes.NotOnline, (await UnknownAcceptAsync(relay)).Code);
        Assert.Equal(RelayCloseCodes.NotOnline, (await UnknownAcceptAsync(relay)).Code);
        Limits.AssertSecondsLeft(await UnknownAcceptAsync(relay), RelayCloseCodes.DialerLimited, TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task Malformed_stream_requests_count_each_time()
    {
        await using var relay = await Limits.StartAsync("--noise-per-ip-per-hour", "2");
        for (var i = 0; i < 2; i++)
        {
            using var ws = await relay.ConnectAsync("/v1/stream?dial=short&kind=link");
            Assert.Equal(RelayCloseCodes.ProtocolError, (await Wire.ClosedAsync(ws)).Code);
        }

        Assert.Equal(RelayCloseCodes.DialerLimited, await Limits.DialCodeAsync(relay, Limits.OfflineId()));
    }

    [Fact]
    public async Task A_wrong_owner_accept_counts()
    {
        await using var relay = await Limits.StartAsync("--noise-per-ip-per-hour", "1");
        await using var chrome = await relay.OnlineAsync();
        using var dial = await relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Link.Token);
        var incoming = await chrome.Chrome.IncomingAsync();
        using (var wrong = await chrome.Chrome.AcceptAsync(incoming.StreamId, $"&id={Limits.OfflineId()}"))
        {
            Assert.Equal(RelayCloseCodes.NotOnline, (await Wire.ClosedAsync(wrong)).Code);
        }

        Assert.Equal(RelayCloseCodes.DialerLimited, await Limits.DialCodeAsync(relay, Limits.OfflineId()));
    }

    [Fact]
    public async Task Upgrades_refused_for_their_Origin_spend_no_noise()
    {
        await using var relay = await Limits.StartAsync("--noise-per-ip-per-hour", "1");
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Forbidden, await relay.RefusedUpgradeAsync($"/v1/stream?dial={Limits.OfflineId()}&kind=link", "https://evil.example"));
            Assert.Equal(HttpStatusCode.Forbidden, await relay.RefusedUpgradeAsync("/v1/stream?accept=AAAAAAAAAAAAAAAAAAAAAA", "https://evil.example"));
        }

        Assert.Equal(RelayCloseCodes.NotOnline, await Limits.DialCodeAsync(relay, Limits.OfflineId())); // the one unit is still there
    }

    [Fact]
    public async Task Over_the_budget_a_known_pending_accept_still_opens_and_registrations_still_work()
    {
        await using var relay = await Limits.StartAsync("--noise-per-ip-per-hour", "1");
        await using var chrome = await relay.OnlineAsync();
        using var dial = await relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Link.Token);
        var incoming = await chrome.Chrome.IncomingAsync();
        Assert.Equal(RelayCloseCodes.NotOnline, await Limits.DialCodeAsync(relay, Limits.OfflineId())); // the budget is now spent

        using var ext = await chrome.Chrome.AcceptAsync(incoming.StreamId);
        Assert.IsType<RelayStreamOpen>(await Wire.ReceiveAsync(dial));
        await using var other = await relay.ChromeAsync(); // /v1/browser is never counted or blocked by the noise budget
    }
}

/// <summary>
/// G4: the client IP the per-IP limits count — only a <c>--trusted-proxy</c> peer's header is read, its right-most
/// address, one hop, IPv6 per /64, the header named by <c>--client-ip-header</c>. Observed through the noise budget
/// (1 unit per unknown accept), the per-IP limit the CLI can set. Addresses: documentation ranges only.
/// </summary>
public class ClientIpTests
{
    /// <summary>True = this client's bucket had budget left (4504), false = spent (4530).</summary>
    private static async Task<bool> AdmittedAsync(IRelayEndpoint relay, params string[] headerLines)
    {
        using var ws = await relay.RawConnectAsync("/v1/stream?accept=AAAAAAAAAAAAAAAAAAAAAA", headerLines);
        var code = (await Wire.ClosedAsync(ws)).Code;
        Assert.Contains(code, new[] { RelayCloseCodes.NotOnline, RelayCloseCodes.DialerLimited });
        return code == RelayCloseCodes.NotOnline;
    }

    private static Task<RelayProcess> BehindProxyAsync(params string[] more) =>
        Limits.StartAsync(["--noise-per-ip-per-hour", "1", "--trusted-proxy", "127.0.0.1", .. more]);

    [Fact]
    public async Task A_trusted_proxys_X_Forwarded_For_picks_the_bucket()
    {
        await using var relay = await BehindProxyAsync();
        Assert.True(await AdmittedAsync(relay, "X-Forwarded-For: 203.0.113.5"));
        Assert.True(await AdmittedAsync(relay, "X-Forwarded-For: 203.0.113.6"));
        Assert.False(await AdmittedAsync(relay, "X-Forwarded-For: 203.0.113.5"));
    }

    [Fact]
    public async Task An_address_with_a_port_counts_without_its_port_and_IPv6_counts_per_64()
    {
        await using var relay = await BehindProxyAsync();
        Assert.True(await AdmittedAsync(relay, "X-Forwarded-For: 203.0.113.7:4711"));
        Assert.False(await AdmittedAsync(relay, "X-Forwarded-For: 203.0.113.7:4712"));
        Assert.False(await AdmittedAsync(relay, "X-Forwarded-For: ::ffff:203.0.113.7")); // an IPv4-mapped address is that IPv4
        Assert.True(await AdmittedAsync(relay, "X-Forwarded-For: [2001:db8:1:2::1]:443"));
        Assert.True(await AdmittedAsync(relay, "X-Forwarded-For: 2001:db8:1:3::1")); // another /64
        Assert.False(await AdmittedAsync(relay, "X-Forwarded-For: 2001:db8:1:2::99"));
    }

    [Fact]
    public async Task The_right_most_address_wins_across_a_list_and_across_header_lines_one_hop_only()
    {
        await using var relay = await BehindProxyAsync();
        Assert.True(await AdmittedAsync(relay, "X-Forwarded-For: 198.51.100.1, 203.0.113.8")); // a client-sent left part
        Assert.True(await AdmittedAsync(relay, "X-Forwarded-For: 203.0.113.8, 198.51.100.1")); // right-most differs
        Assert.False(await AdmittedAsync(relay, "X-Forwarded-For: 198.51.100.2", "X-Forwarded-For: 203.0.113.8"));
        Assert.True(await AdmittedAsync(relay, "X-Forwarded-For: 203.0.113.20, 127.0.0.1")); // one hop: 127.0.0.1
        Assert.False(await AdmittedAsync(relay, "X-Forwarded-For: 203.0.113.21, 127.0.0.1"));
    }

    [Fact]
    public async Task The_header_named_by_client_ip_header_is_read_and_X_Forwarded_For_is_then_ignored()
    {
        await using var relay = await BehindProxyAsync("--client-ip-header", "CF-Connecting-IP");
        Assert.True(await AdmittedAsync(relay, "CF-Connecting-IP: 203.0.113.9", "X-Forwarded-For: 198.51.100.3"));
        Assert.True(await AdmittedAsync(relay, "CF-Connecting-IP: 203.0.113.10", "X-Forwarded-For: 198.51.100.3"));
        Assert.False(await AdmittedAsync(relay, "CF-Connecting-IP: 203.0.113.9", "X-Forwarded-For: 198.51.100.4"));
    }

    [Fact]
    public async Task Without_a_trusted_proxy_the_header_is_ignored()
    {
        await using var relay = await Limits.StartAsync("--noise-per-ip-per-hour", "1");
        Assert.True(await AdmittedAsync(relay, "X-Forwarded-For: 203.0.113.11"));
        Assert.False(await AdmittedAsync(relay, "X-Forwarded-For: 203.0.113.12")); // both are the loopback peer
    }

    [Fact]
    public async Task An_untrusted_peers_header_is_ignored_even_when_another_proxy_is_trusted()
    {
        await using var relay = await Limits.StartAsync("--noise-per-ip-per-hour", "1", "--trusted-proxy", "192.0.2.1"); // every suite peer is 127.0.0.1
        Assert.True(await AdmittedAsync(relay, "X-Forwarded-For: 203.0.113.13"));
        Assert.False(await AdmittedAsync(relay, "X-Forwarded-For: 203.0.113.14"));
    }
}

/// <summary>G4: <c>--internal-port</c> — a loopback listener for the hosted bridge that serves only dials, per relayId, outside the per-IP limits.</summary>
public class InternalListenerTests
{
    private static async Task<(RelayProcess Relay, int Port)> WithInternalAsync(params string[] more)
    {
        var relay = await Limits.StartAsync(["--internal-port", RelayProcess.SecondPort, .. more]);
        return (relay, relay.InternalPort);
    }

    [Theory]
    [InlineData("/v1/browser")]
    [InlineData("/v1/stream?accept=AAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("/v1/other")]
    [InlineData("/v1/browser?dial=AAAAAAAAAAAAAAAAAAAAAA&kind=link")]
    [InlineData("/v1/stream/?dial=AAAAAAAAAAAAAAAAAAAAAA&kind=link")]
    public async Task The_internal_listener_serves_only_dials(string pathAndQuery)
    {
        var (relay, port) = await WithInternalAsync();
        await using var _ = relay;
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", RelayClient.ExtOrigin);
        ws.Options.CollectHttpResponseDetails = true;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(RelayClient.ConnectTimeout);
        await Assert.ThrowsAsync<WebSocketException>(() => ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}{pathAndQuery}"), limit.Token));
        Assert.Equal(HttpStatusCode.NotFound, ws.HttpStatusCode);
    }

    [Fact]
    public async Task The_internal_listener_never_serves_the_help_page()
    {
        var (relay, port) = await WithInternalAsync();
        await using var _ = relay;
        using var http = new HttpClient { Timeout = RelayClient.ConnectTimeout };
        using var response = await http.GetAsync(new Uri($"http://127.0.0.1:{port}/c/AAAAAAAAAAAAAAAAAAAAAA"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_internal_dial_reaches_an_online_Chrome_and_an_offline_relayId_is_4504()
    {
        var (relay, port) = await WithInternalAsync();
        await using var _ = relay;
        await using var chrome = await relay.OnlineAsync();
        var inside = new InternalEndpoint(relay, port);
        using var dial = await inside.DialAsync(chrome.Chrome.RelayId, "link", chrome.Link.Token);
        var incoming = await chrome.Chrome.IncomingAsync();
        using var ext = await chrome.Chrome.AcceptAsync(incoming.StreamId); // the accept stays on the public listener
        Assert.IsType<RelayStreamOpen>(await Wire.ReceiveAsync(dial));
        Assert.Equal(RelayCloseCodes.NotOnline, await Limits.DialCodeAsync(inside, Limits.OfflineId()));
    }

    [Fact]
    public async Task The_internal_listener_spends_no_noise()
    {
        var (relay, port) = await WithInternalAsync("--noise-per-ip-per-hour", "1");
        await using var _ = relay;
        var inside = new InternalEndpoint(relay, port);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(RelayCloseCodes.NotOnline, await Limits.DialCodeAsync(inside, Limits.OfflineId()));
        }

        Assert.Equal(RelayCloseCodes.NotOnline, await Limits.DialCodeAsync(relay, Limits.OfflineId())); // the public bucket is untouched
    }

    /// <summary>The same relay reached on its internal port (its secrets and sockets still go to the relay process).</summary>
    internal sealed class InternalEndpoint(RelayProcess relay, int port) : IRelayEndpoint
    {
        public Uri Ws => new($"ws://127.0.0.1:{port}");

        public int Port => port;

        public string PublicHost => relay.PublicHost;

        public void Opened(WebSocket socket) => relay.Opened(socket);

        public void Secret(string value) => relay.Secret(value);
    }
}
