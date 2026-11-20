package hub

import (
	"bytes"
	"net/http"
	"net/http/httptest"
	"net/netip"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/gorilla/websocket"

	"github.com/nxpanda-ai/panda-relay/relay-go/internal/clock"
	"github.com/nxpanda-ai/panda-relay/relay-go/internal/logx"
	"github.com/nxpanda-ai/panda-relay/relay-go/internal/options"
	"github.com/nxpanda-ai/panda-relay/relay-go/internal/protocol"
	"github.com/nxpanda-ai/panda-relay/relay-go/internal/testkit"
)

const publicHost = "relay.test"

// rig is a hub on a fake clock behind a test server (/v1/browser, /v1/stream), as RelayServer routes them.
type rig struct {
	h     *Hub
	clock *clock.Fake
	base  string
}

func newRig(t *testing.T, hosted Hosted, edit func(*options.Options)) *rig {
	t.Helper()
	o := options.Default(publicHost)
	o.KeepAliveInterval = 24 * time.Hour // the keepalive has its own tests (socket); here it would race the fake time
	if edit != nil {
		edit(o)
	}
	fc := clock.NewFake(time.Date(2026, 11, 20, 10, 0, 0, 0, time.UTC))
	r := &rig{h: New(o, fc, hosted), clock: fc}
	client := netip.MustParseAddr("192.0.2.7")
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, req *http.Request) {
		switch req.URL.Path {
		case "/v1/browser":
			r.h.RunControl(w, req, client)
		case "/v1/stream":
			r.h.RunStream(w, req, client, false)
		default:
			w.WriteHeader(http.StatusNotFound)
		}
	}))
	t.Cleanup(srv.Close)
	r.base = srv.URL
	return r
}

func (r *rig) control(t *testing.T) *testkit.Client {
	t.Helper()
	return testkit.Register(t, testkit.WSURL(r.base, "/v1/browser"), publicHost)
}

// settled: every handler ended and left no timer armed (plan-relay-go D2: every handler stops its timers).
func (r *rig) settled(t *testing.T) {
	t.Helper()
	if !r.h.Wait(time.Now().Add(5 * time.Second)) {
		t.Fatal("a handler still runs")
	}
	if n := r.clock.Armed(); n != 0 {
		t.Fatalf("%d timers left armed", n)
	}
}

type logBuffer struct {
	mu sync.Mutex
	b  bytes.Buffer
}

func (l *logBuffer) Write(p []byte) (int, error) {
	l.mu.Lock()
	defer l.mu.Unlock()
	return l.b.Write(p)
}

func (l *logBuffer) String() string {
	l.mu.Lock()
	defer l.mu.Unlock()
	return l.b.String()
}

// O1: a control socket with no message for 60 s is closed 1001; any message starts the 60 s again.
func TestO1IdleClose(t *testing.T) {
	r := newRig(t, nil, nil)
	c := r.control(t)
	r.clock.Advance(30 * time.Second)
	c.Send(t, protocol.RelayPing{})
	if _, ok := c.Next(t, 5*time.Second).Msg.(protocol.RelayPong); !ok {
		t.Fatal("no RELAY_PONG")
	}
	r.clock.Advance(59 * time.Second) // 89 s since registering, 59 s since the PING
	c.Nothing(t, 200*time.Millisecond)
	r.clock.Advance(time.Second)
	if code := c.Closed(t, 5*time.Second); code != websocket.CloseGoingAway {
		t.Fatalf("closed %d, want 1001", code)
	}
	r.settled(t)
}

// fakeHosted is a hosted bridge whose calls wait for release (one value per call).
type fakeHosted struct {
	configured bool
	panics     bool
	calls      atomic.Int32
	release    chan struct{}
}

func (f *fakeHosted) Configured() bool { return f.configured }

func (f *fakeHosted) Run(_ string, m protocol.RelayMessage) protocol.RelayHostResult {
	f.calls.Add(1)
	if f.panics {
		panic("a bug")
	}
	<-f.release
	return protocol.RelayHostResult{Seq: seqOf(m), Ok: true}
}

func (f *fakeHosted) waitCalls(t *testing.T, n int32) {
	t.Helper()
	for deadline := time.Now().Add(5 * time.Second); f.calls.Load() < n; {
		if time.Now().After(deadline) {
			t.Fatalf("%d hosted calls, want %d", f.calls.Load(), n)
		}
		time.Sleep(5 * time.Millisecond)
	}
}

const (
	code = "panda-relay://relay.test/AbCdEfGhIjKlMnOpQrSt_-#ABCDE-FGHIJ-KLMNO-PQRST-UVWXYZ"
	sha  = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
)

func hostResult(t *testing.T, c *testkit.Client) protocol.RelayHostResult {
	t.Helper()
	r, ok := c.Next(t, 5*time.Second).Msg.(protocol.RelayHostResult)
	if !ok {
		t.Fatal("no RELAY_HOST_RESULT")
	}
	return r
}

func errorOf(r protocol.RelayHostResult) string {
	if r.Error == nil {
		return ""
	}
	return string(*r.Error)
}

// O4: without a hosted bridge every hosting request is unavailable.
func TestO4Unavailable(t *testing.T) {
	for _, hosted := range []Hosted{nil, &fakeHosted{}} {
		r := newRig(t, hosted, nil)
		c := r.control(t)
		c.Send(t, protocol.RelayUnhost{Seq: 4})
		if res := hostResult(t, c); res.Ok || res.Seq != 4 || errorOf(res) != "unavailable" {
			t.Fatalf("%+v", res)
		}
	}
}

// O4: one request per socket (a second → busy at once, the control loop never waits); the relay-wide --max-hosting cap
// holds RELAY_HOST / ROTATE back (busy) but never UNHOST.
func TestO4OnePerSocketAndTheCap(t *testing.T) {
	f := &fakeHosted{configured: true, release: make(chan struct{})}
	r := newRig(t, f, func(o *options.Options) { o.MaxHosting = 1 })
	a, b := r.control(t), r.control(t)

	a.Send(t, protocol.RelayHost{Seq: 1, Code: code, TokenSha256: sha})
	f.waitCalls(t, 1)
	a.Send(t, protocol.RelayRotate{Seq: 2, TokenSha256: sha})
	if res := hostResult(t, a); res.Seq != 2 || errorOf(res) != "busy" {
		t.Fatalf("second on a socket: %+v", res)
	}
	a.Send(t, protocol.RelayPing{}) // the control loop still answers
	if _, ok := a.Next(t, 5*time.Second).Msg.(protocol.RelayPong); !ok {
		t.Fatal("no RELAY_PONG while hosting")
	}

	b.Send(t, protocol.RelayRotate{Seq: 3, TokenSha256: sha})
	if res := hostResult(t, b); res.Seq != 3 || errorOf(res) != "busy" {
		t.Fatalf("over the cap: %+v", res)
	}
	b.Send(t, protocol.RelayUnhost{Seq: 4})
	f.waitCalls(t, 2) // UNHOST is never held back by the cap
	f.release <- struct{}{}
	f.release <- struct{}{}
	got := map[int64]bool{}
	for _, c := range []*testkit.Client{a, b} {
		res := hostResult(t, c)
		got[res.Seq] = res.Ok
	}
	if !got[1] || !got[4] {
		t.Fatalf("results %v", got)
	}

	// the slot is free again once the result is sent
	b.Send(t, protocol.RelayHost{Seq: 5, Code: code, TokenSha256: sha})
	f.waitCalls(t, 3)
	f.release <- struct{}{}
	if res := hostResult(t, b); res.Seq != 5 || !res.Ok {
		t.Fatalf("%+v", res)
	}
}

// O4: a hosted call that fails as a bug (panic) is answered failed and logged by type; a result for a socket that
// closed meanwhile is dropped.
func TestO4FailureAndLateResult(t *testing.T) {
	log := &logBuffer{}
	t.Cleanup(logx.Capture(log))
	r := newRig(t, &fakeHosted{configured: true, panics: true}, nil)
	c := r.control(t)
	c.Send(t, protocol.RelayUnhost{Seq: 9})
	if res := hostResult(t, c); res.Seq != 9 || errorOf(res) != "failed" {
		t.Fatalf("%+v", res)
	}
	if !strings.Contains(log.String(), "Hosting request of relay "+c.RelayID+" failed with") {
		t.Fatalf("log:\n%s", log.String())
	}

	f := &fakeHosted{configured: true, release: make(chan struct{})}
	r2 := newRig(t, f, nil)
	d := r2.control(t)
	d.Send(t, protocol.RelayUnhost{Seq: 1})
	f.waitCalls(t, 1)
	_ = d.WS.WriteControl(websocket.CloseMessage, websocket.FormatCloseMessage(1000, ""), time.Now().Add(time.Second))
	if code := d.Closed(t, 5*time.Second); code != websocket.CloseNormalClosure {
		t.Fatalf("closed %d", code)
	}
	f.release <- struct{}{}
	r2.settled(t)
}

// O6: CloseAll (the graceful stop) closes every control socket 1001.
func TestO6CloseAll(t *testing.T) {
	r := newRig(t, nil, nil)
	a, b := r.control(t), r.control(t)
	r.h.CloseAll()
	for _, c := range []*testkit.Client{a, b} {
		if code := c.Closed(t, 5*time.Second); code != websocket.CloseGoingAway {
			t.Fatalf("closed %d, want 1001", code)
		}
	}
	r.settled(t)

	// a registration completing after CloseAll is closed 1001 too, never left online (T6-T7 critic #4)
	late := r.control(t)
	if code := late.Closed(t, 5*time.Second); code != websocket.CloseGoingAway {
		t.Fatalf("late registration closed %d, want 1001", code)
	}
	r.settled(t)
}

// O8: an offline relayId's 4504 is logged once per relayId and kind per hour (with the previous hour's count); the dial
// token never appears.
func TestO8NotOnlineLoggedOnceAnHour(t *testing.T) {
	log := &logBuffer{}
	t.Cleanup(logx.Capture(log))
	r := newRig(t, nil, nil)
	const offline, token = "ZZZdEfGhIjKlMnOpQrSt_-", "c2VjcmV0LWRpYWwtdG9rZW4tdmFsdWU"
	dial := func(kind string) {
		t.Helper()
		ws, _, err := websocket.DefaultDialer.Dial(testkit.WSURL(r.base, "/v1/stream?dial="+offline+"&kind="+kind), http.Header{"X-Panda-Dial": {token}})
		if err != nil {
			t.Fatal(err)
		}
		defer ws.Close()
		_ = ws.SetReadDeadline(time.Now().Add(5 * time.Second))
		_, _, err = ws.ReadMessage()
		if code := testkit.CloseCode(err); code != protocol.CloseNotOnline {
			t.Fatalf("closed %d, want 4504", code)
		}
	}
	dial("link")
	dial("link")
	dial("pair")
	r.clock.Advance(time.Hour)
	dial("link")
	r.settled(t)

	text := log.String()
	line := "refused 4504 from 192.0.2.7 (logged once an hour; "
	if n := strings.Count(text, "dial Link for "+offline+" "+line); n != 2 {
		t.Fatalf("%d Link lines, want 2 (one per hour):\n%s", n, text)
	}
	if n := strings.Count(text, "dial Pair for "+offline+" "+line); n != 1 {
		t.Fatalf("%d Pair lines, want 1:\n%s", n, text)
	}
	if strings.Contains(text, token) {
		t.Fatalf("the log holds the dial token:\n%s", text)
	}
}
