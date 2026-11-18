using System.Net;

namespace Panda.Relay.Tests;

/// <summary>plan-relay P4: the <c>panda-relay</c> command line.</summary>
public class ProgramTests
{
    [Fact]
    public void Defaults_listen_on_loopback_8080()
    {
        Assert.True(Program.TryParse(["--public-host", "relay.example.com"], out var o, out var error));
        Assert.Null(error);
        Assert.Equal("relay.example.com", o!.PublicHost);
        Assert.Equal(IPAddress.Loopback, o.ListenAddress);
        Assert.Equal(8080, o.Port);
    }

    [Fact]
    public void Every_option_is_read()
    {
        Assert.True(Program.TryParse(["--port", "9000", "--listen", "0.0.0.0", "--public-host", "relay.example.com:8443"], out var o, out _));
        Assert.Equal("relay.example.com:8443", o!.PublicHost);
        Assert.Equal(IPAddress.Any, o.ListenAddress);
        Assert.Equal(9000, o.Port);
    }

    [Fact]
    public void Port_0_lets_the_relay_pick_a_free_port()
    {
        // plan-relay P7 (P7a critic F6): the E2E harness reads the bound port from "listening on" — no free-port race
        Assert.True(Program.TryParse(["--public-host", "relay.example.com", "--port", "0"], out var o, out _));
        Assert.Equal(0, o!.Port);
    }

    [Fact]
    public void Daily_quotas_are_read_and_default_to_the_options()
    {
        // plan-relay P7 (R6): the S6 numbers are config; an E2E relay needs small ones
        Assert.True(Program.TryParse(["--public-host", "relay.example.com", "--bytes-per-day", "200000", "--streams-per-day", "7"], out var o, out _));
        Assert.Equal(200_000L, o!.BytesPerDay);
        Assert.Equal(7, o.StreamsPerDay);
        Assert.True(Program.TryParse(["--public-host", "relay.example.com"], out var d, out _));
        Assert.Equal(new RelayOptions { PublicHost = "x" }.BytesPerDay, d!.BytesPerDay);
        Assert.Equal(new RelayOptions { PublicHost = "x" }.StreamsPerDay, d.StreamsPerDay);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--port|9000")]
    [InlineData("--public-host")]
    [InlineData("--public-host|relay.example.com|--port|65536")]
    [InlineData("--public-host|relay.example.com|--port|-1")]
    [InlineData("--public-host|relay.example.com|--listen|localhost")]
    [InlineData("--public-host|relay.example.com|--verbose")]
    [InlineData("--public-host|https://relay.example.com/")]
    [InlineData("--public-host|relay example")]
    [InlineData("--public-host|relay.example.com:99999")]
    [InlineData("--public-host|relay.example.com:0")]
    [InlineData("--public-host|relay.example.com|--bytes-per-day|0")]
    [InlineData("--public-host|relay.example.com|--bytes-per-day|-5")]
    [InlineData("--public-host|relay.example.com|--bytes-per-day|1e6")]
    [InlineData("--public-host|relay.example.com|--streams-per-day|0")]
    [InlineData("--public-host|relay.example.com|--streams-per-day|99999999999")]
    [InlineData("--public-host|relay.example.com|--streams-per-day")]
    [InlineData("--public-host|relay.example.com|--trusted-proxy|127.0.0.1/33")]
    [InlineData("--public-host|relay.example.com|--trusted-proxy|10.0.0.1/8")] // host bits set: not a network
    [InlineData("--public-host|relay.example.com|--trusted-proxy|localhost")]
    [InlineData("--public-host|relay.example.com|--trusted-proxy")]
    [InlineData("--public-host|relay.example.com|--internal-port|0")] // the hosted bridge needs a fixed port to dial
    [InlineData("--public-host|relay.example.com|--internal-port|65536")]
    [InlineData("--public-host|relay.example.com|--internal-port|8080")] // the public --port
    [InlineData("--public-host|relay.example.com|--internal-port")]
    [InlineData("--public-host|relay.example.com|--noise-per-ip-per-hour|-1")]
    [InlineData("--public-host|relay.example.com|--noise-per-ip-per-hour")]
    [InlineData("--public-host|relay.example.com|--max-hosting|0")]
    [InlineData("--public-host|relay.example.com|--max-hosting|-1")]
    [InlineData("--public-host|relay.example.com|--max-hosting")]
    [InlineData("--public-host|relay.example.com|--trusted-proxy|010.0.0.0/8")] // octal: would trust 8.0.0.0/8
    [InlineData("--public-host|relay.example.com|--trusted-proxy|010.000.000.001")]
    [InlineData("--public-host|relay.example.com|--trusted-proxy|127.1")]
    [InlineData("--public-host|relay.example.com|--trusted-proxy|0.0.0.0/0")] // would let every peer pick its bucket
    [InlineData("--public-host|relay.example.com|--trusted-proxy|::/0")]
    [InlineData("--public-host|relay.example.com|--trusted-proxy|::ffff:1.2.3.4")] // never matches an IPv4 peer
    [InlineData("--public-host|relay.example.com|--client-ip-header|CF-Connecting-IP")] // no proxy to trust
    [InlineData("--public-host|relay.example.com|--trusted-proxy|127.0.0.1|--client-ip-header|X Bad")]
    [InlineData("--public-host|relay.example.com|--trusted-proxy|127.0.0.1|--client-ip-header|")]
    public void Bad_arguments_are_refused_with_a_message(string joined)
    {
        var args = joined.Length == 0 ? [] : joined.Split('|');
        Assert.False(Program.TryParse(args, out var o, out var error));
        Assert.Null(o);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void The_internal_port_is_read_and_absent_by_default()
    {
        // plan-relay-deploy C5 (D5)
        Assert.True(Program.TryParse(["--public-host", "relay.example.com", "--internal-port", "8081"], out var o, out _));
        Assert.Equal(8081, o!.InternalPort);
        Assert.True(Program.TryParse(["--public-host", "relay.example.com"], out var d, out _));
        Assert.Null(d!.InternalPort);
    }

    [Fact]
    public void The_noise_budget_is_read_and_defaults_to_600()
    {
        // plan-relay-deploy C6 (D5); 0 = any IP the relay has seen this hour is limited (like RefusedDialsPerIpPerHour 0)
        Assert.True(Program.TryParse(["--public-host", "relay.example.com", "--noise-per-ip-per-hour", "0"], out var o, out _));
        Assert.Equal(0, o!.NoisePerIpPerHour);
        Assert.True(Program.TryParse(["--public-host", "relay.example.com"], out var d, out _));
        Assert.Equal(600, d!.NoisePerIpPerHour);
    }

    [Fact]
    public void Max_hosting_is_read_and_defaults_to_32()
    {
        // plan-relay-deploy C3 (D3)
        Assert.True(Program.TryParse(["--public-host", "relay.example.com", "--max-hosting", "5"], out var o, out _));
        Assert.Equal(5, o!.MaxHosting);
        Assert.True(Program.TryParse(["--public-host", "relay.example.com"], out var d, out _));
        Assert.Equal(32, d!.MaxHosting);
    }

    // ---- plan-relay-deploy C2: --trusted-proxy (repeatable) / --client-ip-header ------------------------------------

    [Fact]
    public void Trusted_proxies_are_read_as_networks_and_a_bare_address_is_a_host_network()
    {
        Assert.True(Program.TryParse(
            ["--public-host", "relay.example.com", "--trusted-proxy", "127.0.0.1", "--trusted-proxy", "10.0.0.0/8",
                "--trusted-proxy", "::1", "--client-ip-header", "CF-Connecting-IP"], out var o, out var error), error);
        Assert.Equal([IPNetwork.Parse("127.0.0.1/32"), IPNetwork.Parse("10.0.0.0/8"), IPNetwork.Parse("::1/128")], o!.TrustedProxies);
        Assert.Equal("CF-Connecting-IP", o.ClientIpHeader);
    }

    [Fact]
    public void Without_trusted_proxies_the_relay_trusts_none_and_reads_X_Forwarded_For_if_given_one()
    {
        Assert.True(Program.TryParse(["--public-host", "relay.example.com"], out var o, out _));
        Assert.Empty(o!.TrustedProxies);
        Assert.Equal("X-Forwarded-For", o.ClientIpHeader);
    }

    // ---- plan-relay-url U3: --hosted-admin-url / --hosted-admin-token-file / --hosted-mcp-url (all or none) ----------

    private static readonly string Token = new('A', 43);

    [Fact]
    public void The_hosted_flags_are_read_and_the_token_comes_from_its_file()
    {
        var file = Path.GetTempFileName();
        File.WriteAllText(file, Token + Environment.NewLine); // the file may end in a newline
        try
        {
            Assert.True(Program.TryParse(
                ["--public-host", "relay.example.com", "--hosted-admin-url", "http://127.0.0.1:47822", "--hosted-admin-token-file", file,
                    "--hosted-mcp-url", "https://relay.example.com/mcp"], out var o, out var error), error);

            Assert.Equal((new Uri("http://127.0.0.1:47822"), Token, "https://relay.example.com/mcp"),
                (o!.HostedAdminUrl, o.HostedAdminToken, o.HostedMcpUrl));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Without_the_hosted_flags_the_relay_has_no_hosted_bridge()
    {
        Assert.True(Program.TryParse(["--public-host", "relay.example.com"], out var o, out _));

        Assert.Equal((null, null, null), (o!.HostedAdminUrl, o.HostedAdminToken, o.HostedMcpUrl));
    }

    [Theory]
    [InlineData(true, "--hosted-admin-url|http://127.0.0.1:47822|--hosted-mcp-url|https://relay.example.com/mcp")] // no token file
    [InlineData(true, "--hosted-admin-url|not a url|--hosted-admin-token-file|{file}|--hosted-mcp-url|https://relay.example.com/mcp")]
    [InlineData(true, "--hosted-admin-url|http://127.0.0.1:47822|--hosted-admin-token-file|{missing}|--hosted-mcp-url|https://relay.example.com/mcp")]
    [InlineData(false, "--hosted-admin-url|http://127.0.0.1:47822|--hosted-admin-token-file|{file}|--hosted-mcp-url|https://relay.example.com/mcp")] // the file holds no token
    public void Bad_hosted_flags_are_refused_without_printing_the_file(bool goodToken, string joined)
    {
        var file = Path.GetTempFileName();
        File.WriteAllText(file, goodToken ? Token : "secret-but-not-a-token");
        try
        {
            var args = ("--public-host|relay.example.com|" + joined).Replace("{file}", file, StringComparison.Ordinal)
                .Replace("{missing}", file + ".missing", StringComparison.Ordinal).Split('|');

            Assert.False(Program.TryParse(args, out var o, out var error));
            Assert.Null(o);
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.DoesNotContain(Token, error, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-but-not-a-token", error, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
