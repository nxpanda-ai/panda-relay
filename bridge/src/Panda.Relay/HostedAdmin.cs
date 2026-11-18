using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Panda.Bridge.Protocol;
using Panda.Bridge.Relay;

namespace Panda.Relay;

/// <summary>
/// plan-relay-url U3, seam S2 consumer: forwards a registered Chrome's RELAY_HOST / ROTATE / UNHOST to the hosted
/// bridge's <c>/admin/hosted/*</c> and maps the answer to RELAY_HOST_RESULT. The relay's only check is that a code names
/// the socket's own relayId (D6); the code's host is the hosted bridge's check (D5, one owner). The code and the token
/// hash travel only in the request body: never logged, never echoed (S1 INV).
/// </summary>
internal sealed partial class HostedAdmin : IDisposable
{
    private const int MaxAnswerBytes = 64 * 1024;

    private readonly RelayOptions _options;
    private readonly ILogger<HostedAdmin> _log;
    private readonly HttpClient _http;

    public HostedAdmin(RelayOptions options, ILogger<HostedAdmin> log)
    {
        _options = options;
        _log = log;
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            Timeout = options.HostedTimeout,
            MaxResponseContentBufferSize = MaxAnswerBytes,
        };
    }

    public bool Configured => _options.HostedAdminUrl is not null;

    public static long SeqOf(IRelayMessage request) => request switch
    {
        RelayHost host => host.Seq,
        RelayRotate rotate => rotate.Seq,
        RelayUnhost unhost => unhost.Seq,
        _ => throw new ArgumentException("not a hosting request", nameof(request)),
    };

    /// <summary>The result for <paramref name="request"/> from the Chrome registered as <paramref name="relayId"/>.</summary>
    public async Task<RelayHostResult> RunAsync(string relayId, IRelayMessage request)
    {
        switch (request)
        {
            case RelayHost host:
                if (!ConnectionCode.TryParse(host.Code, out var code))
                {
                    return Failed(host.Seq, RelayHostError.Failed);
                }

                if (code.RelayId != relayId)
                {
                    return Failed(host.Seq, RelayHostError.Mismatch); // a code for another Chrome never leaves the relay
                }

                return await PostAsync(relayId, host.Seq, "add", new JsonObject { ["code"] = host.Code, ["tokenSha256"] = host.TokenSha256 }, withUrl: true);
            case RelayRotate rotate:
                return await PostAsync(relayId, rotate.Seq, "rotate", new JsonObject { ["relayId"] = relayId, ["tokenSha256"] = rotate.TokenSha256 }, withUrl: true);
            case RelayUnhost unhost:
                return await PostAsync(relayId, unhost.Seq, "remove", new JsonObject { ["relayId"] = relayId }, withUrl: false);
            default:
                throw new ArgumentException("not a hosting request", nameof(request));
        }
    }

    public void Dispose() => _http.Dispose();

    private static RelayHostResult Failed(long seq, RelayHostError error) => new(seq, false, Error: error);

    private async Task<RelayHostResult> PostAsync(string relayId, long seq, string action, JsonObject body, bool withUrl)
    {
        HttpStatusCode status;
        string? errorCode;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_options.HostedAdminUrl!, "/admin/hosted/" + action))
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-Panda-Admin", _options.HostedAdminToken);
            using var response = await _http.SendAsync(request);
            status = response.StatusCode;
            if (status == HttpStatusCode.OK)
            {
                return new RelayHostResult(seq, true, withUrl ? _options.HostedMcpUrl : null);
            }

            errorCode = ErrorCode(await response.Content.ReadAsStringAsync());
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            // HostedTimeout passed: the add may still finish there (critic U3 #1) — "timeout" = outcome unknown, the
            // Chrome unhosts (D7 c); a 408 from the bridge maps to the same answer
            LogUnreachable(relayId, action, nameof(TimeoutException));
            return Failed(seq, RelayHostError.Timeout);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            LogUnreachable(relayId, action, ex.GetType().Name);
            return Failed(seq, RelayHostError.Failed);
        }

        // S2 CONSUMER RELIES ON: one meaning per status (+ the PandaError code for 409)
        var error = (int)status switch
        {
            400 => RelayHostError.Failed,
            408 => RelayHostError.Timeout,
            422 => RelayHostError.Mismatch,
            409 => errorCode switch
            {
                "AUTH_FAILED" => RelayHostError.Rejected,
                "PERMISSION_DENIED" => RelayHostError.Conflict,
                "BROWSER_NOT_FOUND" => RelayHostError.NotHosted,
                _ => RelayHostError.Failed,
            },
            _ => RelayHostError.Failed,
        };
        LogRefused(relayId, action, (int)status);
        return Failed(seq, error);
    }

    /// <summary>The <c>code</c> of a PandaError body, or null.</summary>
    private static string? ErrorCode(string json)
    {
        try
        {
            return JsonNode.Parse(json) is JsonObject o && o["code"] is JsonValue v && v.TryGetValue<string>(out var code) ? code : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hosted bridge unreachable for {Action} of relay {RelayId} ({ExceptionType})")]
    private partial void LogUnreachable(string relayId, string action, string exceptionType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Hosted bridge answered {Status} to {Action} of relay {RelayId}")]
    private partial void LogRefused(string relayId, string action, int status);
}
