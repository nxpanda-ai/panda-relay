package server

import (
	"net/http"
	"net/netip"
	"testing"
	"time"

	"github.com/nxpanda-ai/panda-relay/relay-go/internal/options"
)

func req(remote string, header string, values ...string) *http.Request {
	r := &http.Request{RemoteAddr: remote, Header: http.Header{}}
	for _, v := range values {
		r.Header.Add(header, v)
	}
	return r
}

func behind(proxies ...string) *clientIP {
	o := options.Default("relay.test")
	for _, p := range proxies {
		o.TrustedProxies = append(o.TrustedProxies, netip.MustParsePrefix(p))
	}
	return newClientIP(o, func() time.Time { return time.Date(2026, 11, 20, 10, 0, 0, 0, time.UTC) })
}

func TestClientIPFromATrustedProxysRightMostAddress(t *testing.T) {
	c := behind("127.0.0.1/32")
	for _, row := range []struct {
		values []string
		want   string
	}{
		{[]string{"203.0.113.5"}, "203.0.113.5"},
		{[]string{"203.0.113.7:4711"}, "203.0.113.7"},
		{[]string{"::ffff:203.0.113.7"}, "::ffff:203.0.113.7"}, // the bucket maps it to IPv4 (quotas.Bucket)
		{[]string{"[2001:db8:1:2::1]:443"}, "2001:db8:1:2::1"},
		{[]string{"2001:db8:1:3::1"}, "2001:db8:1:3::1"},
		{[]string{"198.51.100.1, 203.0.113.8"}, "203.0.113.8"},
		{[]string{"198.51.100.2", "203.0.113.8"}, "203.0.113.8"}, // across header lines
		{[]string{"203.0.113.20, 127.0.0.1"}, "127.0.0.1"},       // one hop only
		{[]string{"[::1]"}, "::1"},
		{[]string{" 203.0.113.9 , "}, "203.0.113.9"}, // trimmed, empty entries skipped
		{[]string{`"203.0.113.10"`}, "203.0.113.10"}, // a quoted entry is unquoted
	} {
		if got := c.of(req("127.0.0.1:5000", "X-Forwarded-For", row.values...)); got != netip.MustParseAddr(row.want) {
			t.Errorf("%q: %s, want %s", row.values, got, row.want)
		}
	}
}

func TestAnUnusableHeaderKeepsThePeerAndWarnsOnceAMinute(t *testing.T) {
	c := behind("127.0.0.1/32")
	peer := netip.MustParseAddr("127.0.0.1")
	for _, values := range [][]string{nil, {"not an address"}, {"203.0.113.5:99999"}, {"203.0.113.5:"}, {""}, {"[203.0.113.5]"}} {
		if got := c.of(req("127.0.0.1:5000", "X-Forwarded-For", values...)); got != peer {
			t.Errorf("%q: %s, want the peer", values, got)
		}
	}
	if c.warn.held != 5 || c.warn.logged != 1 {
		t.Fatalf("held %d logged %d: one line, then counted for a minute", c.warn.held, c.warn.logged)
	}
}

func TestTheHeaderIsReadOnlyFromATrustedPeerAndOnlyTheConfiguredOne(t *testing.T) {
	if got := behind().of(req("127.0.0.1:5000", "X-Forwarded-For", "203.0.113.11")); got != netip.MustParseAddr("127.0.0.1") {
		t.Errorf("no trusted proxy: %s", got)
	}
	if got := behind("192.0.2.1/32").of(req("127.0.0.1:5000", "X-Forwarded-For", "203.0.113.13")); got != netip.MustParseAddr("127.0.0.1") {
		t.Errorf("an untrusted peer: %s", got)
	}
	c := behind("127.0.0.1/32")
	c.o.ClientIPHeader = "CF-Connecting-IP"
	r := req("127.0.0.1:5000", "X-Forwarded-For", "198.51.100.3")
	r.Header.Add("CF-Connecting-IP", "203.0.113.9")
	if got := c.of(r); got != netip.MustParseAddr("203.0.113.9") {
		t.Errorf("--client-ip-header: %s", got)
	}
	if got := behind("127.0.0.0/8").of(req("[::ffff:127.0.0.1]:5000", "X-Forwarded-For", "203.0.113.4")); got != netip.MustParseAddr("203.0.113.4") {
		t.Errorf("an IPv4-mapped peer in a trusted IPv4 network: %s", got)
	}
}
