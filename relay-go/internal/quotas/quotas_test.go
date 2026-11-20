package quotas

import (
	"net/netip"
	"testing"
	"time"

	"github.com/nxpanda-ai/panda-relay/relay-go/internal/options"
)

func opts() *options.Options {
	o := options.Default("relay.test")
	o.RegistrationsPerIPPerHour = 3
	o.RefusedDialsPerIPPerHour = 2
	o.NoisePerIPPerHour = 2
	o.StreamsPerDay = 2
	o.BytesPerDay = 100
	o.MaxTrackedIPs = 4
	o.MaxBucketsPerPrefix = 2
	o.MaxTrackedNoise = 2
	return o
}

var t0 = time.Date(2026, 11, 20, 10, 59, 30, 0, time.UTC)

func ip(s string) netip.Addr { return Bucket(netip.MustParseAddr(s)) }

func TestBucket(t *testing.T) {
	for in, want := range map[string]string{
		"203.0.113.7":                 "203.0.113.7",
		"::ffff:203.0.113.7":          "203.0.113.7",
		"2001:db8:1:2:3:4:5:6":        "2001:db8:1:2::",
		"2001:db8:1:2:ffff::1":        "2001:db8:1:2::",
		"fe80::1%eth0":                "fe80::",
		"2001:db8:1:3:3:4:5:6":        "2001:db8:1:3::",
		"::1":                         "::",
		"2001:db8:aaaa:bbbb:cccc::77": "2001:db8:aaaa:bbbb::",
	} {
		if got := ip(in); got != netip.MustParseAddr(want) {
			t.Errorf("%s: %s, want %s", in, got, want)
		}
	}
	if got := Bucket(netip.Addr{}); got != netip.MustParseAddr("255.255.255.255") {
		t.Errorf("no address: %s (IPAddress.None)", got)
	}
}

func TestRegistrationsPerHourAndTheSecondsLeft(t *testing.T) {
	q := New(opts())
	a := ip("203.0.113.7")
	for i := 0; i < 3; i++ {
		if ok, _ := q.TryCountRegistration(a, t0); !ok {
			t.Fatalf("registration %d refused", i+1)
		}
	}
	ok, retry := q.TryCountRegistration(a, t0)
	if ok || retry != 30 {
		t.Fatalf("4th: %v %d, want refused with 30 s left", ok, retry)
	}
	if ok, _ := q.TryCountRegistration(ip("203.0.113.8"), t0); !ok {
		t.Fatal("another IP shares the bucket")
	}
	if ok, retry := q.TryCountRegistration(a, t0.Add(30*time.Second)); !ok || retry != 3600 {
		t.Fatalf("a new hour: %v %d", ok, retry)
	}
}

func TestSecondsAreCeiledAndClamped(t *testing.T) {
	q := New(opts())
	if _, r := q.TryCountRegistration(ip("203.0.113.7"), t0.Add(29*time.Second+time.Millisecond)); r != 1 {
		t.Errorf("0.999 s left: %d, want 1", r)
	}
	if _, r := q.Blocked("r", time.Date(2026, 11, 20, 0, 0, 0, 0, time.UTC)); r != 86400 {
		t.Errorf("a whole day: %d", r)
	}
}

func TestDialerLimitedByRefusedDialsOrNoiseAndChecksAddNothing(t *testing.T) {
	q := New(opts())
	a := ip("203.0.113.7")
	if limited, _ := q.DialerLimited(a, t0); limited || q.TrackedIPs() != 0 {
		t.Fatal("a check added a bucket or limited a new IP")
	}
	q.CountRefusedDial(a, t0)
	q.CountRefusedDial(a, t0)
	if limited, _ := q.DialerLimited(a, t0); !limited {
		t.Fatal("2 refused dials did not limit")
	}
	b := ip("203.0.113.8")
	q.CountNoise(b, t0)
	q.CountNoise(b, t0)
	if limited, _ := q.DialerLimited(b, t0); !limited {
		t.Fatal("2 noise did not limit")
	}
}

func TestNotOnlineCountsOncePerIPAndRelayIDAnHourUpToTheTable(t *testing.T) {
	q := New(opts())
	a := ip("203.0.113.7")
	q.CountNotOnline(a, "r1", t0)
	q.CountNotOnline(a, "r1", t0)
	if limited, _ := q.DialerLimited(a, t0); limited {
		t.Fatal("a repeated 4504 counted twice")
	}
	q.CountNotOnline(a, "r2", t0)
	if limited, _ := q.DialerLimited(a, t0); !limited {
		t.Fatal("two relayIds = 2 noise")
	}
	// the table holds 2 pairs: a third pair counts every time
	b := ip("203.0.113.8")
	q.CountNotOnline(b, "r3", t0)
	q.CountNotOnline(b, "r3", t0)
	if limited, _ := q.DialerLimited(b, t0); !limited {
		t.Fatal("past the table every 4504 counts")
	}
}

func TestStreamsAndBytesPerDay(t *testing.T) {
	q := New(opts())
	if blocked, _ := q.Blocked("r", t0); blocked {
		t.Fatal("nothing spent yet")
	}
	q.CountStream("r", t0)
	q.CountStream("r", t0)
	if blocked, retry := q.Blocked("r", t0); !blocked || retry != 13*3600+30 {
		t.Fatalf("2 streams: %v %d", blocked, retry)
	}
	q2 := New(opts())
	if over, _ := q2.AddBytes("r", 100, t0); over {
		t.Fatal("exactly the quota is not over")
	}
	if blocked, _ := q2.Blocked("r", t0); blocked {
		t.Fatal("exactly the quota blocks")
	}
	if over, _ := q2.AddBytes("r", 1, t0); !over {
		t.Fatal("one byte over")
	}
	if blocked, _ := q2.Blocked("r", t0.Add(time.Hour*14)); blocked {
		t.Fatal("a new day keeps the old one")
	}
	o := opts()
	o.StreamsPerDay = 0
	if blocked, _ := New(o).Blocked("r", t0); !blocked {
		t.Fatal("a limit of 0 still blocks")
	}
}

func TestTableIsBoundedWithOneOverflowBucket(t *testing.T) {
	q := New(opts()) // MaxTrackedIPs 4
	for _, s := range []string{"203.0.113.1", "203.0.113.2", "203.0.113.3", "203.0.113.4"} {
		q.CountNoise(ip(s), t0)
	}
	q.CountNoise(ip("203.0.113.5"), t0)
	q.CountNoise(ip("203.0.113.6"), t0)
	if q.TrackedIPs() != 5 {
		t.Fatalf("tracked %d, want 4 + the overflow", q.TrackedIPs())
	}
	if limited, _ := q.DialerLimited(ip("203.0.113.9"), t0); !limited {
		t.Fatal("the overflow (2 noise) is shared by every new IP")
	}
	if limited, _ := q.DialerLimited(ip("203.0.113.1"), t0); limited {
		t.Fatal("a tracked IP keeps its own bucket")
	}
}

func TestA48OwnsAtMostItsCapOf64sThenShares(t *testing.T) {
	q := New(opts()) // MaxBucketsPerPrefix 2
	q.CountNoise(ip("2001:db8:1:1::1"), t0)
	q.CountNoise(ip("2001:db8:1:2::1"), t0)
	q.CountNoise(ip("2001:db8:1:3::1"), t0) // the /48's shared bucket
	q.CountNoise(ip("2001:db8:1:4::1"), t0) // the same shared bucket
	if q.TrackedIPs() != 3 {
		t.Fatalf("tracked %d, want 2 /64s + 1 shared", q.TrackedIPs())
	}
	if limited, _ := q.DialerLimited(ip("2001:db8:1:5::1"), t0); !limited {
		t.Fatal("a further /64 of the /48 is not in the shared bucket")
	}
	if limited, _ := q.DialerLimited(ip("2001:db8:2:1::1"), t0); limited {
		t.Fatal("another /48 was limited")
	}
}

func TestANewHourDropsEveryBucket(t *testing.T) {
	q := New(opts())
	q.CountNoise(ip("203.0.113.1"), t0)
	q.CountNoise(ip("203.0.113.1"), t0)
	if limited, _ := q.DialerLimited(ip("203.0.113.1"), t0.Add(31*time.Second)); limited {
		t.Fatal("the last hour's noise still limits")
	}
	if q.TrackedIPs() != 0 {
		t.Fatalf("tracked %d after the hour turned", q.TrackedIPs())
	}
}
