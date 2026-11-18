using Panda.Bridge.Relay;

namespace Panda.Bridge.Tests;

/// <summary>plan-relay P5b2, seam S4: the connection code <c>panda-browser connect</c> takes.</summary>
public sealed class ConnectionCodeTests
{
    private const string RelayId = "AAAAAAAAAAAAAAAAAAAAAA";
    private const string Code = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    [Theory]
    [InlineData("panda-relay://relay.example.com/" + RelayId + "#" + Code, "wss://relay.example.com")]
    [InlineData("  panda-relay://Relay.Example.com:8443/" + RelayId + "#ABCDE-FGHIJ-KLMNO-PQRST-UVWXYZ \n", "wss://relay.example.com:8443")]
    [InlineData("panda-relay://relay.example.com:443/" + RelayId + "#" + Code, "wss://relay.example.com")]
    [InlineData("panda-relay+ws://127.0.0.1:9000/" + RelayId + "#" + Code, "ws://127.0.0.1:9000")]
    [InlineData("panda-relay+ws://localhost:9000/" + RelayId + "#" + Code, "ws://localhost:9000")]
    [InlineData("panda-relay+ws://[::1]:9000/" + RelayId + "#" + Code, "ws://[::1]:9000")]
    public void A_valid_code_gives_the_relay_origin_the_relayId_and_the_normalised_code(string input, string relayUrl)
    {
        Assert.True(ConnectionCode.TryParse(input, out var parsed));
        Assert.Equal((relayUrl, RelayId, Code), (parsed.RelayUrl, parsed.RelayId, parsed.Code));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wss://relay.example.com/" + RelayId + "#" + Code)] // another scheme
    [InlineData("panda-relay+ws://relay.example.com/" + RelayId + "#" + Code)] // plain ws off loopback
    [InlineData("panda-relay+ws://10.0.0.1:9000/" + RelayId + "#" + Code)]
    [InlineData("panda-relay://relay.example.com/" + RelayId)] // no code
    [InlineData("panda-relay://relay.example.com/" + RelayId + "#ABC")] // short code
    [InlineData("panda-relay://relay.example.com/" + RelayId + "#ABCDEFGHIJKLMNOPQRSTUVWXY0")] // not in the code alphabet
    [InlineData("panda-relay://relay.example.com/AAAA#" + Code)] // short relayId
    [InlineData("panda-relay://relay.example.com/AAAAAAAAAAAAAAAAAAAAA=#" + Code)]
    [InlineData("panda-relay://relay.example.com/x/" + RelayId + "#" + Code)] // extra path
    [InlineData("panda-relay://relay.example.com/" + RelayId + "?q=1#" + Code)] // query
    [InlineData("panda-relay://user@relay.example.com/" + RelayId + "#" + Code)] // user info
    [InlineData("panda-relay:///" + RelayId + "#" + Code)] // no host
    public void Anything_else_is_refused(string? input) =>
        Assert.False(ConnectionCode.TryParse(input, out _));
}

/// <summary>
/// plan-https-code T1 (D7, seam S1): the same code as a link, <c>https://host[:port]/c/&lt;relayId&gt;#&lt;code&gt;</c> —
/// taken only by <see cref="ConnectionCode.TryParseCodeOrLink"/> (the CLI's connect, /admin/relay/connect); the hosting
/// paths keep <see cref="ConnectionCode.TryParse"/>, which still refuses every link.
/// </summary>
public sealed class ConnectionCodeLinkTests
{
    private const string RelayId = "AAAAAAAAAAAAAAAAAAAAAA";
    private const string Code = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    [Theory]
    [InlineData("https://relay.example.com/c/" + RelayId + "#" + Code, "panda-relay://relay.example.com/" + RelayId + "#" + Code)]
    [InlineData("  https://Relay.Example.com:8443/c/" + RelayId + "#ABCDE-FGHIJ-KLMNO-PQRST-UVWXYZ \n", "panda-relay://relay.example.com:8443/" + RelayId + "#" + Code)]
    [InlineData("https://relay.example.com:443/c/" + RelayId + "#" + Code, "panda-relay://relay.example.com/" + RelayId + "#" + Code)]
    [InlineData("http://127.0.0.1:9000/c/" + RelayId + "#" + Code, "panda-relay+ws://127.0.0.1:9000/" + RelayId + "#" + Code)]
    [InlineData("http://localhost/c/" + RelayId + "#" + Code, "panda-relay+ws://localhost/" + RelayId + "#" + Code)]
    [InlineData("http://[::1]:9000/c/" + RelayId + "#" + Code, "panda-relay+ws://[::1]:9000/" + RelayId + "#" + Code)]
    [InlineData("https://relay.example.com/x/../c/" + RelayId + "#" + Code, "panda-relay://relay.example.com/" + RelayId + "#" + Code)] // Uri drops dot segments: the same id
    public void A_link_parses_to_the_same_record_as_its_panda_relay_twin(string link, string twin)
    {
        Assert.True(ConnectionCode.TryParse(twin, out var expected));
        Assert.True(ConnectionCode.TryParseCodeOrLink(link, out var parsed));
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData("panda-relay://relay.example.com/" + RelayId + "#" + Code)]
    [InlineData("panda-relay+ws://127.0.0.1:9000/" + RelayId + "#" + Code)]
    public void The_code_form_is_taken_as_TryParse_takes_it(string input)
    {
        Assert.True(ConnectionCode.TryParse(input, out var expected));
        Assert.True(ConnectionCode.TryParseCodeOrLink(input, out var parsed));
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://relay.example.com/c/" + RelayId + "#" + Code)] // plain http off loopback
    [InlineData("http://10.0.0.1:9000/c/" + RelayId + "#" + Code)]
    [InlineData("https://relay.example.com/" + RelayId + "#" + Code)] // no /c/
    [InlineData("https://relay.example.com/C/" + RelayId + "#" + Code)] // case
    [InlineData("https://relay.example.com/c/" + RelayId + "/#" + Code)] // trailing slash
    [InlineData("https://relay.example.com/c%2F" + RelayId + "#" + Code)] // an escaped slash
    [InlineData("https://relay.example.com/evil/c/" + RelayId + "#" + Code)] // extra segment
    [InlineData("https://relay.example.com/c/AAAA#" + Code)] // short relayId
    [InlineData("https://relay.example.com/c/" + RelayId)] // no code
    [InlineData("https://relay.example.com/c/" + RelayId + "#ABC")] // short code
    [InlineData("https://relay.example.com/c/" + RelayId + "?q=1#" + Code)] // query
    [InlineData("https://user@relay.example.com/c/" + RelayId + "#" + Code)] // user info
    [InlineData("wss://relay.example.com/c/" + RelayId + "#" + Code)] // another scheme
    [InlineData("ftp://relay.example.com/c/" + RelayId + "#" + Code)]
    public void Anything_else_is_refused(string? input) =>
        Assert.False(ConnectionCode.TryParseCodeOrLink(input, out _));

    [Theory]
    [InlineData("https://relay.example.com/c/" + RelayId + "#" + Code)]
    [InlineData("http://127.0.0.1:9000/c/" + RelayId + "#" + Code)]
    public void TryParse_still_refuses_every_link_the_hosting_paths_keep_the_code_form(string link) =>
        Assert.False(ConnectionCode.TryParse(link, out _));

    [Fact]
    public void A_parsed_link_never_prints_its_code()
    {
        Assert.True(ConnectionCode.TryParseCodeOrLink("https://relay.example.com/c/" + RelayId + "#" + Code, out var parsed));
        Assert.DoesNotContain(Code, parsed.ToString(), StringComparison.Ordinal);
    }
}
