using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Panda.Bridge.Protocol;
using Panda.Bridge.Relay;

namespace Panda.Relay.Conformance;

/// <summary>
/// G3 (spec §78 S1): <c>/v1/stream</c> — who may dial (the token of the right kind, checked after "online"), accept and
/// STREAM_OPEN, the bytes piped unchanged and in order, the slots, the close rules and codes.
/// </summary>
public class StreamTests(ClassRelay fixture) : IClassFixture<ClassRelay>
{
    private const int FrameCap = 512 * 1024;

    private RelayProcess Relay => fixture.Relay;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_dial_for_an_offline_relayId_is_closed_4504_whatever_the_token(bool rightToken)
    {
        var id = RelayClient.Identity();
        using var key = id.Key;
        Relay.Secret(id.Link.Token);
        using var dial = await Relay.DialAsync(id.RelayId, "link", rightToken ? id.Link.Token : id.Pair.Token);
        Assert.Equal(RelayCloseCodes.NotOnline, (await Wire.ClosedAsync(dial)).Code); // 4504 before 4501: no token oracle
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task A_dial_without_the_registered_token_is_closed_4501(string? token)
    {
        await using var chrome = await Relay.OnlineAsync();
        using var dial = await Relay.DialAsync(chrome.Chrome.RelayId, "link", token);
        Assert.Equal(RelayCloseCodes.DialRefused, (await Wire.ClosedAsync(dial)).Code);
    }

    [Fact]
    public async Task A_repeated_dial_header_is_no_token_and_is_closed_4501()
    {
        await using var chrome = await Relay.OnlineAsync();
        using var dial = await Relay.RawUpgradeAsync(null, $"/v1/stream?dial={chrome.Chrome.RelayId}&kind=link",
            $"X-Panda-Dial: {chrome.Link.Token}", $"X-Panda-Dial: {chrome.Link.Token}");
        Assert.Equal(RelayCloseCodes.DialRefused, (await Wire.ClosedAsync(dial)).Code);
    }

    [Theory]
    [InlineData("link")]
    [InlineData("pair")]
    public async Task A_token_of_the_other_kind_is_closed_4501(string kind)
    {
        await using var chrome = await Relay.OnlineAsync();
        using var dial = await Relay.DialAsync(chrome.Chrome.RelayId, kind, kind == "link" ? chrome.Pair.Token : chrome.Link.Token);
        Assert.Equal(RelayCloseCodes.DialRefused, (await Wire.ClosedAsync(dial)).Code);
    }

    [Theory]
    [InlineData("link")]
    [InlineData("pair")]
    public async Task An_accepted_stream_gets_STREAM_OPEN_and_pipes_binary_messages_unchanged_both_ways(string kind)
    {
        await using var chrome = await Relay.OnlineAsync();
        var (dial, ext) = await Relay.OpenStreamAsync(chrome, kind, kind == "link" ? chrome.Link.Token : chrome.Pair.Token);
        using var d = dial;
        using var e = ext;
        foreach (var bytes in new[] { [0x01], RandomNumberGenerator.GetBytes(64 * 1024), RandomNumberGenerator.GetBytes(FrameCap) }) // exactly the cap passes
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
    public async Task Two_hundred_messages_in_a_row_arrive_in_order_each_way()
    {
        await using var chrome = await Relay.OnlineAsync();
        var (dial, ext) = await Relay.OpenStreamAsync(chrome, "link", chrome.Link.Token);
        using var d = dial;
        using var e = ext;
        foreach (var (from, to) in new[] { (dial, ext), (ext, dial) })
        {
            for (var i = 0; i < 200; i++)
            {
                await Wire.SendBinaryAsync(from, BitConverter.GetBytes(i));
            }

            for (var i = 0; i < 200; i++)
            {
                Assert.Equal(i, BitConverter.ToInt32((await Wire.ReceiveRawAsync(to)).Bytes));
            }
        }
    }

    [Fact]
    public async Task DIAL_TOKENS_replaces_the_registered_hashes()
    {
        await using var chrome = await Relay.OnlineAsync();
        var newPair = DialTokens.PairToken("ZZZZ-YYYY-XXXX-WWWW-VVVV-TTTTSS", chrome.Chrome.RelayId);
        Assert.True(DialTokens.TryHash(newPair, out var newPairHash));
        Relay.Secret(newPair);
        Relay.Secret(newPairHash);
        await Wire.SendAsync(chrome.Chrome.Control, new RelayDialTokens(1, Pair: newPairHash)); // no link any more
        Assert.Equal(1, Assert.IsType<RelayTokensOk>(await Wire.ReceiveAsync(chrome.Chrome.Control)).Seq);

        using var oldLink = await Relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Link.Token);
        Assert.Equal(RelayCloseCodes.DialRefused, (await Wire.ClosedAsync(oldLink)).Code);
        using var oldPair = await Relay.DialAsync(chrome.Chrome.RelayId, "pair", chrome.Pair.Token);
        Assert.Equal(RelayCloseCodes.DialRefused, (await Wire.ClosedAsync(oldPair)).Code);
        using var pair = await Relay.DialAsync(chrome.Chrome.RelayId, "pair", newPair);
        Assert.Equal(RelayStreamKind.Pair, (await chrome.Chrome.IncomingAsync()).Kind);
    }

    [Theory]
    [InlineData("binary", RelayCloseCodes.ProtocolError)]
    [InlineData("text", RelayCloseCodes.ProtocolError)]
    [InlineData("oversize", RelayCloseCodes.FrameTooLarge)]
    public async Task A_frame_from_the_dialer_before_STREAM_OPEN_is_closed_and_frees_the_slot(string frame, int code)
    {
        await using var chrome = await Relay.OnlineAsync();
        using var dial = await Relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Link.Token);
        var incoming = await chrome.Chrome.IncomingAsync();
        await (frame switch
        {
            "binary" => Wire.SendBinaryAsync(dial, [1]),
            "text" => Wire.SendTextAsync(dial, "{}"),
            _ => Wire.SendBinaryAsync(dial, new byte[FrameCap + 1]),
        });
        Assert.Equal(code, (await Wire.ClosedAsync(dial)).Code);

        using var late = await chrome.Chrome.AcceptAsync(incoming.StreamId);
        Assert.Equal(RelayCloseCodes.NotOnline, (await Wire.ClosedAsync(late)).Code);
        var (again, ext) = await Relay.OpenStreamAsync(chrome, "link", chrome.Link.Token);
        again.Dispose();
        ext.Dispose();
    }

    /// <summary>One real 15 s wait (S1: a streamId issued ≤ 15 s ago).</summary>
    [Fact]
    public async Task No_accept_within_15_seconds_closes_the_dialer_4508_and_the_streamId_expires()
    {
        await using var chrome = await Relay.OnlineAsync();
        using var dial = await Relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Link.Token);
        var incoming = await chrome.Chrome.IncomingAsync();
        var issued = Stopwatch.StartNew();
        Assert.Equal(RelayCloseCodes.AcceptTimeout, (await Wire.ClosedAsync(dial, TimeSpan.FromSeconds(40))).Code);
        Assert.InRange(issued.Elapsed, TimeSpan.FromSeconds(14), TimeSpan.FromSeconds(40)); // the timer, not an early close
        using var late = await chrome.Chrome.AcceptAsync(incoming.StreamId);
        Assert.Equal(RelayCloseCodes.NotOnline, (await Wire.ClosedAsync(late)).Code);
    }

    [Fact]
    public async Task An_unknown_or_already_used_streamId_is_closed_4504()
    {
        await using var chrome = await Relay.OnlineAsync();
        using var unknown = await chrome.Chrome.AcceptAsync("AAAAAAAAAAAAAAAAAAAAAA");
        Assert.Equal(RelayCloseCodes.NotOnline, (await Wire.ClosedAsync(unknown)).Code);

        using var dial = await Relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Link.Token);
        var incoming = await chrome.Chrome.IncomingAsync();
        using var first = await chrome.Chrome.AcceptAsync(incoming.StreamId);
        Assert.IsType<RelayStreamOpen>(await Wire.ReceiveAsync(dial));
        using var second = await chrome.Chrome.AcceptAsync(incoming.StreamId);
        Assert.Equal(RelayCloseCodes.NotOnline, (await Wire.ClosedAsync(second)).Code);
    }

    [Fact]
    public async Task An_accept_naming_its_owner_opens_and_one_naming_another_is_closed_4504()
    {
        await using var chrome = await Relay.OnlineAsync();
        using var dial = await Relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Link.Token);
        var incoming = await chrome.Chrome.IncomingAsync();
        using var wrong = await chrome.Chrome.AcceptAsync(incoming.StreamId, "&id=AAAAAAAAAAAAAAAAAAAAAA");
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
        await using var chrome = await Relay.OnlineAsync();
        using var ws = await chrome.Chrome.AcceptAsync("AAAAAAAAAAAAAAAAAAAAAA", query);
        Assert.Equal(RelayCloseCodes.ProtocolError, (await Wire.ClosedAsync(ws)).Code);
    }

    [Fact]
    public async Task A_second_stream_of_the_same_kind_is_closed_4509_while_the_first_holds_the_slot()
    {
        await using var chrome = await Relay.OnlineAsync();
        using var pending = await Relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Link.Token);
        await chrome.Chrome.IncomingAsync();
        using var second = await Relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Link.Token);
        Assert.Equal(RelayCloseCodes.SlotTaken, (await Wire.ClosedAsync(second)).Code);

        using var pair = await Relay.DialAsync(chrome.Chrome.RelayId, "pair", chrome.Pair.Token); // the other kind is free
        Assert.Equal(RelayStreamKind.Pair, (await chrome.Chrome.IncomingAsync()).Kind);

        await Wire.CloseAsync(pending, (int)WebSocketCloseStatus.NormalClosure); // the contract: the slot is free once the close handshake ends
        using var retry = await Relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Link.Token);
        Assert.Equal(RelayStreamKind.Link, (await chrome.Chrome.IncomingAsync()).Kind);
    }

    [Fact]
    public async Task A_text_frame_after_STREAM_OPEN_closes_the_sender_4500_and_the_peer_1000()
    {
        await using var chrome = await Relay.OnlineAsync();
        var (dial, ext) = await Relay.OpenStreamAsync(chrome, "link", chrome.Link.Token);
        using var d = dial;
        using var e = ext;
        await Wire.SendTextAsync(ext, "{}");
        Assert.Equal(RelayCloseCodes.ProtocolError, (await Wire.ClosedAsync(ext)).Code);
        Assert.Equal((int)WebSocketCloseStatus.NormalClosure, (await Wire.ClosedAsync(dial)).Code);
    }

    [Fact]
    public async Task A_frame_over_512_KiB_closes_the_sender_4513_and_the_peer_1000()
    {
        await using var chrome = await Relay.OnlineAsync();
        var (dial, ext) = await Relay.OpenStreamAsync(chrome, "link", chrome.Link.Token);
        using var d = dial;
        using var e = ext;
        await Wire.SendBinaryAsync(dial, new byte[FrameCap + 1]);
        Assert.Equal(RelayCloseCodes.FrameTooLarge, (await Wire.ClosedAsync(dial)).Code);
        Assert.Equal((int)WebSocketCloseStatus.NormalClosure, (await Wire.ClosedAsync(ext)).Code);
    }

    [Fact]
    public async Task When_one_side_closes_the_other_gets_1000_never_the_peer_code()
    {
        await using var chrome = await Relay.OnlineAsync();
        var (dial, ext) = await Relay.OpenStreamAsync(chrome, "link", chrome.Link.Token);
        using var d = dial;
        using var e = ext;
        await Wire.CloseAsync(ext, 4003, "app code");
        var (code, reason) = await Wire.ClosedAsync(dial);
        Assert.Equal((int)WebSocketCloseStatus.NormalClosure, code);
        Assert.Equal("", reason);
    }

    [Fact]
    public async Task Losing_the_control_socket_closes_every_stream_of_that_relayId_1001()
    {
        await using var chrome = await Relay.OnlineAsync();
        var (dial, ext) = await Relay.OpenStreamAsync(chrome, "link", chrome.Link.Token);
        using var d = dial;
        using var e = ext;
        using var pending = await Relay.DialAsync(chrome.Chrome.RelayId, "pair", chrome.Pair.Token);
        await chrome.Chrome.IncomingAsync();

        chrome.Chrome.Control.Abort(); // the service worker died
        Assert.Equal((int)WebSocketCloseStatus.EndpointUnavailable, (await Wire.ClosedAsync(dial)).Code);
        Assert.Equal((int)WebSocketCloseStatus.EndpointUnavailable, (await Wire.ClosedAsync(ext)).Code);
        Assert.Equal((int)WebSocketCloseStatus.EndpointUnavailable, (await Wire.ClosedAsync(pending)).Code);
        using var after = await Relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Link.Token);
        Assert.Equal(RelayCloseCodes.NotOnline, (await Wire.ClosedAsync(after)).Code);
    }

    [Fact]
    public async Task A_replaced_registration_closes_the_older_sockets_streams_1001_and_keeps_the_new_hashes()
    {
        await using var chrome = await Relay.OnlineAsync();
        var (dial, ext) = await Relay.OpenStreamAsync(chrome, "link", chrome.Link.Token);
        using var d = dial;
        using var e = ext;

        await using var copy = await Relay.ChromeAsync(key: ECDsa.Create(chrome.Chrome.Key.ExportParameters(true))); // no hashes
        Assert.Equal(RelayCloseCodes.Replaced, (await Wire.ClosedAsync(chrome.Chrome.Control)).Code);
        Assert.Equal((int)WebSocketCloseStatus.EndpointUnavailable, (await Wire.ClosedAsync(dial)).Code);
        using var refused = await Relay.DialAsync(chrome.Chrome.RelayId, "link", chrome.Link.Token);
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
        using var ws = await Relay.ConnectAsync(path);
        Assert.Equal(RelayCloseCodes.ProtocolError, (await Wire.ClosedAsync(ws)).Code);
    }

    [Fact]
    public async Task A_payload_never_reaches_the_output()
    {
        await using var chrome = await Relay.OnlineAsync();
        var (dial, ext) = await Relay.OpenStreamAsync(chrome, "link", chrome.Link.Token);
        using var d = dial;
        using var e = ext;
        var marker = "SECRET-PAYLOAD-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        Relay.Secret(marker);
        await Wire.SendBinaryAsync(dial, Encoding.ASCII.GetBytes(marker));
        Assert.Equal(marker, Encoding.ASCII.GetString((await Wire.ReceiveRawAsync(ext)).Bytes));
        using var wrong = await Relay.DialAsync(chrome.Chrome.RelayId, "pair", chrome.Link.Token); // a refusal the relay may log
        await Wire.ClosedAsync(wrong);
    }
}
