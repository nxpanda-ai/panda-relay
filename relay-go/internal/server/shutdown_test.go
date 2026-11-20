package server

import (
	"errors"
	"net"
	"net/http"
	"testing"
	"time"

	"github.com/gorilla/websocket"

	"github.com/nxpanda-ai/panda-relay/relay-go/internal/clock"
	"github.com/nxpanda-ai/panda-relay/relay-go/internal/options"
	"github.com/nxpanda-ai/panda-relay/relay-go/internal/testkit"
)

// O6: on a graceful stop (SIGTERM / Ctrl+C → Shutdown) every control socket is closed 1001 before the relay ends, the
// listener stops accepting, and Shutdown returns once the handlers ended — well inside ShutdownTimeout.
func TestO6ShutdownClosesControlSockets1001(t *testing.T) {
	o := options.Default("relay.test")
	l, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	s := New(o, clock.Real{}, nil)
	served := make(chan error, 1)
	go func() { served <- s.Serve(l, nil) }()
	base := "http://" + l.Addr().String()
	a := testkit.Register(t, testkit.WSURL(base, "/v1/browser"), "relay.test")
	b := testkit.Register(t, testkit.WSURL(base, "/v1/browser"), "relay.test")

	start := time.Now()
	s.Shutdown(ShutdownTimeout)
	if took := time.Since(start); took > 2*time.Second {
		t.Fatalf("Shutdown took %v", took)
	}
	for _, c := range []*testkit.Client{a, b} {
		if code := c.Closed(t, 5*time.Second); code != websocket.CloseGoingAway {
			t.Fatalf("closed %d, want 1001", code)
		}
	}
	if err := <-served; !errors.Is(err, http.ErrServerClosed) {
		t.Fatalf("Serve returned %v", err)
	}
	if _, _, err := websocket.DefaultDialer.Dial(testkit.WSURL(base, "/v1/browser"), http.Header{"Origin": {testkit.Origin}}); err == nil {
		t.Fatal("still accepting after Shutdown")
	}
}
