using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Panda.Bridge.Auth;
using Panda.Bridge.Protocol;
using Panda.Bridge.Relay;

namespace Panda.Relay.Conformance;

/// <summary>
/// G2 (spec §78 S1): the extension's control socket <c>/v1/browser</c> — Origin rules, the signed registration, the
/// close codes of a broken or late conversation, PING / DIAL_TOKENS, replacement, the frame cap, <c>?id=</c>, and
/// RELAY_HOST on a relay with no hosted bridge.
/// </summary>
public class ControlSocketTests(ClassRelay fixture) : IClassFixture<ClassRelay>
{
    private const string OtherId = "AAAAAAAAAAAAAAAAAAAAAA";
    private const int FrameCap = 512 * 1024; // S1: a frame over 512 KiB closes 4513

    private RelayProcess Relay => fixture.Relay;

    private FakeChrome NewChrome() => new(Relay, ECDsa.Create(ECCurve.NamedCurves.nistP256));

    [Theory]
    [InlineData("/v1/browser", null)]
    [InlineData("/v1/browser", "https://evil.example")]
    [InlineData("/v1/browser", "null")]
    [InlineData("/v1/browser", "chrome-extension://abc")]
    [InlineData("/v1/stream?accept=AAAAAAAAAAAAAAAAAAAAAA", null)]
    [InlineData("/v1/stream?accept=AAAAAAAAAAAAAAAAAAAAAA", "https://evil.example")]
    [InlineData("/v1/stream?dial=AAAAAAAAAAAAAAAAAAAAAA&kind=link", "https://evil.example")]
    [InlineData("/v1/stream?dial=AAAAAAAAAAAAAAAAAAAAAA&kind=link", RelayClient.ExtOrigin)]
    public async Task An_upgrade_with_the_wrong_Origin_for_its_path_is_403(string path, string? origin) =>
        Assert.Equal(HttpStatusCode.Forbidden, await Relay.RefusedUpgradeAsync(path, origin));

    [Fact]
    public async Task A_valid_registration_gets_the_relayId_of_its_key_and_a_fresh_nonce_each_time()
    {
        await using var chrome = NewChrome();
        var first = await chrome.OpenAsync();
        await Wire.SendAsync(chrome.Control, chrome.Register(first));
        var registered = Assert.IsType<RelayRegistered>(await Wire.ReceiveAsync(chrome.Control));
        Assert.Equal(DialTokens.RelayIdFromSpki(chrome.Spki), registered.RelayId); // the client's derivation is pinned by the vectors

        await using var other = NewChrome();
        Assert.NotEqual(first, await other.OpenAsync());
    }

    [Fact]
    public async Task A_signature_over_another_host_is_closed_4503()
    {
        await using var chrome = NewChrome();
        var nonce = await chrome.OpenAsync();
        await Wire.SendAsync(chrome.Control, chrome.Register(nonce, host: "evil.test:443"));
        Assert.Equal(RelayCloseCodes.RegistrationInvalid, (await Wire.ClosedAsync(chrome.Control)).Code);
    }

    [Fact]
    public async Task A_signature_over_another_nonce_is_closed_4503()
    {
        await using var chrome = NewChrome();
        var nonce = await chrome.OpenAsync();
        nonce[0] ^= 1;
        await Wire.SendAsync(chrome.Control, chrome.Register(nonce));
        Assert.Equal(RelayCloseCodes.RegistrationInvalid, (await Wire.ClosedAsync(chrome.Control)).Code);
    }

    [Fact]
    public async Task A_key_signed_by_another_key_is_closed_4503()
    {
        await using var chrome = NewChrome();
        using var thief = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var nonce = await chrome.OpenAsync();
        var sig = thief.SignData(FakeChrome.SignedBytes(nonce, Relay.PublicHost), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        await Wire.SendAsync(chrome.Control, chrome.Register(nonce) with { Sig = AuthCrypto.EncodeBase64Url(sig) });
        Assert.Equal(RelayCloseCodes.RegistrationInvalid, (await Wire.ClosedAsync(chrome.Control)).Code);
    }

    [Fact]
    public async Task An_spki_that_does_not_import_as_a_P256_key_is_closed_4503()
    {
        await using var chrome = NewChrome();
        var nonce = await chrome.OpenAsync();
        var spki = (byte[])chrome.Spki.Clone();
        spki[22] ^= 0x0F; // the curve OID's last byte: an unknown curve, same length
        await Wire.SendAsync(chrome.Control, chrome.Register(nonce) with { Spki = AuthCrypto.EncodeBase64Url(spki) });
        Assert.Equal(RelayCloseCodes.RegistrationInvalid, (await Wire.ClosedAsync(chrome.Control)).Code);
    }

    [Theory]
    [InlineData("{\"type\":\"RELAY_PING\"}")]
    [InlineData("not json")]
    [InlineData("{\"type\":\"RELAY_REGISTER\"}")]
    [InlineData("{\"type\":\"HELLO\"}")]
    public async Task A_first_message_that_is_not_a_valid_REGISTER_is_closed_4500(string text)
    {
        await using var chrome = NewChrome();
        await chrome.OpenAsync();
        await Wire.SendTextAsync(chrome.Control, text);
        Assert.Equal(RelayCloseCodes.ProtocolError, (await Wire.ClosedAsync(chrome.Control)).Code);
    }

    [Fact]
    public async Task A_binary_frame_on_the_control_socket_is_closed_4500()
    {
        await using var chrome = await Relay.ChromeAsync();
        await Wire.SendBinaryAsync(chrome.Control, [1, 2, 3]);
        Assert.Equal(RelayCloseCodes.ProtocolError, (await Wire.ClosedAsync(chrome.Control)).Code);
    }

    /// <summary>One real 10 s wait (S1: the first message within 10 s is RELAY_REGISTER).</summary>
    [Fact]
    public async Task No_REGISTER_within_10_seconds_is_closed_4500()
    {
        await using var late = NewChrome();
        var opened = Stopwatch.StartNew();
        await late.OpenAsync();
        var (code, _) = await Wire.ClosedAsync(late.Control, TimeSpan.FromSeconds(30));
        Assert.Equal(RelayCloseCodes.ProtocolError, code);
        Assert.InRange(opened.Elapsed, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(30)); // the timer, not an early close
    }

    [Fact]
    public async Task PING_gets_PONG_and_DIAL_TOKENS_gets_TOKENS_OK_with_its_seq()
    {
        await using var chrome = await Relay.ChromeAsync();
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
        await using var chrome = await Relay.ChromeAsync();
        await Wire.SendTextAsync(chrome.Control, text);
        Assert.Equal(RelayCloseCodes.ProtocolError, (await Wire.ClosedAsync(chrome.Control)).Code);
    }

    [Fact]
    public async Task A_second_REGISTER_on_the_same_socket_is_closed_4500()
    {
        await using var chrome = await Relay.ChromeAsync();
        await Wire.SendAsync(chrome.Control, chrome.Register(new byte[32]));
        Assert.Equal(RelayCloseCodes.ProtocolError, (await Wire.ClosedAsync(chrome.Control)).Code);
    }

    [Fact]
    public async Task A_newer_registration_of_the_same_key_closes_the_older_socket_4507()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var older = await Relay.ChromeAsync(key: ECDsa.Create(key.ExportParameters(includePrivateParameters: true)));
        await using var newer = await Relay.ChromeAsync(key: ECDsa.Create(key.ExportParameters(includePrivateParameters: true)));
        Assert.Equal(RelayCloseCodes.Replaced, (await Wire.ClosedAsync(older.Control)).Code);
        await Wire.SendAsync(newer.Control, new RelayPing());
        Assert.IsType<RelayPong>(await Wire.ReceiveAsync(newer.Control));
    }

    [Fact]
    public async Task A_control_frame_over_512_KiB_is_closed_4513()
    {
        await using var chrome = await Relay.ChromeAsync();
        await Wire.SendTextAsync(chrome.Control, new string(' ', FrameCap + 1));
        Assert.Equal(RelayCloseCodes.FrameTooLarge, (await Wire.ClosedAsync(chrome.Control)).Code);
    }

    [Fact]
    public async Task A_REGISTER_over_512_KiB_is_closed_4513()
    {
        await using var chrome = NewChrome();
        await chrome.OpenAsync();
        await Wire.SendTextAsync(chrome.Control, new string(' ', FrameCap + 1));
        Assert.Equal(RelayCloseCodes.FrameTooLarge, (await Wire.ClosedAsync(chrome.Control)).Code);
    }

    [Fact]
    public async Task A_control_socket_whose_id_is_its_own_relayId_registers()
    {
        await using var chrome = NewChrome();
        var nonce = await chrome.OpenAsync($"?id={chrome.RelayId}");
        await Wire.SendAsync(chrome.Control, chrome.Register(nonce));
        Assert.Equal(chrome.RelayId, Assert.IsType<RelayRegistered>(await Wire.ReceiveAsync(chrome.Control)).RelayId);
    }

    [Fact]
    public async Task A_control_socket_whose_id_is_another_relayId_is_closed_4503_and_never_goes_online()
    {
        await using var chrome = NewChrome();
        var nonce = await chrome.OpenAsync($"?id={OtherId}");
        await Wire.SendAsync(chrome.Control, chrome.Register(nonce));
        Assert.Equal(RelayCloseCodes.RegistrationInvalid, (await Wire.ClosedAsync(chrome.Control)).Code);

        using var dial = await Relay.DialAsync(chrome.RelayId, "link", null);
        Assert.Equal(RelayCloseCodes.NotOnline, (await Wire.ClosedAsync(dial)).Code);
    }

    [Theory]
    [InlineData("?id=short")]
    [InlineData("?id=")]
    [InlineData("?id=AAAAAAAAAAAAAAAAAAAAAA&id=AAAAAAAAAAAAAAAAAAAAAA")]
    public async Task A_control_socket_with_a_malformed_or_repeated_id_is_closed_4500(string query)
    {
        using var ws = await Relay.ConnectAsync("/v1/browser" + query);
        Assert.Equal(RelayCloseCodes.ProtocolError, (await Wire.ClosedAsync(ws)).Code);
    }

    [Fact]
    public async Task RELAY_HOST_on_a_relay_without_a_hosted_bridge_is_unavailable_and_the_code_never_reaches_the_output()
    {
        const string pairCode = "ABCDE-FGHIJ-KLMNO-PQRST-UVWXYZ";
        const string sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        await using var chrome = await Relay.ChromeAsync();
        var code = $"panda-relay://{Relay.PublicHost.Split(':')[0]}/{chrome.RelayId}#{pairCode}";
        Relay.Secret(code);
        Relay.Secret(pairCode);
        Relay.Secret(sha);
        await Wire.SendAsync(chrome.Control, new RelayHost(13, code, sha));
        Assert.Equal(new RelayHostResult(13, false, Error: RelayHostError.Unavailable), await Wire.ReceiveAsync(chrome.Control));
    }

    [Fact]
    public async Task The_nonce_the_signature_and_the_key_never_reach_the_output()
    {
        await using var chrome = NewChrome();
        var nonce = await chrome.OpenAsync();
        var register = chrome.Register(nonce);
        Relay.Secret(AuthCrypto.EncodeBase64Url(nonce));
        Relay.Secret(register.Sig);
        Relay.Secret(register.Spki);
        await Wire.SendAsync(chrome.Control, register);
        Assert.IsType<RelayRegistered>(await Wire.ReceiveAsync(chrome.Control));
        var forged = register with { Sig = AuthCrypto.EncodeBase64Url(RandomNumberGenerator.GetBytes(64)) };
        Relay.Secret(forged.Sig);
        await Wire.SendAsync(chrome.Control, forged); // a second REGISTER: closed, and the relay may log the refusal
        await Wire.ClosedAsync(chrome.Control);
    }
}

/// <summary>G2: the per-IP registration limit (600 / clock hour) needs a relay of its own (a fresh budget).</summary>
public class RegistrationLimitTests
{
    private const int PerHour = 600; // RelayOptions.RegistrationsPerIpPerHour, S1 4530

    [Fact]
    public async Task Registration_601_from_one_IP_within_the_hour_is_closed_4530_with_the_seconds_left()
    {
        await using var relay = await Limits.StartAsync(TimeSpan.FromMinutes(4));
        await Limits.RefusedForTheirOriginAsync(relay, Limits.OfflineId(), "not-a-token"); // counted, they would make registration 600 the 4530
        var registered = 1; // the ready probe registered once (critic #29)
        while (registered < PerHour)
        {
            await using var chrome = await relay.ChromeAsync();
            registered++;
        }

        using var over = await relay.ConnectAsync("/v1/browser");
        Limits.AssertSecondsLeft(await Wire.ClosedAsync(over), RelayCloseCodes.DialerLimited, TimeSpan.FromHours(1));
    }
}
