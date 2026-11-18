using System.Net;
using System.Net.WebSockets;
using Panda.Bridge.Protocol;

namespace Panda.Relay.Tests;

/// <summary>
/// plan-relay-deploy C2, seam S9: behind a trusted TLS proxy the per-IP limits use the client IP the proxy forwards
/// (ASP.NET ForwardedHeadersMiddleware, ForwardLimit 1 = the right-most address); any other peer chooses nothing.
/// One registration per IP per hour makes the bucket visible: the second one in a bucket is closed 4530.
/// </summary>
public class ClientIpTests
{
    private static readonly IPNetwork Loopback = IPNetwork.Parse("127.0.0.1/32");

    private static Task<RelayFixture> BehindProxyAsync(Action<RelayOptions>? configure = null) =>
        RelayFixture.StartAsync(o =>
        {
            o.RegistrationsPerIpPerHour = 1;
            o.TrustedProxies.Add(Loopback);
            configure?.Invoke(o);
        });

    /// <summary>True = admitted (CHALLENGE), false = closed 4530.</summary>
    private static async Task<bool> AdmittedAsync(RelayFixture relay, params string[] headerLines)
    {
        using var ws = await relay.RawConnectAsync("/v1/browser", headerLines);
        var (type, _) = await Wire.ReceiveRawAsync(ws);
        if (type == WebSocketMessageType.Text)
        {
            return true;
        }

        Assert.Equal(RelayCloseCodes.DialerLimited, (int)ws.CloseStatus!.Value);
        return false;
    }

    [Fact]
    public async Task A_trusted_proxy_s_X_Forwarded_For_picks_the_bucket()
    {
        await using var relay = await BehindProxyAsync();
        Assert.True(await AdmittedAsync(relay, "X-Forwarded-For: 203.0.113.5"));
        Assert.True(await AdmittedAsync(relay, "X-Forwarded-For: 203.0.113.6"));
        Assert.False(await AdmittedAsync(relay, "X-Forwarded-For: 203.0.113.5"));
    }

    [Fact]
    public async Task An_address_with_a_port_counts_without_its_port()
    {
        await using var relay = await BehindProxyAsync();
        Assert.True(await AdmittedAsync(relay, "X-Forwarded-For: 203.0.113.7:4711"));
        Assert.True(await AdmittedAsync(relay, "X-Forwarded-For: 203.0.113.70:4711"));
        Assert.False(await AdmittedAsync(relay, "X-Forwarded-For: 203.0.113.7:4712"));
    }

    [Fact]
    public async Task A_bracketed_IPv6_with_a_port_counts_in_its_64()
    {
        await using var relay = await BehindProxyAsync();
        Assert.True(await AdmittedAsync(relay, "X-Forwarded-For: [2001:db8:1:2::1]:443"));
        Assert.True(await AdmittedAsync(relay, "X-Forwarded-For: 2001:db8:1:3::1")); // another /64
        Assert.False(await AdmittedAsync(relay, "X-Forwarded-For: 2001:db8:1:2::99"));
    }

    [Fact]
    public async Task The_right_most_address_wins_across_a_list_and_across_header_lines()
    {
        await using var relay = await BehindProxyAsync();
        Assert.True(await AdmittedAsync(relay, "X-Forwarded-For: 198.51.100.1, 203.0.113.8")); // a client-sent left part
        Assert.True(await AdmittedAsync(relay, "X-Forwarded-For: 203.0.113.8, 198.51.100.1")); // right-most differs
        Assert.False(await AdmittedAsync(relay, "X-Forwarded-For: 198.51.100.2", "X-Forwarded-For: 203.0.113.8"));
    }

    [Fact]
    public async Task Only_one_hop_is_read_even_when_the_right_most_address_is_itself_a_trusted_proxy()
    {
        // ForwardLimit 1: "client, 127.0.0.1" gives 127.0.0.1, never the client-chosen left part
        await using var relay = await BehindProxyAsync();
        Assert.True(await AdmittedAsync(relay, "X-Forwarded-For: 203.0.113.20, 127.0.0.1"));
        Assert.False(await AdmittedAsync(relay, "X-Forwarded-For: 203.0.113.21, 127.0.0.1"));
    }

    [Fact]
    public async Task CF_Connecting_IP_is_read_when_configured_and_X_Forwarded_For_is_then_ignored()
    {
        await using var relay = await BehindProxyAsync(o => o.ClientIpHeader = "CF-Connecting-IP");
        Assert.True(await AdmittedAsync(relay, "CF-Connecting-IP: 203.0.113.9", "X-Forwarded-For: 198.51.100.3"));
        Assert.True(await AdmittedAsync(relay, "CF-Connecting-IP: 203.0.113.10", "X-Forwarded-For: 198.51.100.3"));
        Assert.False(await AdmittedAsync(relay, "CF-Connecting-IP: 203.0.113.9", "X-Forwarded-For: 198.51.100.4"));
    }

    [Fact]
    public async Task Without_a_trusted_proxy_the_header_is_ignored()
    {
        await using var relay = await RelayFixture.StartAsync(o => o.RegistrationsPerIpPerHour = 1);
        Assert.True(await AdmittedAsync(relay, "X-Forwarded-For: 203.0.113.11"));
        Assert.False(await AdmittedAsync(relay, "X-Forwarded-For: 203.0.113.12")); // both are the loopback peer
    }

    [Fact]
    public async Task Loopback_is_not_trusted_unless_configured()
    {
        // the middleware's defaults trust 127.0.0.0/8 and ::1; the relay trusts only --trusted-proxy (critic round 2, N3)
        await using var relay = await RelayFixture.StartAsync(o =>
        {
            o.RegistrationsPerIpPerHour = 1;
            o.TrustedProxies.Add(IPNetwork.Parse("10.0.0.0/8"));
        });
        Assert.True(await AdmittedAsync(relay, "X-Forwarded-For: 203.0.113.13"));
        Assert.False(await AdmittedAsync(relay, "X-Forwarded-For: 203.0.113.14"));
        Assert.Empty(Warnings(relay)); // an untrusted peer is not a proxy that forgot its header
    }

    [Fact]
    public async Task A_trusted_proxy_without_a_usable_header_warns_once_a_minute_with_the_count_and_no_header_value()
    {
        await using var relay = await BehindProxyAsync(o => o.RegistrationsPerIpPerHour = 100);
        Assert.True(await AdmittedAsync(relay));
        Assert.True(await AdmittedAsync(relay, "X-Forwarded-For: not-an-address"));
        Assert.True(await AdmittedAsync(relay, "X-Forwarded-For: 203.0.113.15")); // a good one: no warning
        Assert.Single(Warnings(relay));

        relay.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.True(await AdmittedAsync(relay));
        var warnings = Warnings(relay);
        Assert.Equal(2, warnings.Count);
        Assert.Contains("since the last warning: 2", warnings[1], StringComparison.Ordinal); // the one held back + this one
        Assert.DoesNotContain(relay.Logs.Lines, l => l.Contains("not-an-address", StringComparison.Ordinal));
    }

    private static List<string> Warnings(RelayFixture relay) =>
        [.. relay.Logs.Lines.Where(l => l.Contains("trusted proxy sent no client IP", StringComparison.Ordinal))];
}
