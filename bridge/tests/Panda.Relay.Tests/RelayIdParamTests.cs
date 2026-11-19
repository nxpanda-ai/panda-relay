using System.Security.Cryptography;
using Panda.Bridge.Protocol;
using Panda.Bridge.Relay;

namespace Panda.Relay.Tests;

/// <summary>plan-relay-deploy C1, seam S1 v1.11: the optional relayId `id` on /v1/browser and /v1/stream?accept=.</summary>
public class RelayIdParamTests
{
    private const string OtherId = "AAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public async Task A_control_socket_whose_id_is_its_own_relayId_registers()
    {
        await using var relay = await RelayFixture.StartAsync();
        await using var chrome = new FakeChrome(relay, ECDsa.Create(ECCurve.NamedCurves.nistP256));
        var nonce = await chrome.OpenAsync($"?id={chrome.RelayId}");
        await Wire.SendAsync(chrome.Control, chrome.Register(nonce));
        Assert.Equal(chrome.RelayId, Assert.IsType<RelayRegistered>(await Wire.ReceiveAsync(chrome.Control)).RelayId);
    }

    [Fact]
    public async Task A_control_socket_whose_id_is_another_relayId_is_closed_4503_and_never_goes_online()
    {
        await using var relay = await RelayFixture.StartAsync();
        await using var chrome = new FakeChrome(relay, ECDsa.Create(ECCurve.NamedCurves.nistP256));
        var nonce = await chrome.OpenAsync($"?id={OtherId}");
        await Wire.SendAsync(chrome.Control, chrome.Register(nonce));
        Assert.Equal(RelayCloseCodes.RegistrationInvalid, (await Wire.ClosedAsync(chrome.Control)).Code);

        using var dial = await relay.DialAsync(chrome.RelayId, "link", null);
        Assert.Equal(RelayCloseCodes.NotOnline, (await Wire.ClosedAsync(dial)).Code);
    }

    [Theory]
    [InlineData("?id=short")]
    [InlineData("?id=")]
    [InlineData("?id=AAAAAAAAAAAAAAAAAAAAAA&id=AAAAAAAAAAAAAAAAAAAAAA")]
    public async Task A_control_socket_with_a_malformed_or_repeated_id_is_closed_4500(string query)
    {
        await using var relay = await RelayFixture.StartAsync();
        using var ws = await relay.ConnectAsync("/v1/browser" + query);
        Assert.Equal(RelayCloseCodes.ProtocolError, (await Wire.ClosedAsync(ws)).Code);
    }

    [Fact]
    public async Task An_accept_whose_id_is_the_stream_owner_opens_the_stream()
    {
        await using var relay = await RelayFixture.StartAsync();
        await using var chrome = await RelayClient.OnlineAsync(relay);
        using var dial = await relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Link.Token);
        var incoming = await chrome.Chrome.IncomingAsync();
        using var ext = await chrome.Chrome.AcceptAsync(incoming.StreamId, $"&id={chrome.Chrome.RelayId}");
        Assert.IsType<RelayStreamOpen>(await Wire.ReceiveAsync(dial));
    }

    [Fact]
    public async Task An_accept_whose_id_is_not_the_owner_is_closed_4504_and_leaves_the_stream_to_its_real_accept()
    {
        await using var relay = await RelayFixture.StartAsync();
        await using var chrome = await RelayClient.OnlineAsync(relay);
        using var dial = await relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Link.Token);
        var incoming = await chrome.Chrome.IncomingAsync();

        using var wrong = await chrome.Chrome.AcceptAsync(incoming.StreamId, $"&id={OtherId}");
        Assert.Equal(RelayCloseCodes.NotOnline, (await Wire.ClosedAsync(wrong)).Code);

        using var ext = await chrome.Chrome.AcceptAsync(incoming.StreamId, $"&id={chrome.Chrome.RelayId}");
        Assert.IsType<RelayStreamOpen>(await Wire.ReceiveAsync(dial));
    }

    [Theory]
    [InlineData("&id=short")]
    [InlineData("&id=AAAAAAAAAAAAAAAAAAAAAA&id=AAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("&other=AAAAAAAAAAAAAAAAAAAAAA")]
    public async Task An_accept_with_a_malformed_repeated_or_unknown_parameter_is_closed_4500(string query)
    {
        await using var relay = await RelayFixture.StartAsync();
        using var ws = await relay.ConnectAsync("/v1/stream?accept=AAAAAAAAAAAAAAAAAAAAAA" + query);
        Assert.Equal(RelayCloseCodes.ProtocolError, (await Wire.ClosedAsync(ws)).Code);
    }
}
