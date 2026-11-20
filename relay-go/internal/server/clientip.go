package server

import (
	"net/http"
	"net/netip"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/nxpanda-ai/panda-relay/relay-go/internal/logx"
	"github.com/nxpanda-ai/panda-relay/relay-go/internal/options"
)

// clientIP is RelayServer.UseClientIp (plan-relay-deploy C2, seam S9; RelayServer.cs:143-189): with --trusted-proxy,
// the client is the right-most address of --client-ip-header when — and only when — the TCP peer is in a trusted
// network (ASP.NET ForwardedHeadersMiddleware, ForwardLimit 1, its loopback defaults cleared). A trusted peer whose
// header gives no usable address keeps its own address, and proxyWarning notes it. Public listener only (C5).
type clientIP struct {
	o    *options.Options
	warn *proxyWarning
}

func newClientIP(o *options.Options, now func() time.Time) *clientIP {
	return &clientIP{o: o, warn: &proxyWarning{now: now}}
}

// peer is the TCP peer's address (zero when it cannot be read).
func peer(r *http.Request) netip.Addr {
	ap, err := netip.ParseAddrPort(r.RemoteAddr)
	if err != nil {
		return netip.Addr{}
	}
	return ap.Addr()
}

func (c *clientIP) of(r *http.Request) netip.Addr {
	p := peer(r)
	if len(c.o.TrustedProxies) == 0 || !c.trusted(p) {
		return p
	}
	client := p
	if entries := commaSeparated(r.Header.Values(c.o.ClientIPHeader)); len(entries) > 0 {
		if a, ok := parseEndPoint(entries[len(entries)-1]); ok {
			client = a
		}
	}
	if client == p {
		c.warn.note()
	}
	return client
}

// trusted is ForwardedHeadersMiddleware.CheckKnownAddress: an IPv4-mapped peer is also checked as its IPv4 address.
func (c *clientIP) trusted(a netip.Addr) bool {
	if !a.IsValid() {
		return false
	}
	a = a.WithZone("")
	for _, n := range c.o.TrustedProxies {
		if n.Contains(a) || (a.Is4In6() && n.Contains(a.Unmap())) {
			return true
		}
	}
	return false
}

// commaSeparated is StringValues → GetCommaSeparatedValues: every line split on ',', trimmed, a quoted entry
// unquoted, empty entries skipped.
func commaSeparated(lines []string) []string {
	var out []string
	for _, l := range lines {
		for _, e := range strings.Split(l, ",") {
			e = strings.TrimSpace(e)
			if len(e) >= 2 && e[0] == '"' && e[len(e)-1] == '"' {
				e = e[1 : len(e)-1]
			}
			if e != "" {
				out = append(out, e)
			}
		}
	}
	return out
}

// parseEndPoint is IPEndPoint.TryParse: "addr", "a.b.c.d:port", "[v6]" or "[v6]:port" (a colon is a port separator
// only after ']' or when it is the only colon); the port is decimal digits up to 65535.
func parseEndPoint(s string) (netip.Addr, bool) {
	addr := s
	if i := strings.LastIndexByte(s, ':'); i > 0 && (s[i-1] == ']' || !strings.Contains(s[:i], ":")) {
		port := s[i+1:]
		if n, err := strconv.ParseUint(port, 10, 32); err != nil || n > 65535 || strings.ContainsAny(port, "+-") {
			return netip.Addr{}, false
		}
		addr = s[:i]
	}
	bracketed := len(addr) > 1 && addr[0] == '[' && addr[len(addr)-1] == ']'
	if bracketed {
		addr = addr[1 : len(addr)-1]
	}
	a, err := netip.ParseAddr(addr)
	return a, err == nil && (!bracketed || a.Is6()) // brackets hold an IPv6 address only
}

// proxyWarning is ProxyWarning.cs: a trusted proxy that sends no usable client IP leaves every client in the proxy's
// own bucket. Said at most once a minute with the number of such requests since the previous line; never a header value.
type proxyWarning struct {
	mu     sync.Mutex
	now    func() time.Time
	held   int64
	last   time.Time
	logged int // lines written (tests)
}

const proxyWarningInterval = time.Minute

func (w *proxyWarning) note() {
	w.mu.Lock()
	now := w.now()
	w.held++
	if !w.last.IsZero() && now.Sub(w.last) < proxyWarningInterval {
		w.mu.Unlock()
		return
	}
	count := w.held
	w.held = 0
	w.last = now
	w.logged++
	w.mu.Unlock()
	logx.Warn("trusted proxy sent no client IP; requests without one since the last warning: %d", count)
}
