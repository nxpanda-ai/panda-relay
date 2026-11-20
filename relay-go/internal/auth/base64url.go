// Package auth ports the relay's use of Panda.Protocol's AuthCrypto, DialTokens and RegistrationProof (plan-relay-go S3).
package auth

import "encoding/base64"

// DecodeBase64URL is AuthCrypto.TryDecodeBase64Url: unpadded base64url of exactly n bytes, canonical only (the
// unused bits of the last character are zero — re-encoding gives the same text). Anything else: ok=false.
func DecodeBase64URL(text string, n int) (b []byte, ok bool) {
	if len(text) != base64.RawURLEncoding.EncodedLen(n) {
		return nil, false
	}
	b, err := base64.RawURLEncoding.Strict().DecodeString(text)
	if err != nil || len(b) != n || base64.RawURLEncoding.EncodeToString(b) != text {
		return nil, false
	}
	return b, true
}

// EncodeBase64URL is AuthCrypto.EncodeBase64Url: unpadded base64url.
func EncodeBase64URL(b []byte) string { return base64.RawURLEncoding.EncodeToString(b) }
