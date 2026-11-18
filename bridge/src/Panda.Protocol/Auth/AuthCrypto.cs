using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Panda.Bridge.Auth;

/// <summary>
/// Auth primitives of spec §58 / §71.3 (seam S2). Every output is pinned by schema/auth-vectors.json.
/// Proof = lowercase hex HMAC-SHA256(key, frame(label, fields…)); frame = concat(len32be(f) ‖ f).
/// </summary>
public static class AuthCrypto
{
    public const int SecretLength = 32;
    public const int NonceLength = 16;
    public const int PairCodeLength = 26;
    private const int ProofHexLength = 64;

    // Labels are T3's own constants, pinned by the vectors (plan T3, S2) — not taken from generated protocol types.
    private static readonly byte[] BridgeLabel = "bridge"u8.ToArray();
    private static readonly byte[] ExtLabel = "ext"u8.ToArray();
    private static readonly byte[] PairBridgeLabel = "pair-bridge"u8.ToArray();
    private static readonly byte[] PairExtLabel = "pair-ext"u8.ToArray();

    public static byte[] Frame(params ReadOnlySpan<byte[]> fields)
    {
        var length = 0;
        foreach (var f in fields)
        {
            length = checked(length + 4 + f.Length);
        }

        var frame = new byte[length];
        var offset = 0;
        foreach (var f in fields)
        {
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(offset), (uint)f.Length);
            f.CopyTo(frame, offset + 4);
            offset += 4 + f.Length;
        }

        return frame;
    }

    /// <summary>Uppercase, strip spaces and '-'; valid only if exactly 26 chars of [A-Z2-7] remain (§71.3).</summary>
    public static bool TryNormalizePairCode(string input, out string? normalized)
    {
        ArgumentNullException.ThrowIfNull(input);
        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (c is ' ' or '-')
            {
                continue;
            }

            var u = c is >= 'a' and <= 'z' ? (char)(c - 32) : c;
            if (u is not ((>= 'A' and <= 'Z') or (>= '2' and <= '7')))
            {
                normalized = null;
                return false;
            }

            sb.Append(u);
        }

        normalized = sb.Length == PairCodeLength ? sb.ToString() : null;
        return normalized is not null;
    }

    /// <summary>Strict base64url: no padding, canonical, exactly <paramref name="length"/> bytes.</summary>
    public static bool TryDecodeBase64Url(string text, int length, out byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(text);
        bytes = [];
        if (text.Length != Base64Url.GetEncodedLength(length) || !Base64Url.IsValid(text.AsSpan(), out var decodedLength)
            || decodedLength != length)
        {
            return false;
        }

        var decoded = Base64Url.DecodeFromChars(text.AsSpan());
        // Reject non-canonical encodings (non-zero unused bits in the last character).
        if (!string.Equals(Base64Url.EncodeToString(decoded), text, StringComparison.Ordinal))
        {
            return false;
        }

        bytes = decoded;
        return true;
    }

    public static string EncodeBase64Url(ReadOnlySpan<byte> bytes) => Base64Url.EncodeToString(bytes);

    /// <summary>RFC 4648 base32, uppercase, no padding (§71.3; pinned by the shared base32 vectors).</summary>
    public static string EncodeBase32(ReadOnlySpan<byte> bytes)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new StringBuilder(((bytes.Length * 8) + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }

            buffer &= (1 << bits) - 1;
        }

        if (bits > 0)
        {
            sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return sb.ToString();
    }

    public static string BridgeProof(
        ReadOnlySpan<byte> secret, ReadOnlySpan<byte> extNonce, ReadOnlySpan<byte> bridgeNonce, string browserInstanceId)
    {
        RequireLength(secret, SecretLength, nameof(secret));
        return Hmac(secret, BridgeLabel, Nonce(extNonce), Nonce(bridgeNonce), Encoding.UTF8.GetBytes(browserInstanceId));
    }

    public static string ExtProof(
        ReadOnlySpan<byte> secret, ReadOnlySpan<byte> extNonce, ReadOnlySpan<byte> bridgeNonce, string browserInstanceId)
    {
        RequireLength(secret, SecretLength, nameof(secret));
        return Hmac(secret, ExtLabel, Nonce(bridgeNonce), Nonce(extNonce), Encoding.UTF8.GetBytes(browserInstanceId));
    }

    public static string PairBridgeProof(string code, ReadOnlySpan<byte> extNonce, ReadOnlySpan<byte> bridgeNonce) =>
        Hmac(CodeKey(code), PairBridgeLabel, Nonce(extNonce), Nonce(bridgeNonce));

    public static string PairExtProof(string code, ReadOnlySpan<byte> extNonce, ReadOnlySpan<byte> bridgeNonce) =>
        Hmac(CodeKey(code), PairExtLabel, Nonce(bridgeNonce), Nonce(extNonce));

    /// <summary>Constant-time comparison of two proofs. Never throws; anything that is not 64 lowercase hex is false.</summary>
    public static bool Verify(string expectedProof, string? receivedProof)
    {
        ArgumentNullException.ThrowIfNull(expectedProof);
        if (!IsLowerHex64(expectedProof))
        {
            throw new ArgumentException("expected proof must be 64 lowercase hex chars", nameof(expectedProof));
        }

        if (receivedProof is null || !IsLowerHex64(receivedProof))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(expectedProof), Convert.FromHexString(receivedProof));
    }

    /// <summary>First 4 bytes of SHA-256(UTF-8 bridgeId), uppercase hex, as XXXX-XXXX (§58.1 step 8, §71.3).</summary>
    public static string Fingerprint(string bridgeId)
    {
        ArgumentNullException.ThrowIfNull(bridgeId);
        var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bridgeId)), 0, 4);
        return $"{hex[..4]}-{hex[4..]}";
    }

    private static string Hmac(ReadOnlySpan<byte> key, params ReadOnlySpan<byte[]> fields) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(key, Frame(fields)));

    private static byte[] CodeKey(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (!TryNormalizePairCode(code, out var normalized) || normalized != code)
        {
            throw new ArgumentException("pair code must be a normalized 26-char base32 code", nameof(code));
        }

        return Encoding.ASCII.GetBytes(normalized);
    }

    private static byte[] Nonce(ReadOnlySpan<byte> nonce)
    {
        RequireLength(nonce, NonceLength, nameof(nonce));
        return nonce.ToArray();
    }

    private static void RequireLength(ReadOnlySpan<byte> value, int length, string name)
    {
        if (value.Length != length)
        {
            throw new ArgumentException($"{name} must be {length} bytes", name);
        }
    }

    private static bool IsLowerHex64(string s)
    {
        if (s.Length != ProofHexLength)
        {
            return false;
        }

        foreach (var c in s)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }
}
