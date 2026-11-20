package auth

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/sha256"
	"crypto/subtle"
	"crypto/x509"
	"math/big"
)

const (
	spkiLength      = 91 // DER SubjectPublicKeyInfo of an uncompressed P-256 key (RegistrationProof.cs:15)
	signatureLength = 64 // IEEE P1363 r‖s
	relayIDLength   = 22 // DialTokens.cs:15
	tokenLength     = 32 // DialTokens.cs:16
)

var label = []byte("panda-relay-register-v1")

// SignedBytes is RegistrationProof.SignedBytes: "panda-relay-register-v1" ‖ 0x00 ‖ nonce ‖ host (ASCII).
func SignedBytes(nonce []byte, host string) []byte {
	b := make([]byte, 0, len(label)+1+len(nonce)+len(host))
	b = append(b, label...)
	b = append(b, 0)
	b = append(b, nonce...)
	return append(b, host...)
}

// VerifyRegistration is RegistrationProof.Verify (RegistrationProof.cs:24-51): ECDSA P-256 / SHA-256 over SignedBytes,
// with the relay's CONFIGURED public host, so a proof made for another relay or another connection's nonce fails.
// The raw SPKI when the proof holds; ok=false for any other input (never panics on the peer's bytes).
func VerifyRegistration(spkiText, sigText string, nonce []byte, host string) (spki []byte, ok bool) {
	spki, ok = DecodeBase64URL(spkiText, spkiLength)
	if !ok {
		return nil, false
	}
	sig, ok := DecodeBase64URL(sigText, signatureLength)
	if !ok {
		return nil, false
	}
	// trailing bytes after the DER are an error here (C#: read != spki.Length)
	parsed, err := x509.ParsePKIXPublicKey(spki)
	if err != nil {
		return nil, false
	}
	key, isEC := parsed.(*ecdsa.PublicKey)
	if !isEC || key.Curve != elliptic.P256() {
		return nil, false
	}
	digest := sha256.Sum256(SignedBytes(nonce, host))
	r := new(big.Int).SetBytes(sig[:32])
	s := new(big.Int).SetBytes(sig[32:])
	if !ecdsa.Verify(key, digest[:], r, s) {
		return nil, false
	}
	return spki, true
}

// RelayIDFromSPKI is DialTokens.RelayIdFromSpki: the first 22 characters of base64url(SHA-256(spki)).
func RelayIDFromSPKI(spki []byte) string {
	h := sha256.Sum256(spki)
	return EncodeBase64URL(h[:])[:relayIDLength]
}

// IsRelayID: 22 base64url characters (RelayHub.IsRelayId, HelpPage.IsRelayId).
func IsRelayID(v string) bool {
	if len(v) != relayIDLength {
		return false
	}
	for i := 0; i < len(v); i++ {
		c := v[i]
		if !(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-' || c == '_') {
			return false
		}
	}
	return true
}

// TryHash is DialTokens.TryHash: the relay's stored form of a dial token, base64url(SHA-256(token bytes)); false for
// anything that is not 32 bytes of canonical base64url. The relay keeps only these hashes (S3 invariant).
func TryHash(token string) (string, bool) {
	raw, ok := DecodeBase64URL(token, tokenLength)
	if !ok {
		return "", false
	}
	h := sha256.Sum256(raw)
	return EncodeBase64URL(h[:]), true
}

// TokenMatches is RelayHub.TokenMatches (RelayHub.cs:856-865): the token's hash against the registered one, compared
// in constant time (different lengths: false, as FixedTimeEquals).
func TokenMatches(registered, token *string) bool {
	if registered == nil || token == nil {
		return false
	}
	hash, ok := TryHash(*token)
	if !ok {
		return false
	}
	return subtle.ConstantTimeCompare([]byte(hash), []byte(*registered)) == 1
}
