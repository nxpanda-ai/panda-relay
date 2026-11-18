using System.Net;
using System.Net.WebSockets;
using Panda.Bridge.Protocol;

namespace Panda.Relay.Tests;

/// <summary>
/// plan-relay-deploy C5 (D5): a loopback-only listener for the hosted bridge beside the relay. Its dials are exempt
/// from the per-IP limits (hosted Chromes must not share one public IP bucket, critic P5), it never reads a forwarded
/// header (C2 critic N3), and it serves nothing but dials. Per-relayId admission is unchanged.
/// </summary>
public class InternalListenerTests
{
    private static Task<RelayFixture> WithInternalAsync(Action<RelayOptions>? configure = null) =>
        RelayFixture.StartAsync(o =>
        {
            o.InternalPort = 0; // any free port (tests)
            configure?.Invoke(o);
        });

    private static Uri Internal(RelayFixture relay, string pathAndQuery) =>
        new($"ws://127.0.0.1:{relay.Server.InternalPort ?? throw new InvalidOperationException("no internal listener")}{pathAndQuery}");

    private static async Task<ClientWebSocket> DialInternalAsync(RelayFixture relay, string relayId, string kind, string? token)
    {
        var ws = new ClientWebSocket();
        if (token is not null)
        {
            ws.Options.SetRequestHeader("X-Panda-Dial", token);
        }

        await ws.ConnectAsync(Internal(relay, $"/v1/stream?dial={relayId}&kind={kind}"), TestContext.Current.CancellationToken);
        return ws;
    }

    [Fact]
    public async Task Without_the_option_there_is_no_internal_listener()
    {
        await using var relay = await RelayFixture.StartAsync();
        Assert.Null(relay.Server.InternalPort);
    }

    [Fact]
    public async Task The_internal_listener_is_on_127_0_0_1_whatever_the_public_listen_address()
    {
        await using var relay = await WithInternalAsync(o => o.ListenAddress = IPAddress.IPv6Loopback);
        Assert.Equal(IPAddress.Loopback, relay.Server.InternalEndPoint!.Address);
    }

    [Fact]
    public async Task The_internal_listener_has_its_own_port()
    {
        await using var relay = await WithInternalAsync();
        Assert.NotNull(relay.Server.InternalPort);
        Assert.NotEqual(relay.Server.Port, relay.Server.InternalPort);
    }

    [Fact]
    public async Task Internal_dials_are_not_limited_per_IP_while_the_public_bucket_of_the_same_IP_is()
    {
        await using var relay = await WithInternalAsync(o => o.RefusedDialsPerIpPerHour = 1);
        await using var chrome = await StreamTests.OnlineAsync(relay);
        using (var refused = await relay.DialAsync(chrome.Chrome.RelayId, "link", "wrong"))
        {
            Assert.Equal(RelayCloseCodes.DialRefused, (await Wire.ClosedAsync(refused)).Code);
        }

        using (var limited = await relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Link.Token))
        {
            Assert.Equal(RelayCloseCodes.DialerLimited, (await Wire.ClosedAsync(limited)).Code); // public: 127.0.0.1 is spent
        }

        for (var i = 0; i < 3; i++)
        {
            using var wrong = await DialInternalAsync(relay, chrome.Chrome.RelayId, "link", "wrong");
            Assert.Equal(RelayCloseCodes.DialRefused, (await Wire.ClosedAsync(wrong)).Code); // never 4530
        }

        using var dial = await DialInternalAsync(relay, chrome.Chrome.RelayId, "link", chrome.Link.Token);
        var incoming = await chrome.Chrome.IncomingAsync();
        using var ext = await chrome.Chrome.AcceptAsync(incoming.StreamId); // the extension accepts on the public port
        Assert.IsType<RelayStreamOpen>(await Wire.ReceiveAsync(dial));
    }

    [Fact]
    public async Task Per_relayId_admission_still_applies_on_the_internal_listener()
    {
        await using var relay = await WithInternalAsync();
        var (key, relayId, link, _) = StreamTests.Identity();
        using var _k = key;
        using var offline = await DialInternalAsync(relay, relayId, "link", link.Token);
        Assert.Equal(RelayCloseCodes.NotOnline, (await Wire.ClosedAsync(offline)).Code);
    }

    [Theory]
    [InlineData("/v1/browser")]
    [InlineData("/v1/stream?accept=AAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("/v1/other")]
    [InlineData("/v1/browser?dial=AAAAAAAAAAAAAAAAAAAAAA&kind=link")] // a dial query on another path
    [InlineData("/v1/stream/?dial=AAAAAAAAAAAAAAAAAAAAAA&kind=link")]
    public async Task The_internal_listener_serves_only_dials(string pathAndQuery)
    {
        await using var relay = await WithInternalAsync();
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", RelayFixture.ExtOrigin);
        ws.Options.CollectHttpResponseDetails = true;
        await Assert.ThrowsAsync<WebSocketException>(() => ws.ConnectAsync(Internal(relay, pathAndQuery), TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.NotFound, ws.HttpStatusCode);
    }

    [Fact]
    public async Task The_internal_listener_never_warns_about_a_missing_forwarded_header()
    {
        await using var relay = await WithInternalAsync(o =>
        {
            o.TrustedProxies.Add(IPNetwork.Parse("127.0.0.1/32"));
            o.ControlIdleTimeout = TimeSpan.FromHours(1); // the Chrome stays online across the 2-minute advances below
        });
        await using var chrome = await StreamTests.OnlineAsync(relay);
        var before = relay.Logs.Lines.Count(l => l.Contains("trusted proxy sent no client IP", StringComparison.Ordinal));
        for (var i = 0; i < 2; i++)
        {
            relay.Time.Advance(TimeSpan.FromMinutes(2)); // a warning would not be held back by the once-a-minute rule
            using var wrong = await DialInternalAsync(relay, chrome.Chrome.RelayId, "link", "wrong");
            Assert.Equal(RelayCloseCodes.DialRefused, (await Wire.ClosedAsync(wrong)).Code);
        }

        Assert.Equal(before, relay.Logs.Lines.Count(l => l.Contains("trusted proxy sent no client IP", StringComparison.Ordinal)));
    }
}
