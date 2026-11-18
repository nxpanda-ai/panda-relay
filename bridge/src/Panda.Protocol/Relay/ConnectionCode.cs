using System.Diagnostics.CodeAnalysis;
using Panda.Bridge.Auth;

namespace Panda.Bridge.Relay;

/// <summary>
/// plan-relay seam S4: the connection code a Chrome shows, <c>panda-relay://host[:port]/&lt;relayId&gt;#&lt;code&gt;</c>
/// (<c>wss://</c> implied; <c>panda-relay+ws://</c> only for a loopback host, for tests), or the same code as a link
/// <c>https://host[:port]/c/&lt;relayId&gt;#&lt;code&gt;</c> (<see cref="TryParseCodeOrLink"/>, plan-https-code). The code is a secret: never
/// log it or put it in a URL (only HMAC-derived dial tokens leave this daemon).
/// </summary>
internal sealed record ConnectionCode(string RelayUrl, string RelayId, string Code)
{
    private const string Scheme = "panda-relay";
    private const string LoopbackScheme = "panda-relay+ws";
    private const string LinkScheme = "https";
    private const string LoopbackLinkScheme = "http";
    private const string LinkPath = "/c/";

    public Uri Relay => new(RelayUrl);

    /// <summary>The <c>relay:&lt;relayId&gt;</c> extensionId (and pair-code scope) of this relay's Chrome.</summary>
    public string ExtensionId => RelayAddress.ExtensionIdPrefix + RelayId;

    /// <summary>The code form only — the hosting paths (RELAY_HOST, /admin/hosted/add; plan-https-code D7).</summary>
    public static bool TryParse(string? input, [NotNullWhen(true)] out ConnectionCode? code) =>
        TryParse(input, allowLink: false, out code);

    /// <summary>
    /// plan-https-code T1 (D7, seam S1): the code form, or the same code as a link
    /// <c>https://host[:port]/c/&lt;relayId&gt;#&lt;code&gt;</c> (<c>http://</c> only for a loopback host) — the record its
    /// <c>panda-relay[+ws]://</c> twin gives. For <c>panda-browser connect</c> and /admin/relay/connect only.
    /// </summary>
    public static bool TryParseCodeOrLink(string? input, [NotNullWhen(true)] out ConnectionCode? code) =>
        TryParse(input, allowLink: true, out code);

    private static bool TryParse(string? input, bool allowLink, [NotNullWhen(true)] out ConnectionCode? code)
    {
        code = null;
        if (!Uri.TryCreate(input?.Trim(), UriKind.Absolute, out var uri)
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0
            || !uri.Fragment.StartsWith('#') || !AuthCrypto.TryNormalizePairCode(uri.Fragment[1..], out var normalized))
        {
            return false;
        }

        // the scheme gives the relay's (wss or loopback-only ws) and where the relayId sits in the path
        var (secure, prefix) = uri.Scheme switch
        {
            Scheme => (true, "/"),
            LoopbackScheme => (false, "/"),
            LinkScheme when allowLink => (true, LinkPath),
            LoopbackLinkScheme when allowLink => (false, LinkPath),
            _ => (true, (string?)null),
        };
        if (prefix is null || (!secure && !uri.IsLoopback)
            || !uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)
            || !RelayAddress.IsRelayId(uri.AbsolutePath[prefix.Length..]))
        {
            return false;
        }

        var relayUrl = RelayAddress.NormalizeUrl($"{(secure ? "wss" : "ws")}://{uri.Authority}");
        if (relayUrl is null)
        {
            return false;
        }

        code = new ConnectionCode(relayUrl, uri.AbsolutePath[prefix.Length..], normalized!);
        return true;
    }

    /// <summary>Never prints the code.</summary>
    public override string ToString() => $"ConnectionCode {{ RelayUrl = {RelayUrl}, RelayId = {RelayId} }}";
}
