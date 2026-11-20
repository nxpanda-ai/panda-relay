package server

import (
	"net/http"
	"net/http/httptest"
	"testing"

	"github.com/nxpanda-ai/panda-relay/relay-go/internal/clock"
	"github.com/nxpanda-ai/panda-relay/relay-go/internal/options"
)

// Plain GETs (not upgrades): 400 = routed to a socket endpoint, 200 = the help page, 404 = nothing. Every row was
// measured on the C# relay with raw TCP requests on 2026-11-20 (T4 critic #5, round 2 N2).
func TestRoutingAsTheCsharpRelay(t *testing.T) {
	s := New(options.Default("relay.test"), clock.Real{}, nil)
	for path, want := range map[string]int{
		"/v1/browser":                    400,
		"/v1/browser/":                   400,
		"/V1/BROWSER":                    400,
		"/v1/browser/x":                  404,
		"/v1/stream/x":                   404,
		"/v1/stream/":                    400,
		"/v1/browserx":                   404,
		"/c/AAAAAAAAAAAAAAAAAAAAAA/":     404,
		"/v1%2Fbrowser":                  404,
		"/x/../v1/browser":               400,
		"/v1/./browser":                  400,
		"/v1/x/../browser":               400,
		"/x/%2E%2E/v1/browser":           400,
		"/v1/%2E/browser":                400,
		"/c/./AAAAAAAAAAAAAAAAAAAAAA":    200,
		"/c/x/../AAAAAAAAAAAAAAAAAAAAAA": 200,
		"/../v1/browser":                 400,
		"/v1/browser/.":                  400,
		"/v1/browser/..":                 404,
		"//v1/browser":                   404,
	} {
		w := httptest.NewRecorder()
		s.servePublic(w, httptest.NewRequest(http.MethodGet, path, nil))
		if w.Code != want {
			t.Errorf("%s: %d, want %d", path, w.Code, want)
		}
	}
}
