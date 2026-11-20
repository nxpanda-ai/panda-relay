// Package server ports RelayServer.cs: the listeners and the HTTP surface (plan-relay-go D4). One hand-written handler
// per listener — no ServeMux, whose 405 / 301 answers differ from Kestrel's 404.
package server

import (
	"context"
	"net"
	"net/http"
	"net/netip"
	"strconv"
	"strings"
	"time"

	"github.com/nxpanda-ai/panda-relay/relay-go/internal/clock"
	"github.com/nxpanda-ai/panda-relay/relay-go/internal/hub"
	"github.com/nxpanda-ai/panda-relay/relay-go/internal/logx"
	"github.com/nxpanda-ai/panda-relay/relay-go/internal/options"
	"github.com/nxpanda-ai/panda-relay/relay-go/internal/uripath"
)

// Limits Kestrel applies by default that Go's http.Server does not (plan-relay-go AC6). No endpoint reads a body.
const (
	readHeaderTimeout = 30 * time.Second  // Kestrel RequestHeadersTimeout
	idleTimeout       = 130 * time.Second // Kestrel KeepAliveTimeout: an idle keep-alive connection is closed
	maxHeaderBytes    = 32 * 1024         // Kestrel MaxRequestHeadersTotalSize
)

// Server serves one or two listeners (the public one and the optional internal one, C5).
type Server struct {
	opts   *options.Options
	hub    *hub.Hub
	client *clientIP
	public *http.Server
	intern *http.Server
}

// New builds the server for o; Serve starts it on the given listeners.
func New(o *options.Options, c clock.Clock, hosted hub.Hosted) *Server {
	s := &Server{opts: o, hub: hub.New(o, c, hosted), client: newClientIP(o, c.Now)}
	s.public = newHTTP(http.HandlerFunc(s.servePublic))
	s.intern = newHTTP(http.HandlerFunc(s.serveInternal))
	return s
}

func newHTTP(h http.Handler) *http.Server {
	return &http.Server{
		Handler:           h,
		ReadHeaderTimeout: readHeaderTimeout,
		IdleTimeout:       idleTimeout,
		MaxHeaderBytes:    maxHeaderBytes,
		ErrorLog:          logx.ServerLog(),
	}
}

// servePublic is the public listener's pipeline (RelayServer.cs:114-136): the endpoints "/v1/browser" and
// "/v1/stream", GET|HEAD /c/{relayId}, everything else 404. The client IP is the trusted-proxy rule's (C2).
func (s *Server) servePublic(w http.ResponseWriter, r *http.Request) {
	path := kestrelPath(r)
	switch {
	case routed(path, "/v1/browser"):
		s.hub.RunControl(w, r, s.client.of(r))
	case routed(path, "/v1/stream"):
		s.hub.RunStream(w, r, s.client.of(r), false)
	case (r.Method == http.MethodGet || r.Method == http.MethodHead) && strings.HasPrefix(path, helpPrefix):
		serveHelp(w, r, path)
	default:
		w.WriteHeader(http.StatusNotFound)
	}
}

// serveInternal (C5, RelayServer.cs:114-125): the internal listener serves only the hosted bridge's dials — the path
// /v1/stream exactly (OrdinalIgnoreCase) with a `dial` in the query; its dials have no IP bucket.
func (s *Server) serveInternal(w http.ResponseWriter, r *http.Request) {
	if !strings.EqualFold(kestrelPath(r), "/v1/stream") || !hub.HasQueryKey(r.URL.RawQuery, "dial") {
		w.WriteHeader(http.StatusNotFound)
		return
	}
	s.hub.RunStream(w, r, netip.Addr{}, true)
}

// routed is an endpoint route's match (app.Map(pattern, RequestDelegate) is IEndpointRouteBuilder.Map): the path
// itself, case-insensitively, with an optional trailing slash — never a sub-path. Measured on the C# relay 2026-11-20:
// /v1/browser, /v1/browser/, /V1/BROWSER → 400 (routed); /v1/browser/x, /v1/browserx, /v1%2Fbrowser → 404.
func routed(path, route string) bool {
	return strings.EqualFold(path, route) || strings.EqualFold(path, route+"/")
}

// kestrelPath is the request path as Kestrel routes it: every %XX decoded but %2F (a separator stays encoded), then
// its dot segments removed (RFC 3986 §5.2.4); "//" is kept (measured, routing_test.go).
func kestrelPath(r *http.Request) string {
	return uripath.RemoveDotSegments(decodePath(r.URL.EscapedPath()))
}

func decodePath(escaped string) string {
	if !strings.Contains(escaped, "%") {
		return escaped
	}
	b := make([]byte, 0, len(escaped))
	for i := 0; i < len(escaped); i++ {
		c := escaped[i]
		if c == '%' && i+2 < len(escaped) && !strings.EqualFold(escaped[i+1:i+3], "2F") {
			if v, err := strconv.ParseUint(escaped[i+1:i+3], 16, 8); err == nil {
				b = append(b, byte(v))
				i += 2
				continue
			}
		}
		b = append(b, c)
	}
	return string(b)
}

// Serve blocks serving the public listener (and the internal one in its own goroutine); it returns the public
// listener's error (http.ErrServerClosed after Close).
func (s *Server) Serve(public, internal net.Listener) error {
	if internal != nil {
		go func() { _ = s.intern.Serve(internal) }() // ends with ErrServerClosed at Close; Close is the only stop
	}
	return s.public.Serve(public)
}

// Close stops both listeners and their HTTP connections at once (WebSocket handlers are not tracked: see Shutdown).
func (s *Server) Close() {
	_ = s.public.Close()
	_ = s.intern.Close()
}

// ShutdownTimeout is the C# host's (RelayServer.cs:111): how long a graceful stop waits for the handlers.
const ShutdownTimeout = 5 * time.Second

// Shutdown is the graceful stop (SIGTERM / Ctrl+C, suite README O6): the listeners stop accepting, every control socket
// and its streams are closed 1001 (hub.CloseAll, the C# ApplicationStopping callback — RelayServer.cs:137), then the
// handlers get until timeout to end; whatever still runs is cut when the process exits.
func (s *Server) Shutdown(timeout time.Duration) {
	deadline := time.Now().Add(timeout)
	ctx, cancel := context.WithDeadline(context.Background(), deadline)
	defer cancel()
	done := make(chan struct{}, 2)
	for _, srv := range []*http.Server{s.public, s.intern} {
		go func() {
			_ = srv.Shutdown(ctx) // closes the listener at once; waits for plain HTTP requests until the deadline
			done <- struct{}{}
		}()
	}
	s.hub.CloseAll()
	s.hub.Wait(deadline)
	<-done
	<-done
}
