using System.Security.Cryptography;
using System.Text;
using Panda.Bridge.Auth;
using Panda.Bridge.Protocol;

namespace Panda.Relay;

/// <summary>
/// S1 registration proof: ECDSA P-256 / SHA-256, IEEE P1363 r‖s, over
/// "panda-relay-register-v1" ‖ 0x00 ‖ nonce (32 B) ‖ host, where host is the relay's CONFIGURED public host — a
/// signature made for another relay (or replayed from another connection's nonce) does not verify.
/// </summary>
internal static class RegistrationProof
{
    private const int SpkiLength = 91; // DER SubjectPublicKeyInfo of an uncompressed P-256 key
    private const int SignatureLength = 64;
    private const string P256Oid = "1.2.840.10045.3.1.7";
    private static readonly byte[] Label = Encoding.ASCII.GetBytes("panda-relay-register-v1");

    public static byte[] SignedBytes(ReadOnlySpan<byte> nonce, string host) =>
        [.. Label, 0, .. nonce, .. Encoding.ASCII.GetBytes(host)];

    /// <summary>The raw SPKI when the proof holds, else null (never throws for the peer's bytes).</summary>
    public static byte[]? Verify(RelayRegister register, ReadOnlySpan<byte> nonce, string host)
    {
        if (!AuthCrypto.TryDecodeBase64Url(register.Spki, SpkiLength, out var spki)
            || !AuthCrypto.TryDecodeBase64Url(register.Sig, SignatureLength, out var sig))
        {
            return null;
        }

        using var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(spki, out var read);
            if (read != spki.Length || key.KeySize != 256 || !IsP256(key.ExportParameters(includePrivateParameters: false).Curve))
            {
                return null;
            }

            return key.VerifyData(SignedBytes(nonce, host), sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
                ? spki
                : null;
        }
        catch (Exception e) when (e is CryptographicException or PlatformNotSupportedException)
        {
            // Not a key this platform can import: bad DER or point (CryptographicException), or a named curve the
            // platform does not know (Windows CNG throws PlatformNotSupportedException for that).
            return null;
        }
    }

    private static bool IsP256(ECCurve curve) =>
        curve.IsNamed && (curve.Oid.Value == P256Oid || curve.Oid.FriendlyName is "nistP256" or "ECDSA_P256");
}
