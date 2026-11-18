using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Panda.Bridge.Auth;
using Panda.Bridge.Protocol;
using Panda.Bridge.Relay;

namespace Panda.Relay.Tests;

/// <summary>A real relay on 127.0.0.1:0 with a fake clock and captured logs.</summary>
internal sealed class RelayFixture : IAsyncDisposable
{
    public const string PublicHost = "relay.test:443";

    /// <summary>What an extension's WebSocket carries; a bridge (.NET ClientWebSocket) sends no Origin.</summary>
    public const string ExtOrigin = "chrome-extension://abcdefghijklmnopabcdefghijklmnop";

    private RelayFixture(RelayServer server, FakeTimeProvider time, LogSink logs)
    {
        Server = server;
        Time = time;
        Logs = logs;
    }

    public RelayServer Server { get; }
    public FakeTimeProvider Time { get; }
    public LogSink Logs { get; }
    public Uri Ws => new($"ws://127.0.0.1:{Server.Port}");

    public static async Task<RelayFixture> StartAsync(Action<RelayOptions>? configure = null, DateTimeOffset? now = null)
    {
        var options = new RelayOptions { PublicHost = PublicHost };
        configure?.Invoke(options);
        var time = new FakeTimeProvider(now ?? new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));
        var logs = new LogSink();
        var server = await RelayServer.StartAsync(options, time, logs, TestContext.Current.CancellationToken);
        return new RelayFixture(server, time, logs);
    }

    public async Task<FakeChrome> ChromeAsync(string? link = null, string? pair = null, ECDsa? key = null)
    {
        var chrome = new FakeChrome(this, key ?? ECDsa.Create(ECCurve.NamedCurves.nistP256));
        await chrome.RegisterAsync(link, pair);
        return chrome;
    }

    /// <summary>A bridge dialling a relayId; <paramref name="token"/> null sends no X-Panda-Dial header.</summary>
    public async Task<ClientWebSocket> DialAsync(string relayId, string kind, string? token)
    {
        var ws = new ClientWebSocket();
        if (token is not null)
        {
            ws.Options.SetRequestHeader("X-Panda-Dial", token);
        }

        await ws.ConnectAsync(new Uri(Ws, $"/v1/stream?dial={relayId}&kind={kind}"), TestContext.Current.CancellationToken);
        return ws;
    }

    /// <summary>Connects with the Origin the real client of that path sends (the extension for /v1/browser and ?accept=).</summary>
    public Task<ClientWebSocket> ConnectAsync(string pathAndQuery) =>
        ConnectAsync(pathAndQuery, pathAndQuery.StartsWith("/v1/browser", StringComparison.Ordinal) || pathAndQuery.Contains("accept=", StringComparison.Ordinal) ? ExtOrigin : null);

    /// <summary>Connects with <paramref name="origin"/> (null = no Origin header).</summary>
    public async Task<ClientWebSocket> ConnectAsync(string pathAndQuery, string? origin, string? token = null)
    {
        var ws = Client(origin, token);
        await ws.ConnectAsync(new Uri(Ws, pathAndQuery), TestContext.Current.CancellationToken);
        return ws;
    }

    /// <summary>
    /// A hand-written upgrade on a raw socket, so a test controls every request header line (repeated lines included:
    /// ClientWebSocket sends one line per name). Sends the extension Origin; asserts 101.
    /// </summary>
    public async Task<WebSocket> RawConnectAsync(string pathAndQuery, params string[] headerLines)
    {
        var ct = TestContext.Current.CancellationToken;
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, Server.Port, ct);
        var stream = new NetworkStream(socket, ownsSocket: true);
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var request = $"GET {pathAndQuery} HTTP/1.1\r\nHost: 127.0.0.1:{Server.Port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
            + $"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\nOrigin: {ExtOrigin}\r\n"
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
        return WebSocket.CreateFromStream(stream, isServer: false, subProtocol: null, keepAliveInterval: Timeout.InfiniteTimeSpan);
    }

    /// <summary>The HTTP status of an upgrade the relay refuses before it becomes a WebSocket.</summary>
    public async Task<HttpStatusCode> RefusedUpgradeAsync(string pathAndQuery, string? origin, string? token = null)
    {
        using var ws = Client(origin, token);
        ws.Options.CollectHttpResponseDetails = true;
        await Assert.ThrowsAsync<WebSocketException>(() => ws.ConnectAsync(new Uri(Ws, pathAndQuery), TestContext.Current.CancellationToken));
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

    public ValueTask DisposeAsync() => Server.DisposeAsync();
}

/// <summary>The extension side of the control socket (S1), with its own P-256 identity key.</summary>
internal sealed class FakeChrome : IAsyncDisposable
{
    private readonly RelayFixture _relay;

    public FakeChrome(RelayFixture relay, ECDsa key)
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

    public RelayRegister Register(byte[] nonce, string? link = null, string? pair = null, string host = RelayFixture.PublicHost) =>
        new(AuthCrypto.EncodeBase64Url(Spki),
            AuthCrypto.EncodeBase64Url(Key.SignData(SignedBytes(nonce, host), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)),
            "0.7.0", link, pair);

    public async Task RegisterAsync(string? link, string? pair)
    {
        var nonce = await OpenAsync();
        await Wire.SendAsync(Control, Register(nonce, link, pair));
        var registered = Assert.IsType<RelayRegistered>(await Wire.ReceiveAsync(Control));
        Assert.Equal(RelayId, registered.RelayId);
    }

    public async Task<RelayIncoming> IncomingAsync() => Assert.IsType<RelayIncoming>(await Wire.ReceiveAsync(Control));

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
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static Task SendAsync(WebSocket ws, IRelayMessage message) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(ProtocolJson.SerializeRelay(message)), WebSocketMessageType.Text, true, Ct);

    public static Task SendTextAsync(WebSocket ws, string text) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, Ct);

    public static Task SendBinaryAsync(WebSocket ws, byte[] bytes) =>
        ws.SendAsync(bytes, WebSocketMessageType.Binary, true, Ct);

    /// <summary>One whole message (type + bytes), or the close the peer sent (type Close, no bytes).</summary>
    public static async Task<(WebSocketMessageType Type, byte[] Bytes)> ReceiveRawAsync(WebSocket ws)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, timeout.Token);
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

    public static async Task<IRelayMessage> ReceiveAsync(WebSocket ws)
    {
        var (type, bytes) = await ReceiveRawAsync(ws);
        Assert.Equal(WebSocketMessageType.Text, type);
        return Assert.IsType<RelayParseResult.Ok>(ProtocolJson.ParseRelay(Encoding.UTF8.GetString(bytes))).Message;
    }

    /// <summary>Reads until the peer's close frame and returns its code and reason.</summary>
    public static async Task<(int Code, string Reason)> ClosedAsync(WebSocket ws)
    {
        while (true)
        {
            var (type, _) = await ReceiveRawAsync(ws);
            if (type == WebSocketMessageType.Close)
            {
                return ((int)ws.CloseStatus!.Value, ws.CloseStatusDescription ?? "");
            }
        }
    }
}

/// <summary>Captures every log line the relay writes (no-secret checks).</summary>
internal sealed class LogSink : ILoggerProvider
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
            {
                return [.. _lines];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose()
    {
    }

    private sealed class Logger(LogSink sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (sink._lines)
            {
                sink._lines.Add($"{logLevel} {formatter(state, exception)} {exception}");
            }
        }
    }
}
