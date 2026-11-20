// Package hosted ports HostedAdmin.cs (plan-relay-url U3, seam S5 of plan-relay-go): a registered Chrome's RELAY_HOST /
// ROTATE / UNHOST forwarded to the hosted bridge's /admin/hosted/* (AdminEndpoints.Hosted.cs) and its answer mapped to
// RELAY_HOST_RESULT. The relay's only check is that a code names the socket's own relayId; the code's host is the
// hosted bridge's check (one owner). The code, its token hash and the admin token travel only in the request to the
// configured 127.0.0.1 origin: never logged, never echoed.
package hosted

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"time"

	"github.com/nxpanda-ai/panda-relay/relay-go/internal/logx"
	"github.com/nxpanda-ai/panda-relay/relay-go/internal/options"
	"github.com/nxpanda-ai/panda-relay/relay-go/internal/protocol"
)

// maxAnswer is HttpClient.MaxResponseContentBufferSize (HostedAdmin.cs:19): a longer answer fails, a 200 included
// (MEASURED: 65536 bytes pass, 65537 throw HttpRequestException, with or without Content-Length).
const maxAnswer = 64 * 1024

// maxDepth is JsonNode.Parse's default (MEASURED: a depth of 64 parses, 65 throws).
const maxDepth = 64

// Admin is HostedAdmin; it implements hub.Hosted. Safe for concurrent use (one http.Client, no other state).
type Admin struct {
	o      *options.Options
	client *http.Client
}

// New is new HostedAdmin(options): no proxy, no redirects, HostedTimeout over the whole exchange — connect, headers and
// the body (HttpClient.Timeout with the default ResponseContentRead).
func New(o *options.Options) *Admin {
	return &Admin{o: o, client: &http.Client{
		Transport: &http.Transport{
			Proxy:              nil, // UseProxy = false
			DialContext:        (&net.Dialer{}).DialContext,
			DisableCompression: true, // no Accept-Encoding, as HttpClient without AutomaticDecompression (MEASURED)
			MaxIdleConns:       16,
			// SocketsHttpHandler.PooledConnectionIdleTimeout's default, 1 min on .NET 6+ (learn.microsoft.com): below
			// Kestrel's 130 s keep-alive, so a pooled connection is never reused as the bridge closes it (critic #5)
			IdleConnTimeout: time.Minute,
		},
		CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }, // AllowAutoRedirect = false
		Timeout:       o.HostedTimeout,
	}}
}

// Configured: a hosted bridge was given (--hosted-admin-url and its set).
func (a *Admin) Configured() bool { return a.o.HostedAdminURL != nil }

// Run is RunAsync: the result for request from the Chrome registered as relayID. A request that is not a hosting one is
// a programmer error (panic; the hub answers failed).
func (a *Admin) Run(relayID string, request protocol.RelayMessage) protocol.RelayHostResult {
	switch r := request.(type) {
	case protocol.RelayHost:
		id, ok := ParseCode(r.Code)
		if !ok {
			return failed(r.Seq, protocol.RelayHostErrorFailed)
		}
		if id != relayID {
			return failed(r.Seq, protocol.RelayHostErrorMismatch) // a code for another Chrome never leaves the relay
		}
		return a.post(relayID, r.Seq, "add", struct {
			Code        string `json:"code"`
			TokenSha256 string `json:"tokenSha256"`
		}{r.Code, r.TokenSha256}, true)
	case protocol.RelayRotate:
		return a.post(relayID, r.Seq, "rotate", struct {
			RelayID     string `json:"relayId"`
			TokenSha256 string `json:"tokenSha256"`
		}{relayID, r.TokenSha256}, true)
	case protocol.RelayUnhost:
		return a.post(relayID, r.Seq, "remove", struct {
			RelayID string `json:"relayId"`
		}{relayID}, false)
	default:
		panic(fmt.Sprintf("hosted: not a hosting request: %T", request))
	}
}

func failed(seq int64, e protocol.RelayHostError) protocol.RelayHostResult {
	return protocol.RelayHostResult{Seq: seq, Ok: false, Error: &e}
}

func (a *Admin) post(relayID string, seq int64, action string, body any, withURL bool) protocol.RelayHostResult {
	payload, err := json.Marshal(body) // strings only: cannot fail
	if err != nil {
		panic(err)
	}
	target := a.o.HostedAdminURL.ResolveReference(&url.URL{Path: "/admin/hosted/" + action})
	req, err := http.NewRequest(http.MethodPost, target.String(), bytes.NewReader(payload))
	if err != nil {
		panic(err) // the admin URL was validated at start (options.validateHosted)
	}
	req.Header.Set("Content-Type", "application/json; charset=utf-8")
	req.Header.Set("X-Panda-Admin", a.o.HostedAdminToken)
	req.Header["User-Agent"] = []string{""} // HttpClient sends none (MEASURED); net/http omits an empty one

	resp, err := a.client.Do(req)
	if err != nil {
		return a.unreachable(relayID, seq, action, err)
	}
	answer, err := io.ReadAll(io.LimitReader(resp.Body, maxAnswer+1))
	_ = resp.Body.Close()
	if err != nil {
		return a.unreachable(relayID, seq, action, err)
	}
	if len(answer) > maxAnswer {
		return a.unreachable(relayID, seq, action, errTooLong)
	}
	if resp.StatusCode == http.StatusOK {
		result := protocol.RelayHostResult{Seq: seq, Ok: true}
		if withURL {
			u := a.o.HostedMCPURL
			result.McpUrl = &u
		}
		return result
	}

	code, malformed := errorCode(answer)
	if malformed {
		// JsonObject throws ArgumentException on a repeated member, outside ErrorCode's JsonException catch: the hub's
		// catch answers failed and logs it (RelayHub.cs:298-302) — never the refusal line
		logx.Warn("Hosting request of relay %s failed with ArgumentException", relayID)
		return failed(seq, protocol.RelayHostErrorFailed)
	}
	// S5 CONSUMER RELIES ON: one meaning per status (+ the PandaError code for 409) — HostedAdmin.cs:105-119
	e := protocol.RelayHostErrorFailed
	switch resp.StatusCode {
	case 408:
		e = protocol.RelayHostErrorTimeout
	case 422:
		e = protocol.RelayHostErrorMismatch
	case 409:
		switch code {
		case "AUTH_FAILED":
			e = protocol.RelayHostErrorRejected
		case "PERMISSION_DENIED":
			e = protocol.RelayHostErrorConflict
		case "BROWSER_NOT_FOUND":
			e = protocol.RelayHostErrorNotHosted
		}
	}
	logx.Info("Hosted bridge answered %d to %s of relay %s", resp.StatusCode, action, relayID)
	return failed(seq, e)
}

var errTooLong = errors.New("answer over the cap")

// unreachable: HostedTimeout passed → timeout (the add may still finish there: outcome unknown, the Chrome unhosts —
// critic U3 #1); anything else (refused, reset, over the cap) → failed. Logged with the C# exception type only.
func (a *Admin) unreachable(relayID string, seq int64, action string, err error) protocol.RelayHostResult {
	var ne net.Error
	if errors.As(err, &ne) && ne.Timeout() {
		logx.Warn("Hosted bridge unreachable for %s of relay %s (TimeoutException)", action, relayID)
		return failed(seq, protocol.RelayHostErrorTimeout)
	}
	logx.Warn("Hosted bridge unreachable for %s of relay %s (HttpRequestException)", action, relayID)
	return failed(seq, protocol.RelayHostErrorFailed)
}

// errorCode is ErrorCode (HostedAdmin.cs:122-133) on the answer as ReadAsStringAsync + JsonNode.Parse read it: the
// string "code" member of a top-level object, else "". A UTF-8 BOM is dropped; anything JsonNode.Parse refuses
// (syntax, trailing content, comments, a depth over 64) gives ""; a repeated top-level member is malformed (C# throws
// ArgumentException there); repeated members deeper down are never looked at.
func errorCode(answer []byte) (code string, malformed bool) {
	answer = bytes.TrimPrefix(answer, []byte("\xef\xbb\xbf"))
	dec := json.NewDecoder(bytes.NewReader(answer))
	dec.UseNumber() // JsonNode keeps numbers as text: 1e400 is no error there (T6-T7 critic #2)
	depth := 0
	seen := map[string]bool{}
	topObject, repeated := false, false
	var key string
	expectKey := false
	for {
		tok, err := dec.Token()
		if err == io.EOF {
			if depth != 0 {
				return "", false // cut short: Token reports a truncated document as a plain EOF
			}
			break
		}
		if err != nil {
			return "", false
		}
		switch t := tok.(type) {
		case json.Delim:
			switch t {
			case '{', '[':
				if depth == 0 {
					topObject = t == '{'
				}
				depth++
				if depth > maxDepth {
					return "", false
				}
				expectKey = t == '{' && depth == 1
				continue
			default:
				depth--
				if depth == 0 && dec.More() {
					return "", false // trailing content
				}
			}
		default:
			if depth == 0 { // a top-level scalar: not an object
				if dec.More() {
					return "", false
				}
				continue
			}
			if depth == 1 && topObject {
				if expectKey {
					key = t.(string)
					// JsonNode.Parse reads the whole document first (a syntax error or depth over 64 wins); the
					// repeat throws only later, at o["code"] (T6-T7 critic #1)
					repeated = repeated || seen[key]
					seen[key] = true
					expectKey = false
					continue
				}
				if s, ok := t.(string); ok && key == "code" {
					code = s
				}
			}
		}
		if depth == 1 && topObject {
			expectKey = true // after a member's value (scalar, or a nested value just closed)
		}
	}
	if !topObject {
		return "", false
	}
	if repeated {
		return "", true
	}
	return code, false
}
