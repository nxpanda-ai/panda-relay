using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Panda.Bridge.Protocol;
using Panda.Bridge.Relay;

namespace Panda.Relay.Tests;

/// <summary>plan-relay P4, seam S1: dial / accept / STREAM_OPEN, piping, slots and the close rules of /v1/stream.</summary>
public class StreamTests
{
    private const string Code = "ABCD-EFGH-IJKL-MNOP-QRST-UVWXYZ"; // any valid pair code shape works for a token

    [Fact]
    public async Task A_dial_for_an_offline_relayId_is_closed_4504_whatever_the_token()
    {
        await using var relay = await RelayFixture.StartAsync();
        var id = Identity();
        using var key = id.Key;
        using var dial = await relay.DialAsync(id.RelayId, "link", id.Link.Token);
        Assert.Equal(RelayCloseCodes.NotOnline, (await Wire.ClosedAsync(dial)).Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task A_dial_without_the_registered_token_is_closed_4501(string? token)
    {
        await using var relay = await RelayFixture.StartAsync();
        await using var chrome = await OnlineAsync(relay);
        using var dial = await relay.DialAsync(chrome.Chrome.RelayId, "link", token);
        Assert.Equal(RelayCloseCodes.DialRefused, (await Wire.ClosedAsync(dial)).Code);
    }

    [Fact]
    public async Task The_pair_token_does_not_open_a_link_stream()
    {
        await using var relay = await RelayFixture.StartAsync();
        await using var chrome = await OnlineAsync(relay);
        using var dial = await relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Pair.Token);
        Assert.Equal(RelayCloseCodes.DialRefused, (await Wire.ClosedAsync(dial)).Code);
    }

    [Theory]
    [InlineData("link")]
    [InlineData("pair")]
    public async Task An_accepted_stream_gets_STREAM_OPEN_and_pipes_binary_frames_unchanged_both_ways(string kind)
    {
        await using var relay = await RelayFixture.StartAsync();
        await using var chrome = await OnlineAsync(relay);
        var token = kind == "link" ? chrome.Link.Token : chrome.Pair.Token;
        var (dial, ext) = await OpenStreamAsync(relay, chrome, kind, token);
        using var d = dial;
        using var e = ext;

        var big = RandomNumberGenerator.GetBytes(relay.Server.Options.MaxFrameBytes); // exactly the cap passes
        foreach (var bytes in new[] { [0x01], RandomNumberGenerator.GetBytes(1000), big })
        {
            await Wire.SendBinaryAsync(dial, bytes);
            var got = await Wire.ReceiveRawAsync(ext);
            Assert.Equal(WebSocketMessageType.Binary, got.Type);
            Assert.Equal(bytes, got.Bytes);
            await Wire.SendBinaryAsync(ext, bytes);
            Assert.Equal(bytes, (await Wire.ReceiveRawAsync(dial)).Bytes);
        }
    }

    [Fact]
    public async Task DIAL_TOKENS_replaces_the_registered_hashes()
    {
        await using var relay = await RelayFixture.StartAsync();
        await using var chrome = await OnlineAsync(relay);
        var newPair = DialTokens.PairToken("ZZZZ-YYYY-XXXX-WWWW-VVVV-TTTTSS", chrome.Chrome.RelayId);
        Assert.True(DialTokens.TryHash(newPair, out var newPairHash));
        await Wire.SendAsync(chrome.Chrome.Control, new RelayDialTokens(1, Pair: newPairHash)); // no link any more
        Assert.Equal(1, Assert.IsType<RelayTokensOk>(await Wire.ReceiveAsync(chrome.Chrome.Control)).Seq);

        using var oldLink = await relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Link.Token);
        Assert.Equal(RelayCloseCodes.DialRefused, (await Wire.ClosedAsync(oldLink)).Code);
        using var oldPair = await relay.DialAsync(chrome.Chrome.RelayId, "pair", chrome.Pair.Token);
        Assert.Equal(RelayCloseCodes.DialRefused, (await Wire.ClosedAsync(oldPair)).Code);
        using var pair = await relay.DialAsync(chrome.Chrome.RelayId, "pair", newPair);
        Assert.Equal(RelayStreamKind.Pair, (await chrome.Chrome.IncomingAsync()).Kind);
    }

    [Theory]
    [InlineData("binary", RelayCloseCodes.ProtocolError)]
    [InlineData("text", RelayCloseCodes.ProtocolError)]
    [InlineData("oversize", RelayCloseCodes.FrameTooLarge)]
    public async Task A_frame_from_the_dialer_before_STREAM_OPEN_is_closed_and_frees_the_slot(string frame, int code)
    {
        await using var relay = await RelayFixture.StartAsync(o => o.MaxFrameBytes = 1024);
        await using var chrome = await OnlineAsync(relay);
        using var dial = await relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Link.Token);
        var incoming = await chrome.Chrome.IncomingAsync();
        await (frame switch
        {
            "binary" => Wire.SendBinaryAsync(dial, [1]),
            "text" => Wire.SendTextAsync(dial, "{}"),
            _ => Wire.SendBinaryAsync(dial, new byte[1025]),
        });
        Assert.Equal(code, (await Wire.ClosedAsync(dial)).Code);

        using var late = await chrome.Chrome.AcceptAsync(incoming.StreamId);
        Assert.Equal(RelayCloseCodes.NotOnline, (await Wire.ClosedAsync(late)).Code);
        var (again, ext) = await OpenStreamAsync(relay, chrome, "link", chrome.Link.Token);
        again.Dispose();
        ext.Dispose();
    }

    [Fact]
    public async Task No_accept_within_the_accept_timeout_closes_the_dialer_4508_and_the_streamId_expires()
    {
        await using var relay = await RelayFixture.StartAsync();
        await using var chrome = await OnlineAsync(relay);
        using var dial = await relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Link.Token);
        var incoming = await chrome.Chrome.IncomingAsync();
        relay.Time.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(RelayCloseCodes.AcceptTimeout, (await Wire.ClosedAsync(dial)).Code);
        using var late = await chrome.Chrome.AcceptAsync(incoming.StreamId);
        Assert.Equal(RelayCloseCodes.NotOnline, (await Wire.ClosedAsync(late)).Code);
    }

    [Fact]
    public async Task An_unknown_or_already_used_streamId_is_closed_4504()
    {
        await using var relay = await RelayFixture.StartAsync();
        await using var chrome = await OnlineAsync(relay);
        using var unknown = await chrome.Chrome.AcceptAsync("AAAAAAAAAAAAAAAAAAAAAA");
        Assert.Equal(RelayCloseCodes.NotOnline, (await Wire.ClosedAsync(unknown)).Code);

        using var dial = await relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Link.Token);
        var incoming = await chrome.Chrome.IncomingAsync();
        using var first = await chrome.Chrome.AcceptAsync(incoming.StreamId);
        Assert.IsType<RelayStreamOpen>(await Wire.ReceiveAsync(dial));
        using var second = await chrome.Chrome.AcceptAsync(incoming.StreamId);
        Assert.Equal(RelayCloseCodes.NotOnline, (await Wire.ClosedAsync(second)).Code);
    }

    [Fact]
    public async Task A_second_stream_of_the_same_kind_is_closed_4509_while_the_first_holds_the_slot()
    {
        await using var relay = await RelayFixture.StartAsync();
        await using var chrome = await OnlineAsync(relay);
        using var pending = await relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Link.Token);
        await chrome.Chrome.IncomingAsync();
        using var second = await relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Link.Token);
        Assert.Equal(RelayCloseCodes.SlotTaken, (await Wire.ClosedAsync(second)).Code);

        using var pair = await relay.DialAsync(chrome.Chrome.RelayId, "pair", chrome.Pair.Token); // the other kind is free
        Assert.Equal(RelayStreamKind.Pair, (await chrome.Chrome.IncomingAsync()).Kind);

        // The relay frees the slot before it answers the close, so the next dial is admitted at once.
        await pending.CloseAsync(WebSocketCloseStatus.NormalClosure, "", TestContext.Current.CancellationToken);
        using var retry = await relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Link.Token);
        Assert.Equal(RelayStreamKind.Link, (await chrome.Chrome.IncomingAsync()).Kind);
    }

    [Fact]
    public async Task A_text_frame_after_STREAM_OPEN_closes_the_sender_4500_and_the_peer_1000()
    {
        await using var relay = await RelayFixture.StartAsync();
        await using var chrome = await OnlineAsync(relay);
        var (dial, ext) = await OpenStreamAsync(relay, chrome, "link", chrome.Link.Token);
        using var d = dial;
        using var e = ext;
        await Wire.SendTextAsync(ext, "{}");
        Assert.Equal(RelayCloseCodes.ProtocolError, (await Wire.ClosedAsync(ext)).Code);
        Assert.Equal((int)WebSocketCloseStatus.NormalClosure, (await Wire.ClosedAsync(dial)).Code);
    }

    [Fact]
    public async Task A_frame_over_the_cap_closes_the_sender_4513_and_the_peer_1000()
    {
        await using var relay = await RelayFixture.StartAsync(o => o.MaxFrameBytes = 4096);
        await using var chrome = await OnlineAsync(relay);
        var (dial, ext) = await OpenStreamAsync(relay, chrome, "link", chrome.Link.Token);
        using var d = dial;
        using var e = ext;
        await Wire.SendBinaryAsync(dial, new byte[4097]);
        Assert.Equal(RelayCloseCodes.FrameTooLarge, (await Wire.ClosedAsync(dial)).Code);
        Assert.Equal((int)WebSocketCloseStatus.NormalClosure, (await Wire.ClosedAsync(ext)).Code);
    }

    [Fact]
    public async Task When_one_side_closes_the_other_gets_1000_never_the_peer_code()
    {
        await using var relay = await RelayFixture.StartAsync();
        await using var chrome = await OnlineAsync(relay);
        var (dial, ext) = await OpenStreamAsync(relay, chrome, "link", chrome.Link.Token);
        using var d = dial;
        using var e = ext;
        await ext.CloseAsync((WebSocketCloseStatus)4003, "app code", TestContext.Current.CancellationToken);
        var (code, reason) = await Wire.ClosedAsync(dial);
        Assert.Equal((int)WebSocketCloseStatus.NormalClosure, code);
        Assert.Equal("", reason);
    }

    [Fact]
    public async Task Losing_the_control_socket_closes_every_stream_of_that_relayId_1001()
    {
        await using var relay = await RelayFixture.StartAsync();
        await using var chrome = await OnlineAsync(relay);
        var (dial, ext) = await OpenStreamAsync(relay, chrome, "link", chrome.Link.Token);
        using var d = dial;
        using var e = ext;
        using var pending = await relay.DialAsync(chrome.Chrome.RelayId, "pair", chrome.Pair.Token);
        await chrome.Chrome.IncomingAsync();

        chrome.Chrome.Control.Abort(); // the service worker died
        Assert.Equal((int)WebSocketCloseStatus.EndpointUnavailable, (await Wire.ClosedAsync(dial)).Code);
        Assert.Equal((int)WebSocketCloseStatus.EndpointUnavailable, (await Wire.ClosedAsync(ext)).Code);
        Assert.Equal((int)WebSocketCloseStatus.EndpointUnavailable, (await Wire.ClosedAsync(pending)).Code);
        using var after = await relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Link.Token);
        Assert.Equal(RelayCloseCodes.NotOnline, (await Wire.ClosedAsync(after)).Code);
    }

    [Fact]
    public async Task A_replaced_registration_closes_the_older_socket_streams_1001_and_keeps_the_new_hashes()
    {
        await using var relay = await RelayFixture.StartAsync();
        await using var chrome = await OnlineAsync(relay);
        var (dial, ext) = await OpenStreamAsync(relay, chrome, "link", chrome.Link.Token);
        using var d = dial;
        using var e = ext;

        await using var copy = await relay.ChromeAsync(key: ECDsa.Create(chrome.Chrome.Key.ExportParameters(true))); // no hashes
        Assert.Equal(RelayCloseCodes.Replaced, (await Wire.ClosedAsync(chrome.Chrome.Control)).Code);
        Assert.Equal((int)WebSocketCloseStatus.EndpointUnavailable, (await Wire.ClosedAsync(dial)).Code);
        using var refused = await relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Link.Token);
        Assert.Equal(RelayCloseCodes.DialRefused, (await Wire.ClosedAsync(refused)).Code);
    }

    [Theory]
    [InlineData("/v1/stream")]
    [InlineData("/v1/stream?dial=short&kind=link")]
    [InlineData("/v1/stream?dial=AAAAAAAAAAAAAAAAAAAAAA&kind=other")]
    [InlineData("/v1/stream?dial=AAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("/v1/stream?dial=AAAAAAAAAAAAAAAAAAAAAA&kind=link&accept=AAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("/v1/stream?accept=AAAA")]
    [InlineData("/v1/stream?dial=AAAAAAAAAAAAAAAAAAAAAA&dial=BBBBBBBBBBBBBBBBBBBBBB&kind=link")]
    public async Task A_malformed_stream_request_is_closed_4500(string path)
    {
        await using var relay = await RelayFixture.StartAsync();
        using var ws = await relay.ConnectAsync(path);
        Assert.Equal(RelayCloseCodes.ProtocolError, (await Wire.ClosedAsync(ws)).Code);
    }

    [Fact]
    public async Task Logs_never_carry_a_dial_token_its_hash_or_a_payload()
    {
        await using var relay = await RelayFixture.StartAsync();
        await using var chrome = await OnlineAsync(relay);
        var (dial, ext) = await OpenStreamAsync(relay, chrome, "link", chrome.Link.Token);
        using var d = dial;
        using var e = ext;
        var payload = Encoding.ASCII.GetBytes("SECRET-PAYLOAD-0123456789");
        await Wire.SendBinaryAsync(dial, payload);
        await Wire.ReceiveRawAsync(ext);
        using var wrong = await relay.DialAsync(chrome.Chrome.RelayId, "pair", chrome.Link.Token);
        await Wire.ClosedAsync(wrong);
        await dial.CloseAsync(WebSocketCloseStatus.NormalClosure, "", TestContext.Current.CancellationToken);
        await Wire.ClosedAsync(ext);

        var all = string.Join("\n", relay.Logs.Lines);
        Assert.Contains(chrome.Chrome.RelayId, all, StringComparison.Ordinal);
        foreach (var secret in new[] { chrome.Link.Token, chrome.Link.Hash, chrome.Pair.Token, chrome.Pair.Hash, "SECRET-PAYLOAD" })
        {
            Assert.DoesNotContain(secret, all, StringComparison.Ordinal);
        }
    }

    internal sealed record DialToken(string Token, string Hash);

    internal sealed class Online(FakeChrome chrome, DialToken link, DialToken pair) : IAsyncDisposable
    {
        public FakeChrome Chrome { get; } = chrome;
        public DialToken Link { get; } = link;
        public DialToken Pair { get; } = pair;

        public ValueTask DisposeAsync() => Chrome.DisposeAsync();
    }

    internal static (ECDsa Key, string RelayId, DialToken Link, DialToken Pair) Identity()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var relayId = DialTokens.RelayIdFromSpki(key.ExportSubjectPublicKeyInfo());
        return (key, relayId, Make(DialTokens.LinkToken(RandomNumberGenerator.GetBytes(32), relayId)), Make(DialTokens.PairToken(Code, relayId)));

        static DialToken Make(string token) => DialTokens.TryHash(token, out var hash) ? new DialToken(token, hash) : throw new InvalidOperationException();
    }

    /// <summary>A Chrome online with both a link and a pair token registered in its RELAY_REGISTER.</summary>
    internal static async Task<Online> OnlineAsync(RelayFixture relay)
    {
        var (key, _, link, pair) = Identity();
        return new Online(await relay.ChromeAsync(link.Hash, pair.Hash, key), link, pair);
    }

    internal static async Task<(ClientWebSocket Dial, ClientWebSocket Ext)> OpenStreamAsync(RelayFixture relay, Online chrome, string kind, string token)
    {
        var dial = await relay.DialAsync(chrome.Chrome.RelayId, kind, token);
        var incoming = await chrome.Chrome.IncomingAsync();
        Assert.Equal(kind == "link" ? RelayStreamKind.Link : RelayStreamKind.Pair, incoming.Kind);
        var ext = await chrome.Chrome.AcceptAsync(incoming.StreamId);
        Assert.IsType<RelayStreamOpen>(await Wire.ReceiveAsync(dial));
        return (dial, ext);
    }
}
