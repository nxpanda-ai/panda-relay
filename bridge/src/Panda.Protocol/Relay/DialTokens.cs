using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Panda.Bridge.Auth;

namespace Panda.Bridge.Relay;

/// <summary>
/// relayId (plan-relay S1) and dial tokens (S7): linkToken = HMAC-SHA256(pairing secret, "panda-relay-dial-link-v1" ‖
/// relayId), pairToken = HMAC-SHA256(normalised code ASCII, "panda-relay-dial-pair-v1" ‖ relayId), both base64url; the
/// relay stores base64url(SHA-256(token bytes)). Pinned by schema/relay-vectors.json. Tokens are never logged (§37).
/// </summary>
public static class DialTokens
{
    private const int RelayIdLength = 22;
    private const int TokenLength = 32;

    public static string RelayIdFromSpki(ReadOnlySpan<byte> spki) =>
        AuthCrypto.EncodeBase64Url(SHA256.HashData(spki))[..RelayIdLength];

    public static string LinkToken(ReadOnlySpan<byte> secret, string relayId)
    {
        if (secret.Length != AuthCrypto.SecretLength)
        {
            throw new ArgumentException("link token needs the 32-byte pairing secret", nameof(secret));
        }

        return Token(secret, "panda-relay-dial-link-v1", relayId);
    }

    public static string PairToken(string code, string relayId)
    {
        if (!AuthCrypto.TryNormalizePairCode(code, out var normalized))
        {
            throw new ArgumentException("pair token needs a valid pair code", nameof(code));
        }

        return Token(Encoding.ASCII.GetBytes(normalized!), "panda-relay-dial-pair-v1", relayId);
    }

    /// <summary>The relay's stored form of a dial token; false for anything that is not 32 bytes of base64url.</summary>
    public static bool TryHash(string token, [NotNullWhen(true)] out string? hash)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (!AuthCrypto.TryDecodeBase64Url(token, TokenLength, out var raw))
        {
            hash = null;
            return false;
        }

        hash = AuthCrypto.EncodeBase64Url(SHA256.HashData(raw));
        return true;
    }

    private static string Token(ReadOnlySpan<byte> key, string label, string relayId)
    {
        ArgumentNullException.ThrowIfNull(relayId);
        if (relayId.Length != RelayIdLength || !relayId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            throw new ArgumentException("relayId must be 22 base64url characters", nameof(relayId));
        }

        return AuthCrypto.EncodeBase64Url(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(label + relayId)));
    }
}
