package auth

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/x509"
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
)

// Known answers shared with C# and TS (AC5): schema/auth-vectors.json (base64url), schema/relay-vectors.json
// (relayId, dial tokens). The ECDSA proof has no vector: generated keys here, the suite's 4503 cases on the wire.
func vectors(t *testing.T, name string, v any) {
	t.Helper()
	b, err := os.ReadFile(filepath.Join("..", "..", "..", "schema", name))
	if err != nil {
		t.Fatal(err)
	}
	if err := json.Unmarshal(b, v); err != nil {
		t.Fatal(err)
	}
}

func unhex(t *testing.T, s string) []byte {
	t.Helper()
	b, err := hex.DecodeString(s)
	if err != nil {
		t.Fatal(err)
	}
	return b
}

func TestBase64URLVectors(t *testing.T) {
	var v struct {
		Base64url []struct {
			Text     string
			Length   int
			BytesHex *string
		}
	}
	vectors(t, "auth-vectors.json", &v)
	if len(v.Base64url) == 0 {
		t.Fatal("no rows")
	}
	for _, r := range v.Base64url {
		b, ok := DecodeBase64URL(r.Text, r.Length)
		switch {
		case r.BytesHex == nil && ok:
			t.Errorf("%q/%d accepted", r.Text, r.Length)
		case r.BytesHex != nil && (!ok || hex.EncodeToString(b) != *r.BytesHex):
			t.Errorf("%q/%d: %x %v", r.Text, r.Length, b, ok)
		case r.BytesHex != nil && EncodeBase64URL(b) != r.Text:
			t.Errorf("encode %x != %q", b, r.Text)
		}
	}
}

func TestRelayIDVectors(t *testing.T) {
	var v struct {
		RelayID []struct {
			SpkiHex string
			RelayID string `json:"relayId"`
		} `json:"relayId"`
	}
	vectors(t, "relay-vectors.json", &v)
	if len(v.RelayID) == 0 {
		t.Fatal("no rows")
	}
	for _, r := range v.RelayID {
		if got := RelayIDFromSPKI(unhex(t, r.SpkiHex)); got != r.RelayID {
			t.Errorf("got %s want %s", got, r.RelayID)
		}
	}
}

func TestDialTokenHashVectors(t *testing.T) {
	var v struct {
		DialTokens []struct {
			Token     string
			TokenHash string
		}
	}
	vectors(t, "relay-vectors.json", &v)
	if len(v.DialTokens) == 0 {
		t.Fatal("no rows")
	}
	for _, r := range v.DialTokens {
		h, ok := TryHash(r.Token)
		if !ok || h != r.TokenHash {
			t.Errorf("TryHash(%s) = %s %v, want %s", r.Token, h, ok, r.TokenHash)
		}
		registered := r.TokenHash
		token := r.Token
		if !TokenMatches(&registered, &token) {
			t.Error("the registered hash does not match its token")
		}
		other := r.TokenHash[:len(r.TokenHash)-1] + "A"
		if other == r.TokenHash {
			other = r.TokenHash[:len(r.TokenHash)-1] + "B"
		}
		for name, c := range map[string][2]*string{
			"no registered hash": {nil, &token},
			"no token":           {&registered, nil},
			"another hash":       {&other, &token},
			"token not base64":   {&registered, ptr("not a token")},
			"token 31 bytes":     {&registered, ptr(token[:len(token)-2])},
		} {
			if TokenMatches(c[0], c[1]) {
				t.Errorf("%s: matched", name)
			}
		}
	}
}

func ptr(s string) *string { return &s }

// A registration signed as the extension signs it (RegistrationProof.SignedBytes; extension relaySession).
func sign(t *testing.T, key *ecdsa.PrivateKey, nonce []byte, host string) (spki, sig string) {
	t.Helper()
	der, err := x509.MarshalPKIXPublicKey(&key.PublicKey)
	if err != nil {
		t.Fatal(err)
	}
	digest := sha256.Sum256(SignedBytes(nonce, host))
	r, s, err := ecdsa.Sign(rand.Reader, key, digest[:])
	if err != nil {
		t.Fatal(err)
	}
	raw := make([]byte, 64)
	r.FillBytes(raw[:32])
	s.FillBytes(raw[32:])
	return EncodeBase64URL(der), EncodeBase64URL(raw)
}

func TestVerifyRegistration(t *testing.T) {
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	nonce := make([]byte, 32)
	_, _ = rand.Read(nonce)
	const host = "r0123abcd.relay.test:443"
	spki, sig := sign(t, key, nonce, host)

	got, ok := VerifyRegistration(spki, sig, nonce, host)
	if !ok {
		t.Fatal("a good registration was refused")
	}
	if EncodeBase64URL(got) != spki {
		t.Fatal("the returned SPKI is not the registered one")
	}

	otherNonce := append([]byte{}, nonce...)
	otherNonce[0] ^= 1
	flipped := []byte(sig)
	if flipped[10] == 'A' {
		flipped[10] = 'B'
	} else {
		flipped[10] = 'A'
	}
	p384, _ := ecdsa.GenerateKey(elliptic.P384(), rand.Reader)
	der384, _ := x509.MarshalPKIXPublicKey(&p384.PublicKey)
	otherKey, _ := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	otherSpki, _ := sign(t, otherKey, nonce, host)
	zero := EncodeBase64URL(make([]byte, 64))
	for name, c := range map[string]struct {
		spki, sig, host string
		nonce           []byte
	}{
		"another relay's host":     {spki, sig, "r0123abce.relay.test:443", nonce},
		"another nonce":            {spki, sig, host, otherNonce},
		"signature changed":        {spki, string(flipped), host, nonce},
		"another key":              {otherSpki, sig, host, nonce},
		"P-384 (its SPKI length)":  {EncodeBase64URL(der384), sig, host, nonce},
		"r and s zero":             {spki, zero, host, nonce},
		"spki not canonical":       {spki[:len(spki)-1] + "R", sig, host, nonce},
		"spki one byte short":      {EncodeBase64URL(mustDecode(t, spki)[:90]), sig, host, nonce},
		"sig DER-encoded":          {spki, EncodeBase64URL(make([]byte, 70)), host, nonce},
		"spki bytes not a SPKI":    {EncodeBase64URL(make([]byte, 91)), sig, host, nonce},
		"sig empty":                {spki, "", host, nonce},
		"spki standard base64 '+'": {"+" + spki[1:], sig, host, nonce},
	} {
		if _, ok := VerifyRegistration(c.spki, c.sig, c.nonce, c.host); ok {
			t.Errorf("%s: accepted", name)
		}
	}
}

func mustDecode(t *testing.T, s string) []byte {
	t.Helper()
	b, ok := DecodeBase64URL(s, 91)
	if !ok {
		t.Fatal("decode")
	}
	return b
}

func TestSignedBytesLayout(t *testing.T) {
	nonce := make([]byte, 32)
	nonce[31] = 7
	got := SignedBytes(nonce, "h:1")
	want := append(append(append([]byte("panda-relay-register-v1"), 0), nonce...), "h:1"...)
	if string(got) != string(want) {
		t.Fatalf("%x", got)
	}
}
