using System.Net;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Panda.Bridge.Protocol;

namespace Panda.Relay.Tests;

/// <summary>
/// plan-relay-url U3, seams S1 (extension ↔ relay) and S2 (relay → hosted bridge admin): the relay forwards
/// RELAY_HOST / ROTATE / UNHOST of a registered control socket to the hosted bridge with its own credential, maps the
/// answer to RELAY_HOST_RESULT, never blocks its control loop, and never logs the code or the token hash.
/// </summary>
public sealed class HostedControlTests : IAsyncLifetime
{
    private const string McpUrl = "https://relay.test/mcp";
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string PairCode = "ABCDE-FGHIJ-KLMNO-PQRST-UVWXYZ";
    private static readonly string AdminToken = new('A', 43);

    private FakeHostedBridge _admin = null!;
    private RelayFixture _relay = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _admin = await FakeHostedBridge.StartAsync();
        _relay = await RelayFixture.StartAsync(o =>
        {
            o.HostedAdminUrl = _admin.Url;
            o.HostedAdminToken = AdminToken;
            o.HostedMcpUrl = McpUrl;
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _relay.DisposeAsync();
        await _admin.DisposeAsync();
    }

    [Fact]
    public async Task Host_forwards_the_code_and_hash_with_the_relays_credential_and_answers_ok_with_the_mcp_url()
    {
        await using var chrome = await _relay.ChromeAsync();
        var code = CodeFor(chrome.RelayId);

        await Wire.SendAsync(chrome.Control, new RelayHost(1, code, Sha));

        var result = Assert.IsType<RelayHostResult>(await Wire.ReceiveAsync(chrome.Control));
        Assert.Equal(new RelayHostResult(1, true, McpUrl), result);
        var call = Assert.Single(_admin.Calls);
        Assert.Equal("/admin/hosted/add", call.Path);
        Assert.Equal(AdminToken, call.Token);
        Assert.Equal((code, Sha), (call.Body["code"]!.GetValue<string>(), call.Body["tokenSha256"]!.GetValue<string>()));
        Assert.DoesNotContain(_relay.Logs.Lines, l => l.Contains(PairCode, StringComparison.Ordinal)
            || l.Contains(PairCode.Replace("-", "", StringComparison.Ordinal), StringComparison.Ordinal)
            || l.Contains(Sha, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rotate_forwards_the_sockets_own_relayId_and_answers_ok_with_the_mcp_url()
    {
        await using var chrome = await _relay.ChromeAsync();

        await Wire.SendAsync(chrome.Control, new RelayRotate(2, Sha));

        Assert.Equal(new RelayHostResult(2, true, McpUrl), await Wire.ReceiveAsync(chrome.Control));
        var call = Assert.Single(_admin.Calls);
        Assert.Equal("/admin/hosted/rotate", call.Path);
        Assert.Equal((chrome.RelayId, Sha), (call.Body["relayId"]!.GetValue<string>(), call.Body["tokenSha256"]!.GetValue<string>()));
        Assert.DoesNotContain(_relay.Logs.Lines, l => l.Contains(Sha, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unhost_forwards_the_sockets_own_relayId_and_answers_ok_without_a_url()
    {
        await using var chrome = await _relay.ChromeAsync();

        await Wire.SendAsync(chrome.Control, new RelayUnhost(3));

        Assert.Equal(new RelayHostResult(3, true), await Wire.ReceiveAsync(chrome.Control));
        var call = Assert.Single(_admin.Calls);
        Assert.Equal(("/admin/hosted/remove", chrome.RelayId), (call.Path, call.Body["relayId"]!.GetValue<string>()));
    }

    // S2 CONSUMER RELIES ON: each status has one meaning.
    [Theory]
    [InlineData(400, "AUTH_FAILED", RelayHostError.Failed)]
    [InlineData(422, "PERMISSION_DENIED", RelayHostError.Mismatch)]
    [InlineData(408, "COMMAND_TIMEOUT", RelayHostError.Timeout)]
    [InlineData(409, "AUTH_FAILED", RelayHostError.Rejected)]
    [InlineData(409, "PERMISSION_DENIED", RelayHostError.Conflict)]
    [InlineData(409, "BROWSER_NOT_FOUND", RelayHostError.NotHosted)]
    [InlineData(409, "SOMETHING_ELSE", RelayHostError.Failed)]
    [InlineData(500, "INTERNAL_ERROR", RelayHostError.Failed)]
    [InlineData(401, "AUTH_FAILED", RelayHostError.Failed)]
    public async Task A_hosted_bridge_answer_maps_to_one_error(int status, string errorCode, RelayHostError expected)
    {
        await using var chrome = await _relay.ChromeAsync();
        _admin.Answer = (status, errorCode);

        await Wire.SendAsync(chrome.Control, new RelayRotate(4, Sha));

        Assert.Equal(new RelayHostResult(4, false, Error: expected), await Wire.ReceiveAsync(chrome.Control));
    }

    [Fact]
    public async Task A_code_for_another_Chrome_is_a_mismatch_and_reaches_no_bridge()
    {
        await using var chrome = await _relay.ChromeAsync();
        await using var other = await _relay.ChromeAsync();

        await Wire.SendAsync(chrome.Control, new RelayHost(5, CodeFor(other.RelayId), Sha));

        Assert.Equal(new RelayHostResult(5, false, Error: RelayHostError.Mismatch), await Wire.ReceiveAsync(chrome.Control));
        Assert.Empty(_admin.Calls);
    }

    [Fact]
    public async Task A_code_that_does_not_parse_fails_and_reaches_no_bridge()
    {
        await using var chrome = await _relay.ChromeAsync();

        await Wire.SendAsync(chrome.Control, new RelayHost(6, "not-a-connection-code", Sha));

        Assert.Equal(new RelayHostResult(6, false, Error: RelayHostError.Failed), await Wire.ReceiveAsync(chrome.Control));
        Assert.Empty(_admin.Calls);
    }

    [Fact]
    public async Task One_request_per_socket_a_second_is_busy_and_pings_are_still_answered()
    {
        await using var chrome = await _relay.ChromeAsync();
        _admin.Hold();

        await Wire.SendAsync(chrome.Control, new RelayHost(7, CodeFor(chrome.RelayId), Sha));
        await _admin.ArrivedAsync();
        await Wire.SendAsync(chrome.Control, new RelayRotate(8, Sha));
        Assert.Equal(new RelayHostResult(8, false, Error: RelayHostError.Busy), await Wire.ReceiveAsync(chrome.Control));
        await Wire.SendAsync(chrome.Control, new RelayPing());
        Assert.IsType<RelayPong>(await Wire.ReceiveAsync(chrome.Control)); // D6: the control loop never waits for a host

        _admin.Release();
        Assert.Equal(new RelayHostResult(7, true, McpUrl), await Wire.ReceiveAsync(chrome.Control));
        await Wire.SendAsync(chrome.Control, new RelayUnhost(9)); // the slot is free again
        Assert.Equal(new RelayHostResult(9, true), await Wire.ReceiveAsync(chrome.Control));
    }

    [Fact]
    public async Task A_result_for_a_closed_socket_is_dropped_and_the_next_socket_is_served()
    {
        var chrome = await _relay.ChromeAsync();
        _admin.Hold();
        await Wire.SendAsync(chrome.Control, new RelayUnhost(10));
        await _admin.ArrivedAsync();
        await chrome.Control.CloseAsync(WebSocketCloseStatus.NormalClosure, "", Ct);

        _admin.Release();
        await using var again = await _relay.ChromeAsync(key: chrome.Key);
        await Wire.SendAsync(again.Control, new RelayUnhost(1));
        Assert.Equal(new RelayHostResult(1, true), await Wire.ReceiveAsync(again.Control));
        await chrome.DisposeAsync();
    }

    [Fact]
    public async Task An_unreachable_hosted_bridge_fails()
    {
        await using var chrome = await _relay.ChromeAsync();
        await _admin.DisposeAsync(); // nothing listens there now

        await Wire.SendAsync(chrome.Control, new RelayUnhost(11));

        Assert.Equal(new RelayHostResult(11, false, Error: RelayHostError.Failed), await Wire.ReceiveAsync(chrome.Control));
    }

    [Fact]
    public async Task A_hosted_bridge_that_never_answers_is_a_timeout_whose_outcome_is_unknown()
    {
        await using var admin = await FakeHostedBridge.StartAsync();
        await using var relay = await RelayFixture.StartAsync(o =>
        {
            o.HostedAdminUrl = admin.Url;
            o.HostedAdminToken = AdminToken;
            o.HostedMcpUrl = McpUrl;
            o.HostedTimeout = TimeSpan.FromMilliseconds(500);
        });
        await using var chrome = await relay.ChromeAsync();
        admin.Hold();

        await Wire.SendAsync(chrome.Control, new RelayUnhost(12));

        // the add may still finish there (critic U3 #1): "timeout" tells the Chrome the outcome is unknown, so it unhosts
        Assert.Equal(new RelayHostResult(12, false, Error: RelayHostError.Timeout), await Wire.ReceiveAsync(chrome.Control));
        admin.Release();
    }

    [Fact]
    public async Task Host_and_rotate_answer_the_mcp_url_on_the_host_the_Chrome_registered_with()
    {
        // plan-relay-rename D1b: an extension accepts an MCP URL only on its relay's host (relayLink.ts #acceptsMcpUrl)
        await using var relay = await RelayFixture.StartAsync(o =>
        {
            o.HostedAdminUrl = _admin.Url;
            o.HostedAdminToken = AdminToken;
            o.HostedMcpUrl = McpUrl;
            o.AliasHosts.Add("old.test:443");
        });
        await using var viaAlias = new FakeChrome(relay, System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256));
        var nonce = await viaAlias.OpenAsync();
        await Wire.SendAsync(viaAlias.Control, viaAlias.Register(nonce, host: "old.test:443"));
        Assert.IsType<RelayRegistered>(await Wire.ReceiveAsync(viaAlias.Control));
        await using var viaPrimary = await relay.ChromeAsync();

        await Wire.SendAsync(viaAlias.Control, new RelayHost(1, CodeFor(viaAlias.RelayId), Sha));
        Assert.Equal(new RelayHostResult(1, true, "https://old.test/mcp"), await Wire.ReceiveAsync(viaAlias.Control));
        await Wire.SendAsync(viaAlias.Control, new RelayRotate(2, Sha));
        Assert.Equal(new RelayHostResult(2, true, "https://old.test/mcp"), await Wire.ReceiveAsync(viaAlias.Control));
        await Wire.SendAsync(viaPrimary.Control, new RelayRotate(3, Sha));
        Assert.Equal(new RelayHostResult(3, true, McpUrl), await Wire.ReceiveAsync(viaPrimary.Control));
    }

    [Fact]
    public async Task A_relay_without_a_hosted_bridge_is_unavailable()
    {
        await using var relay = await RelayFixture.StartAsync();
        await using var chrome = await relay.ChromeAsync();

        await Wire.SendAsync(chrome.Control, new RelayHost(13, CodeFor(chrome.RelayId), Sha));

        Assert.Equal(new RelayHostResult(13, false, Error: RelayHostError.Unavailable), await Wire.ReceiveAsync(chrome.Control));
    }

    // ---- options: all or none, loopback admin URL, the MCP URL on the public host ------------------------------------

    [Theory]
    [InlineData("http://127.0.0.1:47822", null, McpUrl)] // all or none
    [InlineData(null, "token", McpUrl)]
    [InlineData("http://127.0.0.1:47822", "token", null)]
    [InlineData("http://admin.example.com:47822", "token", McpUrl)] // the admin port is loopback only
    [InlineData("https://127.0.0.1:47822", "token", McpUrl)]
    [InlineData("http://[::1]:47822", "token", McpUrl)] // the hosted bridge listens on 127.0.0.1 only
    [InlineData("http://127.0.0.1:47822/admin", "token", McpUrl)]
    [InlineData("http://127.0.0.1:47822", "short", McpUrl)] // the token file's 32 bytes, base64url
    [InlineData("http://127.0.0.1:47822", "token", "http://relay.test/mcp")] // https only off loopback
    [InlineData("http://127.0.0.1:47822", "token", "https://other.test/mcp")] // the host extensions accept (D4)
    public void Hosted_options_are_all_or_none_and_well_formed(string? adminUrl, string? token, string? mcpUrl)
    {
        var options = new RelayOptions
        {
            PublicHost = RelayFixture.PublicHost,
            HostedAdminUrl = adminUrl is null ? null : new Uri(adminUrl),
            HostedAdminToken = token == "token" ? AdminToken : token,
            HostedMcpUrl = mcpUrl,
        };

        Assert.ThrowsAny<ArgumentException>(options.Validate);
    }

    [Fact]
    public void A_loopback_relay_may_name_an_http_loopback_mcp_url()
    {
        var options = new RelayOptions
        {
            PublicHost = "127.0.0.1:47830",
            HostedAdminUrl = new Uri("http://127.0.0.1:47822"),
            HostedAdminToken = AdminToken,
            HostedMcpUrl = "http://127.0.0.1:47823/mcp",
        };

        options.Validate();
    }

    // ---- plan-relay-deploy C3 (D3): hosted calls in flight across ALL sockets are capped; over the cap → busy ----------

    [Fact]
    public async Task Hosted_calls_in_flight_across_sockets_are_capped_and_the_next_one_is_busy()
    {
        await using var admin = await FakeHostedBridge.StartAsync();
        await using var relay = await CappedRelayAsync(admin, TimeSpan.FromSeconds(30));
        await using var a = await relay.ChromeAsync();
        await using var b = await relay.ChromeAsync();
        admin.Hold();

        await Wire.SendAsync(a.Control, new RelayHost(1, CodeFor(a.RelayId), Sha));
        await admin.ArrivedAsync();
        await Wire.SendAsync(b.Control, new RelayRotate(2, Sha)); // another socket: its own per-socket slot is free
        Assert.Equal(new RelayHostResult(2, false, Error: RelayHostError.Busy), await Wire.ReceiveAsync(b.Control));

        admin.Release();
        Assert.Equal(new RelayHostResult(1, true, McpUrl), await Wire.ReceiveAsync(a.Control));
        await Wire.SendAsync(b.Control, new RelayRotate(3, Sha));
        Assert.Equal(new RelayHostResult(3, true, McpUrl), await Wire.ReceiveAsync(b.Control));
    }

    [Fact]
    public async Task UNHOST_is_never_held_back_by_the_cap_so_no_one_can_delay_a_revocation()
    {
        // C3 critic T1: a full cap must not keep a hosted bridge in control of a Chrome that asked to unhost
        await using var admin = await FakeHostedBridge.StartAsync();
        await using var relay = await CappedRelayAsync(admin, TimeSpan.FromSeconds(30));
        await using var a = await relay.ChromeAsync();
        await using var b = await relay.ChromeAsync();
        admin.Hold();
        await Wire.SendAsync(a.Control, new RelayHost(1, CodeFor(a.RelayId), Sha));
        await admin.ArrivedAsync();

        await Wire.SendAsync(b.Control, new RelayUnhost(2));
        admin.Release(); // a capped UNHOST would already have its busy answer queued
        Assert.Equal(new RelayHostResult(2, true), await Wire.ReceiveAsync(b.Control));
        Assert.Contains(admin.Calls, c => c.Path == "/admin/hosted/remove");
        Assert.Equal(new RelayHostResult(1, true, McpUrl), await Wire.ReceiveAsync(a.Control));
    }

    [Fact]
    public async Task The_cap_slot_is_freed_after_a_failure_and_after_a_timeout()
    {
        await using var admin = await FakeHostedBridge.StartAsync();
        await using var relay = await CappedRelayAsync(admin, TimeSpan.FromMilliseconds(500));
        await using var a = await relay.ChromeAsync();
        await using var b = await relay.ChromeAsync();

        await Wire.SendAsync(a.Control, new RelayHost(1, "not-a-connection-code", Sha));
        Assert.Equal(new RelayHostResult(1, false, Error: RelayHostError.Failed), await Wire.ReceiveAsync(a.Control));
        admin.Hold();
        await Wire.SendAsync(a.Control, new RelayRotate(2, Sha));
        Assert.Equal(new RelayHostResult(2, false, Error: RelayHostError.Timeout), await Wire.ReceiveAsync(a.Control));

        admin.Release();
        await Wire.SendAsync(b.Control, new RelayRotate(3, Sha));
        Assert.Equal(new RelayHostResult(3, true, McpUrl), await Wire.ReceiveAsync(b.Control));
    }

    [Fact]
    public void A_cap_below_one_is_refused()
    {
        var options = new RelayOptions { PublicHost = "relay.test", MaxHosting = 0 };
        Assert.ThrowsAny<ArgumentException>(options.Validate);
    }

    private static Task<RelayFixture> CappedRelayAsync(FakeHostedBridge admin, TimeSpan timeout) =>
        RelayFixture.StartAsync(o =>
        {
            o.HostedAdminUrl = admin.Url;
            o.HostedAdminToken = AdminToken;
            o.HostedMcpUrl = McpUrl;
            o.HostedTimeout = timeout;
            o.MaxHosting = 1;
        });

    // ------------------------------------------------------------------ helpers

    private static string CodeFor(string relayId) => $"panda-relay://relay.test/{relayId}#{PairCode}";

    /// <summary>A stand-in for the hosted bridge's <c>/admin/hosted/*</c>: records calls, answers <see cref="Answer"/>.</summary>
    private sealed class FakeHostedBridge : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly List<Call> _calls = [];
        private TaskCompletionSource _gate = Open();
        private TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _disposed;

        private FakeHostedBridge(WebApplication app) => _app = app;

        public Uri Url { get; private set; } = null!;

        public (int Status, string Code)? Answer { get; set; }

        public IReadOnlyList<Call> Calls
        {
            get
            {
                lock (_calls)
                {
                    return [.. _calls];
                }
            }
        }

        public static async Task<FakeHostedBridge> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            var fake = new FakeHostedBridge(app);
            app.MapPost("/admin/hosted/{action}", fake.HandleAsync);
            await app.StartAsync(TestContext.Current.CancellationToken);
            fake.Url = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
            return fake;
        }

        public void Hold() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult();

        public Task ArrivedAsync() => _arrived.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                Release();
                await _app.DisposeAsync();
            }
        }

        private static TaskCompletionSource Open()
        {
            var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            open.SetResult();
            return open;
        }

        private async Task<IResult> HandleAsync(HttpContext context, string action)
        {
            var body = JsonNode.Parse(await new StreamReader(context.Request.Body).ReadToEndAsync())!;
            lock (_calls)
            {
                _calls.Add(new Call(context.Request.Path, context.Request.Headers["X-Panda-Admin"].ToString(), body));
            }

            _arrived.TrySetResult();
            await _gate.Task;
            if (Answer is { } answer)
            {
                return Results.Json(new { code = answer.Code, message = "x", recoverable = false }, statusCode: answer.Status);
            }

            return action switch
            {
                "add" => Results.Json(new { browserId = "chrome-hostedaa" }),
                "rotate" => Results.Json(new { rotated = body["relayId"]!.GetValue<string>() }),
                _ => Results.Json(new { removed = body["relayId"]!.GetValue<string>() }),
            };
        }
    }

    private sealed record Call(string Path, string Token, JsonNode Body);
}
