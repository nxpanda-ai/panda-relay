package socket

import (
	"errors"
	"net/http"
	"net/http/httptest"
	"runtime"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/gorilla/websocket"

	"github.com/nxpanda-ai/panda-relay/relay-go/internal/clock"
	"github.com/nxpanda-ai/panda-relay/relay-go/internal/options"
)

// pair serves one relay-side Socket to a test client; serve runs on the handler goroutine (as the hub's handlers do).
func pair(t *testing.T, serve func(*Socket)) *websocket.Conn {
	t.Helper()
	o := options.Default("relay.test")
	up := websocket.Upgrader{}
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		ws, err := up.Upgrade(w, r, nil)
		if err != nil {
			return
		}
		serve(New(ws, o, clock.Real{}))
	}))
	t.Cleanup(srv.Close)
	c, _, err := websocket.DefaultDialer.Dial("ws"+strings.TrimPrefix(srv.URL, "http"), nil)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = c.Close() })
	return c
}

// held is a relay-side Socket on a fake clock handed to the test (the handler goroutine waits until cleanup), and its
// client, which reads nothing unless the test does.
func held(t *testing.T) (*Socket, *websocket.Conn, *clock.Fake) {
	t.Helper()
	return heldWith(t, func(o *options.Options) { o.KeepAliveInterval = 24 * time.Hour })
}

func heldWith(t *testing.T, edit func(*options.Options)) (*Socket, *websocket.Conn, *clock.Fake) {
	t.Helper()
	o := options.Default("relay.test")
	edit(o)
	fc := clock.NewFake(time.Date(2026, 11, 20, 10, 0, 0, 0, time.UTC))
	got := make(chan *Socket, 1)
	end := make(chan struct{})
	up := websocket.Upgrader{}
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		ws, err := up.Upgrade(w, r, nil)
		if err != nil {
			return
		}
		got <- New(ws, o, fc)
		<-end
	}))
	c, _, err := websocket.DefaultDialer.Dial("ws"+strings.TrimPrefix(srv.URL, "http"), nil)
	if err != nil {
		t.Fatal(err)
	}
	s := <-got
	t.Cleanup(func() {
		s.Abort()
		_ = c.Close()
		close(end)
		srv.Close()
	})
	return s, c, fc
}

// stuck fills the path to a client that does not read until a send blocks; it returns the channel the blocked send's
// result arrives on.
func stuck(t *testing.T, s *Socket) <-chan bool {
	t.Helper()
	results := make(chan bool, 1)
	var done atomic.Int64 // sends completed (sendStart cannot tell sends apart: the fake time does not move)
	go func() {
		buf := make([]byte, 256*1024)
		for s.SendBinary(buf) {
			done.Add(1)
		}
		results <- false
	}()
	// a send in flight while no send completed for 300 ms is the stuck one (T6-T7 critic #3)
	last := int64(-1)
	for deadline := time.Now().Add(20 * time.Second); ; {
		time.Sleep(300 * time.Millisecond)
		n := done.Load()
		if n == last && s.sendStart.Load() != noSend {
			return results
		}
		last = n
		if time.Now().After(deadline) {
			t.Fatal("no send blocked within 20 s")
		}
	}
}

// O2: a peer that stops reading is cut off once a send has been stuck for SendTimeout (30 s), and not before.
func TestO2SendTimeout(t *testing.T) {
	s, _, fc := held(t)
	results := stuck(t, s)
	fc.Advance(s.o.SendTimeout - time.Millisecond)
	select {
	case <-results:
		t.Fatal("cut off before SendTimeout")
	case <-time.After(300 * time.Millisecond):
	}
	fc.Advance(time.Millisecond)
	select {
	case ok := <-results:
		if ok {
			t.Fatal("the stuck send succeeded")
		}
	case <-time.After(5 * time.Second):
		t.Fatal("not cut off at SendTimeout")
	}
}

// T5 critic round 2: Dispose's wait for a pending close frame is bounded while a send is stuck — by the close timeout
// armed with the request (or the send timer); on a fake clock it ends only when the time moves.
func TestDisposeWaitIsBoundedUnderAStuckSend(t *testing.T) {
	s, _, fc := held(t)
	stuck(t, s)
	s.CloseAsync(EndpointUnavailable, "")
	disposed := make(chan struct{})
	go func() {
		s.Dispose()
		close(disposed)
	}()
	select {
	case <-disposed:
		t.Fatal("Dispose did not wait for the pending close")
	case <-time.After(300 * time.Millisecond):
	}
	fc.Advance(s.o.CloseTimeout)
	select {
	case <-disposed:
	case <-time.After(5 * time.Second):
		t.Fatal("Dispose still waits after the close timeout")
	}
}

// T4 critic N1: a ping is answered within SendTimeout, not gorilla's default 1 s — a peer that reads nothing for 1.5 s
// (the pong queued behind a stuck data write) still gets its pong once it reads again.
func TestPongWaitsBehindAStuckWrite(t *testing.T) {
	s, c, _ := held(t)
	go func() {
		for f := s.Receive(); f.Kind != Lost && f.Kind != Close; f = s.Receive() {
		}
	}()
	stuck(t, s)
	if err := c.WriteControl(websocket.PingMessage, []byte("p1"), time.Now().Add(time.Second)); err != nil {
		t.Fatal(err)
	}
	time.Sleep(1500 * time.Millisecond)
	pong := make(chan string, 1)
	c.SetPongHandler(func(data string) error {
		select {
		case pong <- data:
		default:
		}
		return nil
	})
	go func() {
		for {
			if _, _, err := c.ReadMessage(); err != nil {
				return
			}
		}
	}()
	select {
	case data := <-pong:
		if data != "p1" {
			t.Fatalf("pong %q", data)
		}
	case <-time.After(10 * time.Second):
		t.Fatal("no pong after the peer read again")
	}
}

// T4 critic round 3: at most one pong is in flight — a ping flood while a pong waits adds no goroutines.
func TestPingFloodKeepsOnePongInFlight(t *testing.T) {
	s, c, _ := held(t)
	go func() {
		for f := s.Receive(); f.Kind != Lost && f.Kind != Close; f = s.Receive() {
		}
	}()
	stuck(t, s)
	if err := c.WriteControl(websocket.PingMessage, nil, time.Now().Add(time.Second)); err != nil {
		t.Fatal(err)
	}
	time.Sleep(200 * time.Millisecond)
	before := runtime.NumGoroutine()
	for i := 0; i < 500; i++ {
		if err := c.WriteControl(websocket.PingMessage, nil, time.Now().Add(time.Second)); err != nil {
			t.Fatal(err)
		}
	}
	time.Sleep(500 * time.Millisecond)
	if grown := runtime.NumGoroutine() - before; grown > 5 {
		t.Fatalf("%d goroutines more after 500 pings", grown)
	}
}

// receiving runs Receive until the socket ends; the channel gets the last frame's kind.
func receiving(s *Socket) <-chan Kind {
	end := make(chan Kind, 1)
	go func() {
		f := s.Receive()
		for ; f.Kind != Lost && f.Kind != Close; f = s.Receive() {
		}
		end <- f.Kind
	}()
	return end
}

func alive(t *testing.T, end <-chan Kind, why string) {
	t.Helper()
	select {
	case k := <-end:
		t.Fatalf("%s: the socket ended (%v)", why, k)
	case <-time.After(300 * time.Millisecond):
	}
}

func lost(t *testing.T, end <-chan Kind, why string) {
	t.Helper()
	select {
	case k := <-end:
		if k != Lost {
			t.Fatalf("%s: ended %v, want Lost (aborted)", why, k)
		}
	case <-time.After(5 * time.Second):
		t.Fatalf("%s: not aborted", why)
	}
}

// Close timeout (RelaySocket.cs:301-331): a peer that never answers the close frame is aborted CloseTimeout (5 s) after
// the request, not before.
func TestCloseTimeout(t *testing.T) {
	s, _, fc := held(t) // the client reads nothing, so it never answers
	end := receiving(s)
	s.CloseAsync(EndpointUnavailable, "")
	fc.Advance(s.o.CloseTimeout - time.Millisecond)
	alive(t, end, "before CloseTimeout")
	fc.Advance(time.Millisecond)
	lost(t, end, "at CloseTimeout")
}

// Keepalive (RelayOptions.cs:45-48): a ping every KeepAliveInterval; no pong within KeepAliveTimeout → abort. A peer
// that reads (and so answers pings) stays.
func TestKeepAlive(t *testing.T) {
	s, c, fc := heldWith(t, func(*options.Options) {})
	end := receiving(s)
	go func() { // the peer reads: gorilla answers each ping
		for {
			if _, _, err := c.ReadMessage(); err != nil {
				return
			}
		}
	}()
	// each Advance runs the previous ping's pong check (the interval equals the timeout) and sends the next ping, whose
	// pong is awaited before the time moves again
	for i := 0; i < 4; i++ {
		before := s.lastPong.Load()
		fc.Advance(s.o.KeepAliveInterval)
		for deadline := time.Now().Add(5 * time.Second); s.lastPong.Load() == before; {
			if time.Now().After(deadline) {
				t.Fatalf("round %d: no pong", i)
			}
			time.Sleep(5 * time.Millisecond)
		}
	}
	alive(t, end, "a peer that answers")

	s2, _, fc2 := heldWith(t, func(*options.Options) {}) // reads nothing: never answers
	end2 := receiving(s2)
	fc2.Advance(s2.o.KeepAliveInterval)
	fc2.Advance(s2.o.KeepAliveTimeout - time.Millisecond)
	alive(t, end2, "before KeepAliveTimeout")
	fc2.Advance(time.Millisecond)
	lost(t, end2, "no pong within KeepAliveTimeout")
}

// T5 critic F2: a CloseAsync followed at once by Dispose (a stream released by others while its handler ends) still
// puts the close frame on the wire — the peer reads the requested code, not a dropped connection (1006).
func TestDisposeWaitsForAPendingAsyncClose(t *testing.T) {
	for i := 0; i < 20; i++ {
		c := pair(t, func(s *Socket) {
			s.CloseAsync(EndpointUnavailable, "")
			s.Dispose()
		})
		_ = c.SetReadDeadline(time.Now().Add(5 * time.Second))
		_, _, err := c.ReadMessage()
		var ce *websocket.CloseError
		if !errors.As(err, &ce) || ce.Code != EndpointUnavailable {
			t.Fatalf("run %d: the peer read %v, want close 1001", i, err)
		}
	}
}
