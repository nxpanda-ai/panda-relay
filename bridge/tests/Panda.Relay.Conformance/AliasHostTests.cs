using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Panda.Bridge.Protocol;

namespace Panda.Relay.Conformance;

/// <summary>
/// plan-relay-rename D1 / D1b (A 2026-11-24): a relay started with <c>--alias-host</c> also serves that host — a
/// registration signed for it verifies, one signed for any other host is still 4503, and the hosted MCP URL a Chrome is
/// told is on the host it registered with (an extension accepts an MCP URL only on its relay's host).
/// </summary>
public class AliasHostTests
{
    private const string Alias = "old.relay.test"; // as production passes it (no :443, the host an extension signs)
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task A_registration_signed_for_an_alias_host_registers()
    {
        await using var relay = await RelayProcess.StartAsync(["--alias-host", Alias], line => TestContext.Current.SendDiagnosticMessage(line));
        await using var chrome = await RegisterAsync(relay, Alias);
    }

    [Fact]
    public async Task With_an_alias_a_registration_signed_for_a_third_host_is_closed_4503()
    {
        await using var relay = await RelayProcess.StartAsync(["--alias-host", Alias], line => TestContext.Current.SendDiagnosticMessage(line));
        await using var chrome = new FakeChrome(relay, ECDsa.Create(ECCurve.NamedCurves.nistP256));
        var nonce = await chrome.OpenAsync();

        await Wire.SendAsync(chrome.Control, chrome.Register(nonce, host: "evil.test:443"));

        Assert.Equal(RelayCloseCodes.RegistrationInvalid, (await Wire.ClosedAsync(chrome.Control)).Code);
    }

    [Fact]
    public async Task A_rotate_answers_the_mcp_url_on_the_host_the_Chrome_registered_with()
    {
        using var admin = new OkAdmin();
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, Cli.Token, TestContext.Current.CancellationToken);
            await using var relay = await RelayProcess.StartAsync(
                ["--alias-host", Alias, "--hosted-admin-url", admin.Url, "--hosted-admin-token-file", file,
                    "--hosted-mcp-url", $"https://{RelayProcess.PublicHostName}/mcp"],
                line => TestContext.Current.SendDiagnosticMessage(line));
            relay.Secret(Cli.Token);
            await using var viaAlias = await RegisterAsync(relay, Alias);
            await using var viaPrimary = await RegisterAsync(relay, relay.PublicHost);

            await Wire.SendAsync(viaAlias.Control, new RelayRotate(1, Sha));
            Assert.Equal(new RelayHostResult(1, true, "https://old.relay.test/mcp"), await Wire.ReceiveAsync(viaAlias.Control));
            await Wire.SendAsync(viaPrimary.Control, new RelayRotate(2, Sha));
            Assert.Equal(new RelayHostResult(2, true, $"https://{relay.PublicHost.Split(':')[0]}/mcp"), await Wire.ReceiveAsync(viaPrimary.Control));
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static async Task<FakeChrome> RegisterAsync(RelayProcess relay, string host)
    {
        var chrome = new FakeChrome(relay, ECDsa.Create(ECCurve.NamedCurves.nistP256));
        var nonce = await chrome.OpenAsync();
        await Wire.SendAsync(chrome.Control, chrome.Register(nonce, host: host));
        Assert.Equal(new RelayRegistered(chrome.RelayId), await Wire.ReceiveAsync(chrome.Control));
        return chrome;
    }

    /// <summary>A stand-in for the hosted bridge's admin port: answers every request 200 with no body (raw TCP, no URL ACL).</summary>
    private sealed class OkAdmin : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public OkAdmin()
        {
            _listener.Start();
            _ = ServeAsync();
        }

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture)}";

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                _ = AnswerAsync(client);
            }
        }

        private static async Task AnswerAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var head = new StringBuilder();
                var buffer = new byte[4096];
                int read;
                while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal) && (read = await stream.ReadAsync(buffer)) > 0)
                {
                    head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                // the whole body too: unread bytes at close can reset the socket and lose the answer (diff critic F7)
                var text = head.ToString();
                var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                var length = System.Text.RegularExpressions.Regex.Match(text, @"(?im)^content-length:\s*(\d+)\s*$");
                var missing = end < 0 || !length.Success ? 0 : int.Parse(length.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
                    - Encoding.ASCII.GetByteCount(text[(end + 4)..]);
                while (missing > 0 && (read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, missing)))) > 0)
                {
                    missing -= read;
                }

                await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
            }
        }
    }
}
