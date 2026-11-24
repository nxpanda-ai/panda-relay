using System.Net.WebSockets;
using System.Security.Cryptography;
using Panda.Bridge.Protocol;

namespace Panda.Relay.Tests;

/// <summary>
/// plan-relay-rename D1 / D1b / D6 (A 2026-11-24): the relay serves its public host and its alias hosts (the old name
/// of a renamed relay). A registration verifies for any of them; the alias use is counted per hour, never per event.
/// </summary>
public class AliasHostTests
{
    private const string Alias = "old.test"; // as production passes it: the host an extension signs (u.host, no :443)

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Alias_hosts_are_read_in_order_and_none_by_default()
    {
        Assert.True(Program.TryParse(["--public-host", "new.example.com", "--alias-host", "old.example.com", "--alias-host", "older.example.com:8443"], out var o, out _));
        Assert.Equal(["old.example.com", "older.example.com:8443"], o!.AliasHosts);

        Assert.True(Program.TryParse(["--public-host", "new.example.com"], out var d, out _));
        Assert.Empty(d!.AliasHosts);
    }

    [Theory]
    [InlineData("--public-host new.example.com --alias-host new.example.com")] // the primary itself
    [InlineData("--public-host new.example.com --alias-host old.example.com --alias-host old.example.com")] // repeated
    [InlineData("--public-host new.example.com --alias-host Old.example.com")] // not lowercase: never what an extension signs
    [InlineData("--public-host new.example.com --alias-host https://old.example.com")]
    [InlineData("--public-host new.example.com --alias-host old.example.com:0")]
    [InlineData("--public-host new.example.com --alias-host old.example.com:70000")]
    [InlineData("--public-host new.example.com --alias-host")]
    public void Bad_alias_hosts_are_refused(string joined)
    {
        Assert.False(Program.TryParse(joined.Split(' '), out var o, out var error));
        Assert.Null(o);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData("https://new.test/mcp", "old.test:443", "https://old.test/mcp")]
    [InlineData("https://new.test/mcp", "old.test", "https://old.test/mcp")]
    [InlineData("https://new.test:8443/x/mcp", "old.test", "https://old.test:8443/x/mcp")] // the MCP URL's own port stays
    public void The_mcp_url_for_an_alias_is_the_configured_one_on_that_host(string mcpUrl, string host, string expected)
    {
        var options = new RelayOptions { PublicHost = "new.test", HostedMcpUrl = mcpUrl };
        options.AliasHosts.Add(host);

        Assert.Equal(expected, options.McpUrlFor(host));
        Assert.Equal(mcpUrl, options.McpUrlFor("new.test")); // the primary: the configured text unchanged
    }

    [Fact]
    public async Task A_registration_signed_for_an_alias_host_is_accepted()
    {
        await using var relay = await RelayFixture.StartAsync(o => o.AliasHosts.Add(Alias));
        await using var chrome = new FakeChrome(relay, ECDsa.Create(ECCurve.NamedCurves.nistP256));
        var nonce = await chrome.OpenAsync();

        await Wire.SendAsync(chrome.Control, chrome.Register(nonce, host: Alias));

        Assert.Equal(new RelayRegistered(chrome.RelayId), await Wire.ReceiveAsync(chrome.Control));
    }

    [Fact]
    public async Task With_an_alias_a_signature_over_a_third_host_is_still_closed_4503()
    {
        await using var relay = await RelayFixture.StartAsync(o => o.AliasHosts.Add(Alias));
        await using var chrome = new FakeChrome(relay, ECDsa.Create(ECCurve.NamedCurves.nistP256));
        var nonce = await chrome.OpenAsync();

        await Wire.SendAsync(chrome.Control, chrome.Register(nonce, host: "evil.test:443"));

        Assert.Equal(RelayCloseCodes.RegistrationInvalid, (await Wire.ClosedAsync(chrome.Control)).Code);
        Assert.Single(relay.Logs.Lines, l => l.Contains("registration proof refused", StringComparison.Ordinal)); // counted once
    }

    [Fact]
    public async Task Alias_use_is_logged_once_an_hour_with_the_previous_hours_counts_and_no_relayId()
    {
        await using var relay = await RelayFixture.StartAsync(o => o.AliasHosts.Add(Alias));
        await using var first = await RegisterViaAliasAsync(relay);
        await using var second = await RegisterViaAliasAsync(relay);
        await using var primary = await relay.ChromeAsync(); // the primary host is never counted

        var lines = AliasLines(relay);
        Assert.Single(lines);
        Assert.DoesNotContain(lines, l => l.Contains(first.RelayId, StringComparison.Ordinal) || l.Contains(second.RelayId, StringComparison.Ordinal));

        relay.Time.Advance(TimeSpan.FromHours(1));
        await using var third = await RegisterViaAliasAsync(relay);

        lines = AliasLines(relay);
        Assert.Equal(2, lines.Count);
        Assert.Contains("; last hour with use 2026-10-12T12Z: 2 registrations, 0 dials", lines[1], StringComparison.Ordinal);
        Assert.Contains("; last hour with use none: 0 registrations, 0 dials", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dials_whose_Host_is_an_alias_are_counted_and_others_are_not()
    {
        await using var relay = await RelayFixture.StartAsync(o => o.AliasHosts.Add(Alias));
        await DialAsync(relay, relay.Ws.Authority); // the primary's own Host (here the loopback one): never counted
        Assert.Empty(AliasLines(relay));

        await DialAsync(relay, "OLD.test"); // Host compares ignoring case
        await DialAsync(relay, Alias);
        Assert.Contains("(first: Dial)", Assert.Single(AliasLines(relay)), StringComparison.Ordinal);

        relay.Time.Advance(TimeSpan.FromHours(3)); // a gap: the last used hour is still reported (diff critic F2)
        await DialAsync(relay, Alias);
        Assert.Contains("; last hour with use 2026-10-12T12Z: 0 registrations, 2 dials", AliasLines(relay)[1], StringComparison.Ordinal);
    }

    [Fact]
    public void The_alias_count_reports_the_last_hour_with_a_use()
    {
        var log = new AliasUseLog();
        var t0 = new DateTimeOffset(2026, 11, 24, 10, 15, 0, TimeSpan.Zero);

        Assert.True(log.Count(AliasUse.Registration, t0, out var none, out var regs, out var dials));
        Assert.Equal(((DateTime?)null, 0, 0), (none, regs, dials));
        Assert.False(log.Count(AliasUse.Dial, t0.AddMinutes(1), out _, out _, out _));
        Assert.False(log.Count(AliasUse.Dial, t0.AddMinutes(44), out _, out _, out _)); // 10:59

        Assert.True(log.Count(AliasUse.Dial, t0.AddMinutes(45), out var hour, out regs, out dials)); // 11:00
        Assert.Equal(("2026-11-24T10Z", 1, 2), (AliasUseLog.Name(hour), regs, dials));

        Assert.True(log.Count(AliasUse.Registration, t0.AddHours(3), out hour, out regs, out dials)); // 13:15, after a gap
        Assert.Equal(("2026-11-24T11Z", 0, 1), (AliasUseLog.Name(hour), regs, dials));
    }

    // A refused dial (4504, never online) is counted too: the relay cannot tell a real dial token (the Chrome checks it),
    // so the count is an upper bound — it errs toward keeping the old name (diff critic F3, trade-off).
    private static async Task DialAsync(RelayFixture relay, string host)
    {
        using var dial = new ClientWebSocket();
        dial.Options.SetRequestHeader("Host", host);
        dial.Options.SetRequestHeader("X-Panda-Dial", AuthToken());
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(TimeSpan.FromSeconds(10));
        await dial.ConnectAsync(new Uri(relay.Ws, $"/v1/stream?dial={NeverOnline()}&kind=link"), limit.Token);
        await Wire.ClosedAsync(dial);
    }
    private static List<string> AliasLines(RelayFixture relay) =>
        [.. relay.Logs.Lines.Where(l => l.Contains("alias host", StringComparison.Ordinal))];

    private static async Task<FakeChrome> RegisterViaAliasAsync(RelayFixture relay)
    {
        var chrome = new FakeChrome(relay, ECDsa.Create(ECCurve.NamedCurves.nistP256));
        var nonce = await chrome.OpenAsync();
        await Wire.SendAsync(chrome.Control, chrome.Register(nonce, host: Alias));
        Assert.IsType<RelayRegistered>(await Wire.ReceiveAsync(chrome.Control));
        return chrome;
    }

    private static string AuthToken() => Panda.Bridge.Auth.AuthCrypto.EncodeBase64Url(RandomNumberGenerator.GetBytes(32));

    private static string NeverOnline() =>
        Panda.Bridge.Relay.DialTokens.RelayIdFromSpki(ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportSubjectPublicKeyInfo());
}
