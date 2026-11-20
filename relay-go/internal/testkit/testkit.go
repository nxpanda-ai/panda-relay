// Package testkit is a test Chrome for the relay's in-process tests (the suite README's obligations O1-O10): it
// registers on /v1/browser as the extension does — a P-256 key, the challenge signed (RegistrationProof.SignedBytes) —
// and reads every message on its own goroutine, so pings are answered while a test waits. Tests only.
package testkit

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/x509"
	"errors"
	"net/http"
	"strings"
	"testing"
	"time"

	"github.com/gorilla/websocket"

	"github.com/nxpanda-ai/panda-relay/relay-go/internal/auth"
	"github.com/nxpanda-ai/panda-relay/relay-go/internal/protocol"
)

// Origin is a well-formed extension Origin (chrome-extension:// + 32 of a-p).
const Origin = "chrome-extension://abcdefghijklmnopabcdefghijklmnop"

// Item is one thing the client read: a message, or the error that ended the reading (a close carries its code).
type Item struct {
	Msg protocol.RelayMessage
	Err error
}

// Client is a registered control socket.
type Client struct {
	WS      *websocket.Conn
	RelayID string
	In      chan Item
}

// WSURL turns an httptest URL (http://host:port) into ws://host:port + path.
func WSURL(base, path string) string { return "ws" + strings.TrimPrefix(base, "http") + path }

// Register opens a control socket at url and registers a fresh key for publicHost.
func Register(t *testing.T, url, publicHost string) *Client {
	t.Helper()
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	ws, _, err := websocket.DefaultDialer.Dial(url, http.Header{"Origin": {Origin}})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = ws.Close() })
	c := &Client{WS: ws, In: make(chan Item, 64)}
	go c.read()
	challenge, ok := c.Next(t, 5*time.Second).Msg.(protocol.RelayChallenge)
	if !ok {
		t.Fatal("no RELAY_CHALLENGE")
	}
	nonce, ok := auth.DecodeBase64URL(challenge.Nonce, 32)
	if !ok {
		t.Fatal("bad nonce")
	}
	der, err := x509.MarshalPKIXPublicKey(&key.PublicKey)
	if err != nil {
		t.Fatal(err)
	}
	digest := sha256.Sum256(auth.SignedBytes(nonce, publicHost))
	r, s, err := ecdsa.Sign(rand.Reader, key, digest[:])
	if err != nil {
		t.Fatal(err)
	}
	sig := make([]byte, 64)
	r.FillBytes(sig[:32])
	s.FillBytes(sig[32:])
	c.Send(t, protocol.RelayRegister{Spki: auth.EncodeBase64URL(der), Sig: auth.EncodeBase64URL(sig), ExtensionVersion: "0.1.15"})
	registered, ok := c.Next(t, 5*time.Second).Msg.(protocol.RelayRegistered)
	if !ok {
		t.Fatal("no RELAY_REGISTERED")
	}
	c.RelayID = registered.RelayId
	return c
}

func (c *Client) read() {
	for {
		_, b, err := c.WS.ReadMessage()
		if err != nil {
			c.In <- Item{Err: err}
			close(c.In)
			return
		}
		res := protocol.ParseRelay(b)
		if res.Kind != protocol.Ok {
			c.In <- Item{Err: errors.New("unparsable message from the relay")}
			continue
		}
		c.In <- Item{Msg: res.Message}
	}
}

// Send writes m.
func (c *Client) Send(t *testing.T, m protocol.RelayMessage) {
	t.Helper()
	b, err := protocol.MarshalRelay(m)
	if err != nil {
		t.Fatal(err)
	}
	if err := c.WS.WriteMessage(websocket.TextMessage, b); err != nil {
		t.Fatal(err)
	}
}

// Next is the next item within d.
func (c *Client) Next(t *testing.T, d time.Duration) Item {
	t.Helper()
	select {
	case it, ok := <-c.In:
		if !ok {
			t.Fatal("the socket already ended")
		}
		return it
	case <-time.After(d):
		t.Fatalf("nothing from the relay within %v", d)
		return Item{}
	}
}

// Nothing checks that nothing arrives within d.
func (c *Client) Nothing(t *testing.T, d time.Duration) {
	t.Helper()
	select {
	case it, ok := <-c.In:
		if ok {
			t.Fatalf("unexpected %+v", it)
		}
		t.Fatal("the socket ended")
	case <-time.After(d):
	}
}

// Closed reads until the socket ends and returns the relay's close code (1006 = no close frame).
func (c *Client) Closed(t *testing.T, d time.Duration) int {
	t.Helper()
	deadline := time.After(d)
	for {
		select {
		case it, ok := <-c.In:
			if !ok {
				t.Fatal("the socket already ended")
			}
			if it.Err != nil {
				return CloseCode(it.Err)
			}
		case <-deadline:
			t.Fatalf("not closed within %v", d)
		}
	}
}

// CloseCode is the code of a close error, 1006 for any other end.
func CloseCode(err error) int {
	var ce *websocket.CloseError
	if errors.As(err, &ce) {
		return ce.Code
	}
	return websocket.CloseAbnormalClosure
}
