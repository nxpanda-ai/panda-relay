package main

import (
	"net"
	"net/netip"
	"strconv"
	"testing"

	"github.com/nxpanda-ai/panda-relay/relay-go/internal/options"
)

func addrOf(t *testing.T, l net.Listener) netip.Addr {
	t.Helper()
	ap, err := netip.ParseAddrPort(l.Addr().String())
	if err != nil {
		t.Fatal(err)
	}
	return ap.Addr()
}

// O10: without --listen the relay listens on loopback only. O9: --internal-port listens on 127.0.0.1 whatever --listen
// says (here ::1, a second loopback, so the test never opens a port to the network).
func TestO9O10Loopback(t *testing.T) {
	o, err := options.Parse([]string{"--public-host", "relay.test", "--port", "0"})
	if err != nil {
		t.Fatal(err)
	}
	public, internal, err := listen(o)
	if err != nil {
		t.Fatal(err)
	}
	defer public.Close()
	if internal != nil {
		t.Fatal("an internal listener without --internal-port")
	}
	if a := addrOf(t, public); a != netip.MustParseAddr("127.0.0.1") {
		t.Fatalf("default listen on %v", a)
	}

	free, err := net.Listen("tcp", "127.0.0.1:0") // --internal-port takes 1-65535: borrow a free one
	if err != nil {
		t.Fatal(err)
	}
	port := free.Addr().(*net.TCPAddr).Port
	_ = free.Close()
	o2, err := options.Parse([]string{"--public-host", "relay.test", "--port", "0", "--listen", "::1", "--internal-port", strconv.Itoa(port)})
	if err != nil {
		t.Fatal(err)
	}
	if v6, err := net.Listen("tcp", "[::1]:0"); err != nil { // skip only for a missing IPv6 loopback (critic #8)
		t.Skipf("no IPv6 loopback here: %v", err)
	} else {
		_ = v6.Close()
	}
	p2, i2, err := listen(o2)
	if err != nil {
		t.Fatal(err)
	}
	defer p2.Close()
	defer i2.Close()
	if a := addrOf(t, p2); a != netip.MustParseAddr("::1") {
		t.Fatalf("--listen ::1 listened on %v", a)
	}
	if a := addrOf(t, i2); a != netip.MustParseAddr("127.0.0.1") {
		t.Fatalf("internal listener on %v", a)
	}
}
