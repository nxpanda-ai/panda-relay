package hosted

import (
	"bytes"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/nxpanda-ai/panda-relay/relay-go/internal/logx"
	"github.com/nxpanda-ai/panda-relay/relay-go/internal/options"
	"github.com/nxpanda-ai/panda-relay/relay-go/internal/protocol"
)

const (
	relayID = "AbCdEfGhIjKlMnOpQrSt_-"
	code    = "panda-relay://relay.test/" + relayID + "#ABCDE-FGHIJ-KLMNO-PQRST-UVWXYZ"
	token   = "QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVphYmNkZWY" // 32 bytes, base64url
	sha     = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
	mcpURL  = "https://relay.test/mcp"
)

// seen is one request the fake hosted bridge received.
type seen struct {
	method, path, contentType, admin, acceptEncoding, userAgent string
	body                                                        map[string]any
	rawBody                                                     string
}

// bridge is a fake hosted bridge answering every request with answer; it records what it received.
type bridge struct {
	mu   sync.Mutex
	reqs []seen
}

func (b *bridge) requests() []seen {
	b.mu.Lock()
	defer b.mu.Unlock()
	return append([]seen(nil), b.reqs...)
}

func newAdmin(t *testing.T, answer http.HandlerFunc) (*Admin, *bridge) {
	t.Helper()
	b := &bridge{}
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		raw, _ := io.ReadAll(r.Body)
		var body map[string]any
		_ = json.Unmarshal(raw, &body)
		b.mu.Lock()
		b.reqs = append(b.reqs, seen{r.Method, r.URL.Path, r.Header.Get("Content-Type"), r.Header.Get("X-Panda-Admin"),
			r.Header.Get("Accept-Encoding"), r.Header.Get("User-Agent"), body, string(raw)})
		b.mu.Unlock()
		answer(w, r)
	}))
	t.Cleanup(srv.Close)
	return adminFor(t, srv.URL, time.Minute), b
}

func adminFor(t *testing.T, base string, timeout time.Duration) *Admin {
	t.Helper()
	o := options.Default("relay.test")
	u, err := url.Parse(base)
	if err != nil {
		t.Fatal(err)
	}
	o.HostedAdminURL, o.HostedAdminToken, o.HostedMCPURL, o.HostedTimeout = u, token, mcpURL, timeout
	return New(o)
}

func status(code int, body string) http.HandlerFunc {
	return func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/json; charset=utf-8")
		w.WriteHeader(code)
		_, _ = io.WriteString(w, body)
	}
}

// logBuffer captures the relay's log for one test.
type logBuffer struct {
	mu sync.Mutex
	b  bytes.Buffer
}

func (l *logBuffer) Write(p []byte) (int, error) {
	l.mu.Lock()
	defer l.mu.Unlock()
	return l.b.Write(p)
}
func (l *logBuffer) String() string { l.mu.Lock(); defer l.mu.Unlock(); return l.b.String() }

func captureLog(t *testing.T) *logBuffer {
	t.Helper()
	l := &logBuffer{}
	t.Cleanup(logx.Capture(l))
	return l
}

func errorOf(r protocol.RelayHostResult) string {
	if r.Error == nil {
		return ""
	}
	return string(*r.Error)
}

// HostedAdmin.cs:60-68, AdminEndpoints.Hosted.cs:26 / 77 / 102: method, path, credential, body per request kind; 200 =
// ok, with the hosted MCP URL for add / rotate only.
func TestRequestsAndSuccess(t *testing.T) {
	cases := []struct {
		name    string
		request protocol.RelayMessage
		path    string
		body    map[string]any
		url     bool
	}{
		{"host", protocol.RelayHost{Seq: 7, Code: code, TokenSha256: sha}, "/admin/hosted/add", map[string]any{"code": code, "tokenSha256": sha}, true},
		{"rotate", protocol.RelayRotate{Seq: 7, TokenSha256: sha}, "/admin/hosted/rotate", map[string]any{"relayId": relayID, "tokenSha256": sha}, true},
		{"unhost", protocol.RelayUnhost{Seq: 7}, "/admin/hosted/remove", map[string]any{"relayId": relayID}, false},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			a, b := newAdmin(t, status(200, `{"browserId":"x"}`))
			r := a.Run(relayID, c.request)
			if !r.Ok || r.Seq != 7 || r.Error != nil || (r.McpUrl != nil) != c.url || (c.url && *r.McpUrl != mcpURL) {
				t.Fatalf("result %+v", r)
			}
			reqs := b.requests()
			if len(reqs) != 1 {
				t.Fatalf("%d requests", len(reqs))
			}
			q := reqs[0]
			if q.method != http.MethodPost || q.path != c.path || q.admin != token || q.contentType != "application/json; charset=utf-8" {
				t.Fatalf("request %+v", q)
			}
			if q.acceptEncoding != "" || q.userAgent != "" { // MEASURED: HostedAdmin's HttpClient sends neither
				t.Fatalf("Accept-Encoding %q, User-Agent %q", q.acceptEncoding, q.userAgent)
			}
			if len(q.body) != len(c.body) {
				t.Fatalf("body %s", q.rawBody)
			}
			for k, v := range c.body {
				if q.body[k] != v {
					t.Fatalf("body %s", q.rawBody)
				}
			}
		})
	}
}

// HostedAdmin.cs:52-60: a code that does not parse is failed, one for another Chrome is mismatch — neither leaves the relay.
func TestCodeCheckedBeforeForwarding(t *testing.T) {
	for _, c := range []struct{ code, want string }{
		{"not a code", "failed"},
		{"panda-relay://relay.test/" + relayID + "#ABCDE", "failed"},
		{"panda-relay://relay.test/ZZZdEfGhIjKlMnOpQrSt_-#ABCDE-FGHIJ-KLMNO-PQRST-UVWXYZ", "mismatch"},
	} {
		a, b := newAdmin(t, status(200, "{}"))
		r := a.Run(relayID, protocol.RelayHost{Seq: 1, Code: c.code, TokenSha256: sha})
		if r.Ok || errorOf(r) != c.want || len(b.requests()) != 0 {
			t.Fatalf("%q: %+v, %d requests", c.code, r, len(b.requests()))
		}
	}
}

// HostedAdmin.cs:105-120: one meaning per status (+ the PandaError code for 409); anything else is failed. A body the C#
// parser throws on (a repeated top-level member: ArgumentException, outside its JsonException catch) is failed whatever
// the status (RelayHub.cs:298-302).
func TestStatusMapping(t *testing.T) {
	for _, c := range []struct {
		status int
		body   string
		want   string
	}{
		{400, `{"code":"AUTH_FAILED"}`, "failed"},
		{408, `{"code":"COMMAND_TIMEOUT"}`, "timeout"},
		{422, `{"code":"PERMISSION_DENIED"}`, "mismatch"},
		{409, `{"code":"AUTH_FAILED"}`, "rejected"},
		{409, `{"code":"PERMISSION_DENIED"}`, "conflict"},
		{409, `{"code":"BROWSER_NOT_FOUND"}`, "notHosted"},
		{409, `{"code":"STORE_FAILED"}`, "failed"},
		{409, `{"code":1}`, "failed"},
		{409, `[{"code":"AUTH_FAILED"}]`, "failed"},
		{409, `not json`, "failed"},
		{409, `{"code":"AUTH_FAILED"`, "failed"}, // cut short: JsonNode.Parse throws, the code is null
		{409, ``, "failed"},
		{409, `{"code":"AUTH_FAILED"} {}`, "failed"},
		{409, "\ufeff" + `{"code":"AUTH_FAILED"}`, "rejected"},        // ReadAsStringAsync drops a UTF-8 BOM
		{409, `{"x":{"a":1,"a":2},"code":"AUTH_FAILED"}`, "rejected"}, // nested objects are never materialised
		{409, `{"code":"AUTH_FAILED","code":"AUTH_FAILED"}`, "failed"},
		{408, `{"a":1,"a":1}`, "failed"},
		{408, `{"a":1,"a":2`, "timeout"},
		{408, `{"a":1,"a":2} x`, "timeout"},                                                              // trailing content wins over the repeat too                                                                 // a syntax error wins over the repeat (critic #1)
		{422, `{"a":1,"a":2,"x":` + strings.Repeat("[", 64) + strings.Repeat("]", 64) + "}", "mismatch"}, // so does a depth over 64
		{409, `{"code":"BROWSER_NOT_FOUND","n":1e400}`, "notHosted"},                                     // a number past float64 (critic #2)
		{409, nest(63), "rejected"},                                                                      // MEASURED: JsonNode.Parse allows a depth of 64 ...
		{409, nest(64), "failed"},                                                                        // ... and throws JsonReaderException past it
		{409, `{"Code":"AUTH_FAILED"}`, "failed"},
		{409, `{"code":"AUTH_FAILED"}  ` + "\r\n", "rejected"},
		{409, `{"code":"AUTH_FAILED",}`, "failed"},
		{409, `{"code":"AUTH_FAILED"/*c*/}`, "failed"},
		{404, `{"code":"UNSUPPORTED_COMMAND"}`, "failed"},
		{401, ``, "failed"},
		{500, `{"code":"STORE_FAILED"}`, "failed"},
		{204, ``, "failed"},
	} {
		a, _ := newAdmin(t, status(c.status, c.body))
		r := a.Run(relayID, protocol.RelayUnhost{Seq: 3})
		if r.Ok || r.Seq != 3 || errorOf(r) != c.want || r.McpUrl != nil {
			t.Errorf("%d %q: %+v, want %s", c.status, c.body, r, c.want)
		}
	}
}

// nest is a 409 body whose "x" holds d nested arrays (depth d + 1).
func nest(d int) string {
	return `{"code":"AUTH_FAILED","x":` + strings.Repeat("[", d) + strings.Repeat("]", d) + "}"
}

// HostedAdmin.cs:29 (AllowAutoRedirect = false): a redirect is an answer (failed), never followed.
func TestNoRedirect(t *testing.T) {
	var followed atomic.Bool
	target := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) { followed.Store(true) }))
	defer target.Close()
	a, _ := newAdmin(t, func(w http.ResponseWriter, r *http.Request) {
		http.Redirect(w, r, target.URL+"/admin/hosted/remove", http.StatusTemporaryRedirect)
	})
	r := a.Run(relayID, protocol.RelayUnhost{Seq: 1})
	if r.Ok || errorOf(r) != "failed" || followed.Load() {
		t.Fatalf("%+v, followed %v", r, followed.Load())
	}
}

// HostedAdmin.cs:29 (UseProxy = false): no proxy, whatever the environment says.
func TestNoProxy(t *testing.T) {
	t.Setenv("HTTP_PROXY", "http://127.0.0.1:1")
	t.Setenv("http_proxy", "http://127.0.0.1:1")
	t.Setenv("NO_PROXY", "")
	a, b := newAdmin(t, status(200, "{}"))
	if r := a.Run(relayID, protocol.RelayUnhost{Seq: 1}); !r.Ok || len(b.requests()) != 1 {
		t.Fatalf("%+v", r)
	}
	if tr, ok := a.client.Transport.(*http.Transport); !ok || tr.Proxy != nil {
		t.Fatal("the transport may use a proxy")
	}
}

// HostedAdmin.cs:19 / 32 (MaxResponseContentBufferSize 64 KiB): a longer answer fails, a 200 included.
func TestAnswerCap(t *testing.T) {
	for _, n := range []int{64 * 1024, 64*1024 + 1} {
		a, _ := newAdmin(t, status(200, `"`+strings.Repeat("x", n-2)+`"`))
		r := a.Run(relayID, protocol.RelayUnhost{Seq: 1})
		if want := n <= 64*1024; r.Ok != want {
			t.Fatalf("%d bytes: %+v", n, r)
		}
	}
}

// HostedAdmin.cs:88-101: HostedTimeout passed → timeout (the outcome is unknown, the Chrome unhosts); the bridge
// unreachable → failed. Both logged with the exception type only.
func TestTimeoutAndUnreachable(t *testing.T) {
	log := captureLog(t)
	release := make(chan struct{})
	var servers []*httptest.Server
	defer func() { // the handlers are let go first: Close waits for them
		close(release)
		for _, s := range servers {
			s.Close()
		}
	}()
	slow := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) { <-release }))
	servers = append(servers, slow)
	r := adminFor(t, slow.URL, 200*time.Millisecond).Run(relayID, protocol.RelayHost{Seq: 2, Code: code, TokenSha256: sha})
	if r.Ok || errorOf(r) != "timeout" {
		t.Fatalf("slow: %+v", r)
	}

	// a body that stalls after the headers: the total time is bounded too
	drip := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(200)
		w.(http.Flusher).Flush()
		<-release
	}))
	servers = append(servers, drip)
	if r := adminFor(t, drip.URL, 200*time.Millisecond).Run(relayID, protocol.RelayUnhost{Seq: 2}); r.Ok || errorOf(r) != "timeout" {
		t.Fatalf("drip: %+v", r)
	}

	gone := httptest.NewServer(http.NotFoundHandler())
	base := gone.URL
	gone.Close()
	if r := adminFor(t, base, time.Minute).Run(relayID, protocol.RelayUnhost{Seq: 2}); r.Ok || errorOf(r) != "failed" {
		t.Fatalf("gone: %+v", r)
	}
	text := log.String()
	for _, want := range []string{
		"warn: Hosted bridge unreachable for add of relay " + relayID + " (TimeoutException)",
		"warn: Hosted bridge unreachable for remove of relay " + relayID + " (TimeoutException)",
		"warn: Hosted bridge unreachable for remove of relay " + relayID + " (HttpRequestException)",
	} {
		if !strings.Contains(text, want) {
			t.Errorf("log lacks %q:\n%s", want, text)
		}
	}
}

// HostedAdmin.cs:126-127 + the S5 invariant: a refusal logs the status, the action and the relayId — never the code,
// its pair code, the token hash or the admin token.
func TestLogContent(t *testing.T) {
	log := captureLog(t)
	a, _ := newAdmin(t, status(409, `{"code":"AUTH_FAILED"}`))
	a.Run(relayID, protocol.RelayHost{Seq: 1, Code: code, TokenSha256: sha})
	a2, _ := newAdmin(t, status(409, `{"code":"AUTH_FAILED","code":"x"}`))
	a2.Run(relayID, protocol.RelayHost{Seq: 1, Code: code, TokenSha256: sha})
	text := log.String()
	if !strings.Contains(text, "info: Hosted bridge answered 409 to add of relay "+relayID+"\n") ||
		!strings.Contains(text, "warn: Hosting request of relay "+relayID+" failed with ArgumentException\n") {
		t.Fatalf("log:\n%s", text)
	}
	for _, secret := range []string{"ABCDE", "panda-relay://", sha, token} {
		if strings.Contains(text, secret) {
			t.Fatalf("the log holds %q:\n%s", secret, text)
		}
	}
}

func TestConfigured(t *testing.T) {
	if New(options.Default("relay.test")).Configured() {
		t.Fatal("configured without a hosted admin URL")
	}
	a, _ := newAdmin(t, status(200, "{}"))
	if !a.Configured() {
		t.Fatal("not configured")
	}
}
