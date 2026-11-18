namespace Panda.Bridge.Relay;

/// <summary>
/// plan-protocol-project: the relay address rules <see cref="ConnectionCode"/> parses with, moved verbatim out of
/// <c>RelayTargets</c> (the bridge's relay-targets.json store, which keeps these three names as forwarders) so the
/// parser can live in Panda.Protocol without the store.
/// </summary>
internal static class RelayAddress
{
    /// <summary>The <c>relay:&lt;relayId&gt;</c> extensionId prefix (and pair-code scope) of a relay's Chrome.</summary>
    public const string ExtensionIdPrefix = "relay:";

    public static bool IsRelayId(string? value) =>
        value is { Length: 22 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>The <c>ws://</c> / <c>wss://</c> origin of <paramref name="url"/> (no path, query, fragment or user), else null.</summary>
    public static string? NormalizeUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss")
            || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }
}
