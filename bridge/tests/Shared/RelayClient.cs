using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Panda.Bridge.Auth;
using Panda.Bridge.Protocol;
using Panda.Bridge.Relay;

namespace Panda.TestSupport;

/// <summary>
/// A relay the test clients talk to (plan-relay-conformance D5, seam S3): the in-process RelayFixture or a relay
/// process. Only what a client sees from outside: its WebSocket base address, its port and the public host it signs.
/// </summary>
internal interface IRelayEndpoint
{
    Uri Ws { get; }

    int Port { get; }

    string PublicHost { get; }

    /// <summary>A socket a helper opened against this relay (a relay process closes the open ones normally before it stops).</summary>
    void Opened(WebSocket socket)
    {
    }

    /// <summary>A value that must never appear in this relay's output: a token, a token hash, a streamId, a payload marker.</summary>
    void Secret(string value)
    {
    }
}

/// <summary>
/// The client side of the relay (spec §78 S1): what the extension and the bridge send. One implementation, linked
/// into every relay test project (never copied).
/// </summary>
internal static class RelayClient
{
    /// <summary>What an extension's WebSocket carries; a bridge (.NET ClientWebSocket) sends no Origin.</summary>
    public const string ExtOrigin = "chrome-extension://abcdefghijklmnopabcdefghijklmnop";

    private const string Code = "ABCD-EFGH-IJKL-MNOP-QRST-UVWXYZ"; // any valid pair code shape works for a token

    /// <summary>Every connect is bounded: a relay that accepts TCP and never answers the upgrade fails the test, never hangs it.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private static CancellationTokenSource ConnectLimit()
    {
        var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(ConnectTimeout);
        return limit;
    }

    public static async Task<FakeChrome> ChromeAsync(this IRelayEndpoint relay, string? link = null, string? pair = null, ECDsa? key = null)
    {
        var chrome = new FakeChrome(relay, key ?? ECDsa.Create(ECCurve.NamedCurves.nistP256));
        foreach (var hash in new[] { link, pair })
        {
            if (hash is not null)
            {
                relay.Secret(hash);
            }
        }

        await chrome.RegisterAsync(link, pair);
        return chrome;
    }

    /// <summary>A bridge dialling a relayId; <paramref name="token"/> null sends no X-Panda-Dial header.</summary>
    public static async Task<ClientWebSocket> DialAsync(this IRelayEndpoint relay, string relayId, string kind, string? token)
    {
        var ws = Client(null, token);
        if (token is not null)
        {
            relay.Secret(token);
        }

        relay.Opened(ws);
        using var limit = ConnectLimit();
        await ws.ConnectAsync(new Uri(relay.Ws, $"/v1/stream?dial={relayId}&kind={kind}"), limit.Token);
        return ws;
    }

    /// <summary>Connects with the Origin the real client of that path sends (the extension for /v1/browser and ?accept=).</summary>
    public static Task<ClientWebSocket> ConnectAsync(this IRelayEndpoint relay, string pathAndQuery) =>
        relay.ConnectAsync(pathAndQuery, pathAndQuery.StartsWith("/v1/browser", StringComparison.Ordinal) || pathAndQuery.Contains("accept=", StringComparison.Ordinal) ? ExtOrigin : null);

    /// <summary>Connects with <paramref name="origin"/> (null = no Origin header).</summary>
    public static async Task<ClientWebSocket> ConnectAsync(this IRelayEndpoint relay, string pathAndQuery, string? origin, string? token = null)
    {
        var ws = Client(origin, token);
        if (token is not null)
        {
            relay.Secret(token);
        }

        relay.Opened(ws);
        using var limit = ConnectLimit();
        await ws.ConnectAsync(new Uri(relay.Ws, pathAndQuery), limit.Token);
        return ws;
    }

    /// <summary>
    /// A hand-written upgrade on a raw socket, so a test controls every request header line (repeated lines included:
    /// ClientWebSocket sends one line per name). Sends the extension Origin; asserts 101.
    /// </summary>
    public static Task<WebSocket> RawConnectAsync(this IRelayEndpoint relay, string pathAndQuery, params string[] headerLines) =>
        relay.RawUpgradeAsync(ExtOrigin, pathAndQuery, headerLines);

    /// <summary>The raw upgrade with <paramref name="origin"/> (null = no Origin line, as a bridge's dial); asserts 101.</summary>
    public static async Task<WebSocket> RawUpgradeAsync(this IRelayEndpoint relay, string? origin, string pathAndQuery, params string[] headerLines)
    {
        using var limit = ConnectLimit();
        var ct = limit.Token;
        Assert.Equal("127.0.0.1", relay.Ws.Host); // the same address as every other helper (one source: Ws)
        var port = relay.Ws.Port;
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, port, ct);
        var stream = new NetworkStream(socket, ownsSocket: true);
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var request = $"GET {pathAndQuery} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
            + $"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n" + (origin is null ? "" : $"Origin: {origin}\r\n")
            + string.Concat(headerLines.Select(l => l + "\r\n")) + "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct);
        var head = new StringBuilder();
        var one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal)) // byte by byte: no frame bytes are eaten
        {
            Assert.Equal(1, await stream.ReadAsync(one, ct));
            head.Append((char)one[0]);
        }

        Assert.StartsWith("HTTP/1.1 101", head.ToString(), StringComparison.Ordinal);
        var ws = WebSocket.CreateFromStream(stream, isServer: false, subProtocol: null, keepAliveInterval: Timeout.InfiniteTimeSpan);
        relay.Opened(ws);
        return ws;
    }

    /// <summary>The HTTP status of an upgrade the relay refuses before it becomes a WebSocket.</summary>
    public static async Task<HttpStatusCode> RefusedUpgradeAsync(this IRelayEndpoint relay, string pathAndQuery, string? origin, string? token = null)
    {
        using var ws = Client(origin, token);
        if (token is not null)
        {
            relay.Secret(token);
        }

        ws.Options.CollectHttpResponseDetails = true;
        using var limit = ConnectLimit();
        await Assert.ThrowsAsync<WebSocketException>(() => ws.ConnectAsync(new Uri(relay.Ws, pathAndQuery), limit.Token));
        return ws.HttpStatusCode;
    }

    private static ClientWebSocket Client(string? origin, string? token)
    {
        var ws = new ClientWebSocket();
        if (origin is not null)
        {
            ws.Options.SetRequestHeader("Origin", origin);
        }

        if (token is not null)
        {
            ws.Options.SetRequestHeader("X-Panda-Dial", token);
        }

        return ws;
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
    internal static async Task<Online> OnlineAsync(this IRelayEndpoint relay)
    {
        var (key, _, link, pair) = Identity();
        relay.Secret(link.Token);
        relay.Secret(pair.Token);
        return new Online(await relay.ChromeAsync(link.Hash, pair.Hash, key), link, pair);
    }

    internal static async Task<(ClientWebSocket Dial, ClientWebSocket Ext)> OpenStreamAsync(this IRelayEndpoint relay, Online chrome, string kind, string token)
    {
        var dial = await relay.DialAsync(chrome.Chrome.RelayId, kind, token);
        var incoming = await chrome.Chrome.IncomingAsync();
        Assert.Equal(kind == "link" ? RelayStreamKind.Link : RelayStreamKind.Pair, incoming.Kind);
        var ext = await chrome.Chrome.AcceptAsync(incoming.StreamId);
        Assert.IsType<RelayStreamOpen>(await Wire.ReceiveAsync(dial));
        return (dial, ext);
    }
}

/// <summary>The extension side of the control socket (S1), with its own P-256 identity key.</summary>
internal sealed class FakeChrome : IAsyncDisposable
{
    private readonly IRelayEndpoint _relay;

    public FakeChrome(IRelayEndpoint relay, ECDsa key)
    {
        _relay = relay;
        Key = key;
        Spki = key.ExportSubjectPublicKeyInfo();
        RelayId = DialTokens.RelayIdFromSpki(Spki);
    }

    public ECDsa Key { get; }
    public byte[] Spki { get; }
    public string RelayId { get; }
    public ClientWebSocket Control { get; private set; } = new();

    public static byte[] SignedBytes(byte[] nonce, string host) =>
        [.. Encoding.ASCII.GetBytes("panda-relay-register-v1"), 0, .. nonce, .. Encoding.ASCII.GetBytes(host)];

    /// <summary>Opens the control socket (<paramref name="query"/> appended to the path) and returns the challenge nonce (raw bytes).</summary>
    public async Task<byte[]> OpenAsync(string query = "")
    {
        Control = await _relay.ConnectAsync("/v1/browser" + query);
        var challenge = Assert.IsType<RelayChallenge>(await Wire.ReceiveAsync(Control));
        Assert.True(AuthCrypto.TryDecodeBase64Url(challenge.Nonce, 32, out var nonce));
        return nonce;
    }

    /// <summary>A RELAY_REGISTER signed for <paramref name="host"/> (null = the relay's own public host).</summary>
    public RelayRegister Register(byte[] nonce, string? link = null, string? pair = null, string? host = null) =>
        new(AuthCrypto.EncodeBase64Url(Spki),
            AuthCrypto.EncodeBase64Url(Key.SignData(SignedBytes(nonce, host ?? _relay.PublicHost), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)),
            "0.7.0", link, pair);

    public async Task RegisterAsync(string? link, string? pair)
    {
        var nonce = await OpenAsync();
        await Wire.SendAsync(Control, Register(nonce, link, pair));
        var registered = Assert.IsType<RelayRegistered>(await Wire.ReceiveAsync(Control));
        Assert.Equal(RelayId, registered.RelayId);
    }

    public async Task<RelayIncoming> IncomingAsync()
    {
        var incoming = Assert.IsType<RelayIncoming>(await Wire.ReceiveAsync(Control));
        _relay.Secret(incoming.StreamId); // with an extension Origin, a streamId is enough to accept that stream
        return incoming;
    }

    public Task<ClientWebSocket> AcceptAsync(string streamId, string query = "") => _relay.ConnectAsync($"/v1/stream?accept={streamId}{query}");

    public async ValueTask DisposeAsync()
    {
        Control.Dispose();
        Key.Dispose();
        await Task.CompletedTask;
    }
}

internal static class Wire
{
    /// <summary>How long a receive waits by default; a test waiting for a relay timer passes a longer one.</summary>
    public static readonly TimeSpan DefaultReceive = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static Task SendAsync(WebSocket ws, IRelayMessage message) =>
        SendLimitedAsync(ws, Encoding.UTF8.GetBytes(ProtocolJson.SerializeRelay(message)), WebSocketMessageType.Text);

    public static Task SendTextAsync(WebSocket ws, string text) =>
        SendLimitedAsync(ws, Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text);

    public static Task SendBinaryAsync(WebSocket ws, byte[] bytes) => SendLimitedAsync(ws, bytes, WebSocketMessageType.Binary);

    /// <summary>A close handshake: sends <paramref name="code"/> and waits at most 10 s for the peer's close (a peer that never answers fails, never hangs).</summary>
    public static async Task CloseAsync(WebSocket ws, int code, string reason = "")
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(DefaultReceive);
        await ws.CloseAsync((WebSocketCloseStatus)code, reason, limit.Token);
    }

    private static async Task SendLimitedAsync(WebSocket ws, byte[] bytes, WebSocketMessageType type)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(DefaultReceive);
        await ws.SendAsync(bytes, type, true, limit.Token);
    }

    /// <summary>One whole message (type + bytes), or the close the peer sent (type Close, no bytes); waits at most <paramref name="timeout"/> (default 10 s).</summary>
    public static async Task<(WebSocketMessageType Type, byte[] Bytes)> ReceiveRawAsync(WebSocket ws, TimeSpan? timeout = null)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(timeout ?? DefaultReceive);
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, limit.Token);
            if (r.MessageType == WebSocketMessageType.Close)
            {
                return (WebSocketMessageType.Close, []);
            }

            message.Write(buffer, 0, r.Count);
            if (r.EndOfMessage)
            {
                return (r.MessageType, message.ToArray());
            }
        }
    }

    public static async Task<IRelayMessage> ReceiveAsync(WebSocket ws, TimeSpan? timeout = null)
    {
        var (type, bytes) = await ReceiveRawAsync(ws, timeout);
        Assert.Equal(WebSocketMessageType.Text, type);
        return Assert.IsType<RelayParseResult.Ok>(ProtocolJson.ParseRelay(Encoding.UTF8.GetString(bytes))).Message;
    }

    /// <summary>Reads until the peer's close frame and returns its code and reason; <paramref name="timeout"/> limits each frame's wait, not the whole call.</summary>
    public static async Task<(int Code, string Reason)> ClosedAsync(WebSocket ws, TimeSpan? timeout = null)
    {
        while (true)
        {
            var (type, _) = await ReceiveRawAsync(ws, timeout);
            if (type == WebSocketMessageType.Close)
            {
                return ((int)ws.CloseStatus!.Value, ws.CloseStatusDescription ?? "");
            }
        }
    }
}
