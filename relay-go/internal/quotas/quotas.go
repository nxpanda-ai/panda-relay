// Package quotas ports RelayQuotas.cs (S6 / S1 counters) and NotOnlineLog.cs. Not thread-safe: the hub calls them
// under its lock, as the C# hub does.
package quotas

import (
	"math"
	"net/netip"
	"time"

	"github.com/nxpanda-ai/panda-relay/relay-go/internal/options"
)

type day struct {
	bytes   int64
	streams int
}

type hour struct {
	registrations int
	refusedDials  int
	noise         int
}

type seenKey struct {
	ip      netip.Addr
	relayID string
}

// Quotas: per relayId per UTC day, bytes piped and streams opened; per IP bucket per clock hour, registrations,
// token-refused dials and /v1/stream noise (C6). Only the current day and hour are kept; checks never add an entry;
// past MaxTrackedIPs buckets in one hour every new bucket shares one overflow counter; one IPv6 /48 owns at most
// MaxBucketsPerPrefix /64 buckets an hour, its further /64s share one bucket of that /48 (RelayQuotas.cs:5-15).
type Quotas struct {
	o             *options.Options
	days          map[string]*day
	hours         map[netip.Addr]*hour
	prefixHours   map[netip.Addr]*hour // a /48's shared bucket (plan-relay-prefix D1)
	perPrefix     map[netip.Addr]int   // /64 buckets each /48 owns this hour (D2)
	seenNotOnline map[seenKey]struct{} // C6: 4504 dedup, this hour only
	day           time.Time
	hour          time.Time
	overflow      *hour
}

// New is new RelayQuotas(options).
func New(o *options.Options) *Quotas {
	return &Quotas{
		o:             o,
		days:          map[string]*day{},
		hours:         map[netip.Addr]*hour{},
		prefixHours:   map[netip.Addr]*hour{},
		perPrefix:     map[netip.Addr]int{},
		seenNotOnline: map[seenKey]struct{}{},
	}
}

// TrackedIPs is the table's entries plus the overflow bucket.
func (q *Quotas) TrackedIPs() int {
	n := q.tracked()
	if q.overflow != nil {
		n++
	}
	return n
}

// TrackedChromes is the relayIds with a day entry.
func (q *Quotas) TrackedChromes() int { return len(q.days) }

func (q *Quotas) tracked() int { return len(q.hours) + len(q.prefixHours) }

// Blocked: true when the relayId may not open a stream now; retryAfter = seconds to the next UTC day.
func (q *Quotas) Blocked(relayID string, now time.Time) (blocked bool, retryAfter int) {
	retryAfter = q.dayWindow(now)
	var bytes int64
	streams := 0
	if d, ok := q.days[relayID]; ok { // no entry = nothing spent yet (a limit of 0 still blocks)
		bytes, streams = d.bytes, d.streams
	}
	return bytes > q.o.BytesPerDay || streams >= q.o.StreamsPerDay, retryAfter
}

// CountStream counts one opened stream.
func (q *Quotas) CountStream(relayID string, now time.Time) {
	q.dayWindow(now)
	q.dayOf(relayID).streams++
}

// AddBytes adds piped bytes; true when this crossed the day's byte quota (every stream of the relayId closes).
func (q *Quotas) AddBytes(relayID string, bytes int64, now time.Time) (over bool, retryAfter int) {
	retryAfter = q.dayWindow(now)
	d := q.dayOf(relayID)
	d.bytes += bytes
	return d.bytes > q.o.BytesPerDay, retryAfter
}

// TryCountRegistration counts one /v1/browser connection; false (with the seconds left in the hour) when the IP is
// over its limit.
func (q *Quotas) TryCountRegistration(ip netip.Addr, now time.Time) (ok bool, retryAfter int) {
	retryAfter = q.hourWindow(now)
	h := q.resolve(ip, true)
	if h.registrations >= q.o.RegistrationsPerIPPerHour {
		return false, retryAfter
	}
	h.registrations++
	return true, retryAfter
}

// DialerLimited: the IP spent its refused dials or its noise this hour (4530). Adds no entry.
func (q *Quotas) DialerLimited(ip netip.Addr, now time.Time) (limited bool, retryAfter int) {
	retryAfter = q.hourWindow(now)
	h := q.resolve(ip, false)
	return h != nil && (h.refusedDials >= q.o.RefusedDialsPerIPPerHour || h.noise >= q.o.NoisePerIPPerHour), retryAfter
}

// CountNoise counts one unit of noise (an unknown / late / wrong-owner accept, a malformed request) (C6).
func (q *Quotas) CountNoise(ip netip.Addr, now time.Time) {
	q.hourWindow(now)
	q.resolve(ip, true).noise++
}

// CountNotOnline: a 4504 costs one unit per (IP, relayId) per hour; past MaxTrackedNoise remembered pairs every new one
// counts (strict, memory bound) (C6).
func (q *Quotas) CountNotOnline(ip netip.Addr, relayID string, now time.Time) {
	q.hourWindow(now)
	key := seenKey{ip, relayID}
	if _, seen := q.seenNotOnline[key]; seen {
		return
	}
	if len(q.seenNotOnline) < q.o.MaxTrackedNoise {
		q.seenNotOnline[key] = struct{}{}
	}
	q.resolve(ip, true).noise++
}

// CountRefusedDial counts one token-refused dial (4501).
func (q *Quotas) CountRefusedDial(ip netip.Addr, now time.Time) {
	q.hourWindow(now)
	q.resolve(ip, true).refusedDials++
}

// dayWindow moves to now's UTC day (dropping the last one); returns the seconds left in it.
func (q *Quotas) dayWindow(now time.Time) int {
	utc := now.UTC()
	date := time.Date(utc.Year(), utc.Month(), utc.Day(), 0, 0, 0, 0, time.UTC)
	if !date.Equal(q.day) {
		clear(q.days)
		q.day = date
	}
	return seconds(utc, date.AddDate(0, 0, 1))
}

// hourWindow moves to now's clock hour (dropping the last one); returns the seconds left in it.
func (q *Quotas) hourWindow(now time.Time) int {
	utc := now.UTC()
	start := utc.Truncate(time.Hour)
	if !start.Equal(q.hour) {
		clear(q.hours)
		clear(q.prefixHours)
		clear(q.perPrefix)
		clear(q.seenNotOnline)
		q.overflow = nil
		q.hour = start
	}
	return seconds(utc, start.Add(time.Hour))
}

func (q *Quotas) dayOf(relayID string) *day {
	d, ok := q.days[relayID]
	if !ok {
		d = &day{}
		q.days[relayID] = d
	}
	return d
}

// resolve is RelayQuotas.Resolve (RelayQuotas.cs:155-196): (1) the IP's own bucket or its /48's shared bucket, even on
// a full table; (2) a full table → the overflow; (3) a /48 that owns its cap of /64s → its new shared bucket; (4) a new
// bucket of its own. A check (create=false) sees the same bucket and never adds an entry nor spends a /48's slot.
func (q *Quotas) resolve(ip netip.Addr, create bool) *hour {
	if h, ok := q.hours[ip]; ok {
		return h
	}
	prefix, isV6 := prefix48(ip)
	if isV6 {
		if h, ok := q.prefixHours[prefix]; ok {
			return h
		}
	}
	if q.tracked() >= q.o.MaxTrackedIPs {
		if create && q.overflow == nil {
			q.overflow = &hour{}
		}
		return q.overflow
	}
	if !create {
		return nil
	}
	if isV6 {
		owned := q.perPrefix[prefix]
		if owned >= q.o.MaxBucketsPerPrefix {
			h := &hour{}
			q.prefixHours[prefix] = h
			return h
		}
		q.perPrefix[prefix] = owned + 1
	}
	h := &hour{}
	q.hours[ip] = h
	return h
}

// prefix48 is the /48 of an IPv6 bucket (bytes 6..15 cleared); false for IPv4.
func prefix48(ip netip.Addr) (netip.Addr, bool) {
	if !ip.Is6() || ip.Is4In6() {
		return netip.Addr{}, false
	}
	return netip.PrefixFrom(ip, 48).Masked().Addr(), true
}

// Bucket is RelayQuotas.Bucket: the per-IP key. No address (the zero Addr) is IPAddress.None (255.255.255.255); an
// IPv4-mapped address counts as its IPv4 address; IPv6 counts per /64 (one client usually holds a whole /64). The zone
// is dropped (new IPAddress(bytes) has none).
func Bucket(ip netip.Addr) netip.Addr {
	switch {
	case !ip.IsValid():
		return netip.AddrFrom4([4]byte{255, 255, 255, 255})
	case ip.Is4In6():
		return ip.Unmap()
	case ip.Is4():
		return ip
	default:
		return netip.PrefixFrom(ip.WithZone(""), 64).Masked().Addr()
	}
}

// seconds: whole seconds until end, in 1..86400 (S6: the reason is a decimal integer in that range).
func seconds(now, end time.Time) int {
	s := math.Ceil(end.Sub(now).Seconds())
	return int(math.Min(math.Max(s, 1), 86400))
}
