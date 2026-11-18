using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using Panda.Bridge.Auth;
using Panda.Bridge.Protocol;
using Panda.Bridge.Relay;

namespace Panda.Relay.Tests;

/// <summary>plan-relay P4, seam S1: the extension's control socket /v1/browser and the relay's other paths.</summary>
public class ControlSocketTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/")]
    [InlineData("/v1")]
    [InlineData("/v1/browser/x")]
    [InlineData("/index.html")]
    public async Task Every_other_path_is_404(string path)
    {
        await using var relay = await RelayFixture.StartAsync();
        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri($"http://127.0.0.1:{relay.Server.Port}{path}"), Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/v1/browser")]
    [InlineData("/v1/stream?dial=AAAAAAAAAAAAAAAAAAAAAA&kind=link")]
    public async Task A_plain_HTTP_request_on_a_socket_path_is_400(string path)
    {
        await using var relay = await RelayFixture.StartAsync();
        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri($"http://127.0.0.1:{relay.Server.Port}{path}"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_valid_registration_gets_the_relayId_of_its_key_and_a_fresh_nonce_each_time()
    {
        await using var relay = await RelayFixture.StartAsync();
        await using var chrome = new FakeChrome(relay, ECDsa.Create(ECCurve.NamedCurves.nistP256));
        var first = await chrome.OpenAsync();
        await Wire.SendAsync(chrome.Control, chrome.Register(first));
        var registered = Assert.IsType<RelayRegistered>(await Wire.ReceiveAsync(chrome.Control));
        Assert.Equal(DialTokens.RelayIdFromSpki(chrome.Spki), registered.RelayId);

        await using var other = new FakeChrome(relay, ECDsa.Create(ECCurve.NamedCurves.nistP256));
        Assert.NotEqual(first, await other.OpenAsync());
    }

    [Fact]
    public async Task A_signature_over_another_host_is_closed_4503()
    {
        await using var relay = await RelayFixture.StartAsync();
        await using var chrome = new FakeChrome(relay, ECDsa.Create(ECCurve.NamedCurves.nistP256));
        var nonce = await chrome.OpenAsync();
        await Wire.SendAsync(chrome.Control, chrome.Register(nonce, host: "evil.test:443"));
        Assert.Equal(RelayCloseCodes.RegistrationInvalid, (await Wire.ClosedAsync(chrome.Control)).Code);
    }

    [Fact]
    public async Task A_signature_over_another_nonce_is_closed_4503()
    {
        await using var relay = await RelayFixture.StartAsync();
        await using var chrome = new FakeChrome(relay, ECDsa.Create(ECCurve.NamedCurves.nistP256));
        var nonce = await chrome.OpenAsync();
        nonce[0] ^= 1;
        await Wire.SendAsync(chrome.Control, chrome.Register(nonce));
        Assert.Equal(RelayCloseCodes.RegistrationInvalid, (await Wire.ClosedAsync(chrome.Control)).Code);
    }

    [Fact]
    public async Task A_key_signed_by_another_key_is_closed_4503()
    {
        await using var relay = await RelayFixture.StartAsync();
        await using var chrome = new FakeChrome(relay, ECDsa.Create(ECCurve.NamedCurves.nistP256));
        using var thief = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var nonce = await chrome.OpenAsync();
        var honest = chrome.Register(nonce);
        var sig = thief.SignData(FakeChrome.SignedBytes(nonce, RelayFixture.PublicHost), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        await Wire.SendAsync(chrome.Control, honest with { Sig = AuthCrypto.EncodeBase64Url(sig) });
        Assert.Equal(RelayCloseCodes.RegistrationInvalid, (await Wire.ClosedAsync(chrome.Control)).Code);
    }

    [Fact]
    public async Task An_spki_that_does_not_import_as_a_P256_key_is_closed_4503()
    {
        await using var relay = await RelayFixture.StartAsync();
        await using var chrome = new FakeChrome(relay, ECDsa.Create(ECCurve.NamedCurves.nistP256));
        var nonce = await chrome.OpenAsync();
        var spki = (byte[])chrome.Spki.Clone();
        spki[22] ^= 0x0F; // the curve OID's last byte: 1.2.840.10045.3.1.7 → …3.1.8, an unknown curve, same length
        var register = chrome.Register(nonce) with { Spki = AuthCrypto.EncodeBase64Url(spki) };
        await Wire.SendAsync(chrome.Control, register);
        Assert.Equal(RelayCloseCodes.RegistrationInvalid, (await Wire.ClosedAsync(chrome.Control)).Code);
    }

    [Theory]
    [InlineData("{\"type\":\"RELAY_PING\"}")]
    [InlineData("not json")]
    [InlineData("{\"type\":\"RELAY_REGISTER\"}")]
    [InlineData("{\"type\":\"HELLO\"}")]
    public async Task A_first_message_that_is_not_a_valid_REGISTER_is_closed_4500(string text)
    {
        await using var relay = await RelayFixture.StartAsync();
        await using var chrome = new FakeChrome(relay, ECDsa.Create(ECCurve.NamedCurves.nistP256));
        await chrome.OpenAsync();
        await Wire.SendTextAsync(chrome.Control, text);
        Assert.Equal(RelayCloseCodes.ProtocolError, (await Wire.ClosedAsync(chrome.Control)).Code);
    }

    [Fact]
    public async Task A_binary_frame_on_the_control_socket_is_closed_4500()
    {
        await using var relay = await RelayFixture.StartAsync();
        await using var chrome = await relay.ChromeAsync();
        await Wire.SendBinaryAsync(chrome.Control, [1, 2, 3]);
        Assert.Equal(RelayCloseCodes.ProtocolError, (await Wire.ClosedAsync(chrome.Control)).Code);
    }

    [Fact]
    public async Task No_REGISTER_within_the_register_timeout_is_closed_4500()
    {
        await using var relay = await RelayFixture.StartAsync();
        await using var chrome = new FakeChrome(relay, ECDsa.Create(ECCurve.NamedCurves.nistP256));
        var nonce = await chrome.OpenAsync();
        relay.Time.Advance(TimeSpan.FromSeconds(9));
        await Wire.SendAsync(chrome.Control, chrome.Register(nonce)); // still in time at 9 s
        Assert.IsType<RelayRegistered>(await Wire.ReceiveAsync(chrome.Control));

        await using var late = new FakeChrome(relay, ECDsa.Create(ECCurve.NamedCurves.nistP256));
        await late.OpenAsync();
        relay.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(RelayCloseCodes.ProtocolError, (await Wire.ClosedAsync(late.Control)).Code);
    }

    [Fact]
    public async Task PING_gets_PONG_and_DIAL_TOKENS_gets_TOKENS_OK_with_its_seq()
    {
        await using var relay = await RelayFixture.StartAsync();
        await using var chrome = await relay.ChromeAsync();
        await Wire.SendAsync(chrome.Control, new RelayPing());
        Assert.IsType<RelayPong>(await Wire.ReceiveAsync(chrome.Control));
        await Wire.SendAsync(chrome.Control, new RelayDialTokens(7));
        Assert.Equal(7, Assert.IsType<RelayTokensOk>(await Wire.ReceiveAsync(chrome.Control)).Seq);
        await Wire.SendAsync(chrome.Control, new RelayPong()); // an answer to the relay's own ping: ignored
        await Wire.SendAsync(chrome.Control, new RelayDialTokens(8));
        Assert.Equal(8, Assert.IsType<RelayTokensOk>(await Wire.ReceiveAsync(chrome.Control)).Seq);
    }

    [Theory]
    [InlineData("{\"type\":\"RELAY_REGISTERED\",\"relayId\":\"AAAAAAAAAAAAAAAAAAAAAA\"}")]
    [InlineData("{\"type\":\"RELAY_DIAL_TOKENS\",\"seq\":0}")] // seq ≥ 1: not a valid DIAL_TOKENS
    [InlineData("{\"type\":\"RELAY_STREAM_OPEN\"}")]
    [InlineData("not json")]
    public async Task A_message_that_is_not_a_valid_extension_message_after_registration_is_closed_4500(string text)
    {
        await using var relay = await RelayFixture.StartAsync();
        await using var chrome = await relay.ChromeAsync();
        await Wire.SendTextAsync(chrome.Control, text);
        Assert.Equal(RelayCloseCodes.ProtocolError, (await Wire.ClosedAsync(chrome.Control)).Code);
    }

    [Fact]
    public async Task A_second_REGISTER_on_the_same_socket_is_closed_4500()
    {
        await using var relay = await RelayFixture.StartAsync();
        await using var chrome = await relay.ChromeAsync();
        await Wire.SendAsync(chrome.Control, chrome.Register(new byte[32]));
        Assert.Equal(RelayCloseCodes.ProtocolError, (await Wire.ClosedAsync(chrome.Control)).Code);
    }

    [Fact]
    public async Task A_newer_registration_of_the_same_key_closes_the_older_socket_4507()
    {
        await using var relay = await RelayFixture.StartAsync();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var older = await relay.ChromeAsync(key: ECDsa.Create(key.ExportParameters(includePrivateParameters: true)));
        await using var newer = await relay.ChromeAsync(key: ECDsa.Create(key.ExportParameters(includePrivateParameters: true)));
        Assert.Equal(RelayCloseCodes.Replaced, (await Wire.ClosedAsync(older.Control)).Code);
        await Wire.SendAsync(newer.Control, new RelayPing());
        Assert.IsType<RelayPong>(await Wire.ReceiveAsync(newer.Control));
    }

    [Fact]
    public async Task A_control_socket_silent_for_the_idle_timeout_is_closed_1001()
    {
        await using var relay = await RelayFixture.StartAsync();
        await using var chrome = await relay.ChromeAsync();
        relay.Time.Advance(TimeSpan.FromSeconds(50));
        await Wire.SendAsync(chrome.Control, new RelayPing()); // any message restarts the idle clock
        Assert.IsType<RelayPong>(await Wire.ReceiveAsync(chrome.Control));
        relay.Time.Advance(TimeSpan.FromSeconds(50));
        await Wire.SendAsync(chrome.Control, new RelayPing());
        Assert.IsType<RelayPong>(await Wire.ReceiveAsync(chrome.Control));
        relay.Time.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal((int)WebSocketCloseStatus.EndpointUnavailable, (await Wire.ClosedAsync(chrome.Control)).Code);
    }

    [Fact]
    public async Task A_control_frame_over_the_cap_is_closed_4513()
    {
        await using var relay = await RelayFixture.StartAsync(o => o.MaxFrameBytes = 1024);
        await using var chrome = await relay.ChromeAsync();
        await Wire.SendTextAsync(chrome.Control, new string(' ', 1025));
        Assert.Equal(RelayCloseCodes.FrameTooLarge, (await Wire.ClosedAsync(chrome.Control)).Code);
    }

    [Fact]
    public async Task A_REGISTER_over_the_frame_cap_is_closed_4513()
    {
        await using var relay = await RelayFixture.StartAsync(o => o.MaxFrameBytes = 1024);
        await using var chrome = new FakeChrome(relay, ECDsa.Create(ECCurve.NamedCurves.nistP256));
        await chrome.OpenAsync();
        await Wire.SendTextAsync(chrome.Control, new string(' ', 1025));
        Assert.Equal(RelayCloseCodes.FrameTooLarge, (await Wire.ClosedAsync(chrome.Control)).Code);
    }

    [Fact]
    public async Task Registrations_over_the_IP_limit_are_closed_4530_with_the_seconds_left_in_the_hour()
    {
        await using var relay = await RelayFixture.StartAsync(o => o.RegistrationsPerIpPerHour = 2,
            now: new DateTimeOffset(2026, 10, 12, 12, 59, 0, TimeSpan.Zero));
        await using var a = await relay.ChromeAsync();
        await using var b = await relay.ChromeAsync();
        using var c = await relay.ConnectAsync("/v1/browser");
        var (code, reason) = await Wire.ClosedAsync(c);
        Assert.Equal(RelayCloseCodes.DialerLimited, code);
        Assert.Equal("60", reason);

        relay.Time.Advance(TimeSpan.FromSeconds(60)); // a new hour
        await using var d = await relay.ChromeAsync();
    }

    [Fact]
    public async Task Logs_never_carry_the_nonce_the_signature_or_the_key()
    {
        await using var relay = await RelayFixture.StartAsync();
        await using var chrome = new FakeChrome(relay, ECDsa.Create(ECCurve.NamedCurves.nistP256));
        var nonce = await chrome.OpenAsync();
        var register = chrome.Register(nonce);
        await Wire.SendAsync(chrome.Control, register);
        Assert.IsType<RelayRegistered>(await Wire.ReceiveAsync(chrome.Control));
        await Wire.SendAsync(chrome.Control, register with { Sig = AuthCrypto.EncodeBase64Url(new byte[64]) });
        await Wire.ClosedAsync(chrome.Control);

        var all = string.Join("\n", relay.Logs.Lines);
        Assert.Contains(chrome.RelayId, all, StringComparison.Ordinal); // the relayId is not a secret and is logged
        Assert.DoesNotContain(AuthCrypto.EncodeBase64Url(nonce), all, StringComparison.Ordinal);
        Assert.DoesNotContain(register.Sig, all, StringComparison.Ordinal);
        Assert.DoesNotContain(register.Spki, all, StringComparison.Ordinal);
    }
}
