using System.Net;
using System.Text.RegularExpressions;

namespace Panda.Relay;

/// <summary>
/// The relay's configuration (plan-relay P4). Quota numbers are placeholders until plan 7c measures the cost; every
/// timeout and cap below sits above what the protocol needs (S1: records ≤ 256 KiB + overhead, extension ping 20 s).
/// </summary>
public sealed partial class RelayOptions
{
    /// <summary>The lowercase <c>host[:port]</c> extensions dial; RELAY_REGISTER signs it (S1), so it must match exactly.</summary>
    public required string PublicHost { get; set; }

    public IPAddress ListenAddress { get; set; } = IPAddress.Loopback;

    /// <summary>0 = any free port (tests).</summary>
    public int Port { get; set; }

    /// <summary>
    /// plan-relay-deploy C5 (D5): a second listener on 127.0.0.1 for the hosted bridge beside the relay (its
    /// <c>--hosted-dial-url</c>). It serves only <c>/v1/stream?dial=</c>, is exempt from the per-IP limits and never
    /// reads a forwarded header; the TLS proxy must never forward to it. Null = none; 0 = any free port (tests).
    /// </summary>
    public int? InternalPort { get; set; }

    /// <summary>S1: a frame over 512 KiB closes 4513 (S2 records are ≤ 256 KiB + 25 bytes).</summary>
    public int MaxFrameBytes { get; set; } = 512 * 1024;

    /// <summary>S1: the first extension message within 10 s is RELAY_REGISTER.</summary>
    public TimeSpan RegisterTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The extension sends RELAY_PING every 20 s (decisions.md, P2); three missed pings close the socket.</summary>
    public TimeSpan ControlIdleTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>S1: a streamId issued ≤ 15 s ago.</summary>
    public TimeSpan AcceptTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>A peer that stops reading: one frame (≤ 512 KiB) must leave within this, else that socket is aborted.</summary>
    public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the relay waits for the peer's close frame after sending its own, then aborts.</summary>
    public TimeSpan CloseTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>WebSocket PING/PONG keepalive on every socket, so a dead TCP peer ends its stream (.NET 9+).</summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(20);

    public TimeSpan KeepAliveTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>S6: bytes piped per relayId per UTC day, both directions.</summary>
    public long BytesPerDay { get; set; } = 1L << 30;

    /// <summary>S6: streams opened (STREAM_OPEN) per relayId per UTC day.</summary>
    public int StreamsPerDay { get; set; } = 2000;

    /// <summary>S1 4530: /v1/browser connections per IP per clock hour (generous: CGNAT / offices share an IP).</summary>
    public int RegistrationsPerIpPerHour { get; set; } = 600;

    /// <summary>S1 4530: dials refused for their token (4501) per IP per clock hour.</summary>
    public int RefusedDialsPerIpPerHour { get; set; } = 600;

    /// <summary>
    /// plan-relay-deploy C2 (seam S9): the TLS proxies whose forwarded client IP is believed. Empty = none: every
    /// limit uses the TCP peer. Only these are trusted (the middleware's loopback defaults are cleared).
    /// </summary>
    public IList<IPNetwork> TrustedProxies { get; } = [];

    /// <summary>The header a trusted proxy puts the client IP in (<c>X-Forwarded-For</c>; <c>CF-Connecting-IP</c> for cloudflared).</summary>
    public string ClientIpHeader { get; set; } = "X-Forwarded-For";

    /// <summary>
    /// plan-relay-deploy C6 (D5): /v1/stream upgrades not admitted per IP per clock hour — a 4504 once per (IP, relayId),
    /// an unknown / late / wrong-owner accept and a malformed request each time. Over it: 4530 on that IP's dials and
    /// unknown accepts. Never counts or blocks /v1/browser.
    /// </summary>
    public int NoisePerIpPerHour { get; set; } = 600;

    /// <summary>(IP, relayId) pairs remembered per hour for the 4504 dedup; past it every 4504 counts (memory bound).</summary>
    public int MaxTrackedNoise { get; set; } = 200_000;

    /// <summary>Distinct IP buckets tracked per hour; past it, new buckets share one overflow bucket (memory bound).</summary>
    public int MaxTrackedIps { get; set; } = 100_000;

    /// <summary>
    /// plan-relay-prefix: IPv6 /64 buckets one /48 may own per hour; its further /64s share one bucket of that /48, so a
    /// client holding a /48 can fill neither the table nor the overflow bucket. IPv4 is not affected.
    /// </summary>
    public int MaxBucketsPerPrefix { get; set; } = 256;

    /// <summary>
    /// URL-only mode (plan-relay-url D1, U3): the hosted bridge's admin origin, <c>http://127.0.0.1:&lt;port&gt;</c> (its
    /// admin port is loopback only). Null = no hosted bridge: RELAY_HOST / ROTATE / UNHOST answer <c>unavailable</c>.
    /// </summary>
    public Uri? HostedAdminUrl { get; set; }

    /// <summary>The hosted bridge's <c>hosted-admin.token</c> (opens <c>/admin/hosted/*</c> only). A secret: never printed.</summary>
    public string? HostedAdminToken { get; set; }

    /// <summary>The MCP URL a hosted Chrome is told (<c>https://&lt;public host&gt;/…</c>, the only form extensions accept, D4).</summary>
    public string? HostedMcpUrl { get; set; }

    /// <summary>
    /// plan-relay-deploy C3 (D3): RELAY_HOST / ROTATE / UNHOST calls in flight to the hosted bridge across ALL control
    /// sockets (one per socket already); the next one is answered <c>busy</c>. Each holds a hosted add up to
    /// <see cref="HostedTimeout"/>.
    /// </summary>
    public int MaxHosting { get; set; } = 32;

    /// <summary>
    /// How long one call to the hosted bridge may take: an add long-polls up to the pair code's 5 min; this stays under
    /// the extension's 6 min wait, so it hears <c>failed</c> rather than nothing.
    /// </summary>
    public TimeSpan HostedTimeout { get; set; } = TimeSpan.FromSeconds(330);

    [GeneratedRegex("^[a-z0-9]([a-z0-9.-]{0,251}[a-z0-9])?(:[0-9]{1,5})?$", RegexOptions.CultureInvariant)]
    private static partial Regex HostShape();

    [GeneratedRegex("^[!-~]{1,300}$", RegexOptions.CultureInvariant)]
    private static partial Regex McpUrlShape();

    /// <summary>An HTTP header name (RFC 9110 token), at most 64 characters.</summary>
    [GeneratedRegex("^[!#$%&'*+.^_`|~0-9A-Za-z-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderName();

    private static bool IsLoopback(string host) =>
        host == "localhost" || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));

    /// <summary>The three hosted settings: all or none, each well formed (plan-relay-url U3).</summary>
    private void ValidateHosted()
    {
        if (HostedAdminUrl is null && HostedAdminToken is null && HostedMcpUrl is null)
        {
            return;
        }

        if (HostedAdminUrl is not { IsAbsoluteUri: true, Scheme: "http", AbsolutePath: "/", Query: "", Fragment: "", UserInfo: "" }
            || HostedAdminUrl.Host is not ("127.0.0.1" or "localhost")) // the hosted bridge listens on 127.0.0.1 only
        {
            throw new ArgumentException("HostedAdminUrl must be the hosted bridge's http://127.0.0.1:<port> admin origin", nameof(HostedAdminUrl));
        }

        if (HostedAdminToken is null || !Panda.Bridge.Auth.AuthCrypto.TryDecodeBase64Url(HostedAdminToken, 32, out _))
        {
            throw new ArgumentException("HostedAdminToken must be the hosted bridge's hosted-admin.token", nameof(HostedAdminToken));
        }

        var publicHost = PublicHost.Split(':')[0];
        if (HostedMcpUrl is null || !McpUrlShape().IsMatch(HostedMcpUrl)
            || !Uri.TryCreate(HostedMcpUrl, UriKind.Absolute, out var mcp) || mcp.Host != publicHost || mcp.UserInfo.Length != 0
            || !(mcp.Scheme == "https" || (mcp.Scheme == "http" && IsLoopback(mcp.Host))))
        {
            throw new ArgumentException("HostedMcpUrl must be https://<public host>/… (http only on loopback)", nameof(HostedMcpUrl));
        }

        if (HostedTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(HostedTimeout), "timeouts must be positive");
        }
    }

    private static bool PortInRange(string host)
    {
        var colon = host.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 || int.Parse(host.AsSpan(colon + 1), System.Globalization.CultureInfo.InvariantCulture) is > 0 and <= 65535;
    }

    /// <summary>Throws <see cref="ArgumentException"/> for a configuration the relay cannot run with.</summary>
    public void Validate()
    {
        if (string.IsNullOrEmpty(PublicHost) || !HostShape().IsMatch(PublicHost) || !PortInRange(PublicHost))
        {
            throw new ArgumentException("PublicHost must be the lowercase host[:port] extensions dial", nameof(PublicHost));
        }

        if (ClientIpHeader is null || !HeaderName().IsMatch(ClientIpHeader))
        {
            throw new ArgumentException("ClientIpHeader must be an HTTP header name", nameof(ClientIpHeader));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(MaxFrameBytes, 1024, nameof(MaxFrameBytes));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxTrackedIps, 1, nameof(MaxTrackedIps));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBucketsPerPrefix, 1, nameof(MaxBucketsPerPrefix));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxHosting, 1, nameof(MaxHosting));
        if (InternalPort is { } internalPort && (internalPort is < 0 or > 65535 || (internalPort != 0 && internalPort == Port)))
        {
            throw new ArgumentException("InternalPort must be a free loopback port other than Port", nameof(InternalPort));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(BytesPerDay, nameof(BytesPerDay));
        ArgumentOutOfRangeException.ThrowIfNegative(StreamsPerDay, nameof(StreamsPerDay));
        ArgumentOutOfRangeException.ThrowIfNegative(RegistrationsPerIpPerHour, nameof(RegistrationsPerIpPerHour));
        ArgumentOutOfRangeException.ThrowIfNegative(RefusedDialsPerIpPerHour, nameof(RefusedDialsPerIpPerHour));
        ArgumentOutOfRangeException.ThrowIfNegative(NoisePerIpPerHour, nameof(NoisePerIpPerHour));
        ArgumentOutOfRangeException.ThrowIfNegative(MaxTrackedNoise, nameof(MaxTrackedNoise));
        ValidateHosted();
        ArgumentOutOfRangeException.ThrowIfLessThan(Port, 0, nameof(Port));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Port, 65535, nameof(Port));
        foreach (var (name, value) in new[]
        {
            (nameof(RegisterTimeout), RegisterTimeout), (nameof(ControlIdleTimeout), ControlIdleTimeout),
            (nameof(AcceptTimeout), AcceptTimeout), (nameof(SendTimeout), SendTimeout), (nameof(CloseTimeout), CloseTimeout),
            (nameof(KeepAliveInterval), KeepAliveInterval), (nameof(KeepAliveTimeout), KeepAliveTimeout),
        })
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(name, "timeouts must be positive");
            }
        }
    }
}
