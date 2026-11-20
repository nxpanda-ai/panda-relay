package quotas

import "time"

// Line is what the hub does with one 4504 dial refusal's log line (NotOnlineLog.cs:5-16).
type Line int

const (
	// First of its (relayId, kind) this hour: logged with the previous hour's count.
	First Line = iota
	// Repeat: seen this hour already — counted, not logged.
	Repeat
	// Untracked: the hour's table is full — logged plainly, every time (never silent).
	Untracked
)

type notOnlineKey struct{ relayID, kind string }

// NotOnlineLog is NotOnlineLog.cs (2026-11-18): the first 4504 of each (relayId, kind) in a UTC clock hour is logged
// with that pair's count in the previous hour; the rest of the hour is counted, not logged. At most maxTracked pairs
// this hour plus as many kept from the hour before; a pair that does not fit is logged every time. Logging only.
// Not thread-safe: the hub calls it under its lock.
type NotOnlineLog struct {
	maxTracked int
	thisHour   map[notOnlineKey]int
	lastHour   map[notOnlineKey]int
	hour       time.Time
}

// NewNotOnlineLog is new NotOnlineLog(maxTracked).
func NewNotOnlineLog(maxTracked int) *NotOnlineLog {
	return &NotOnlineLog{maxTracked: maxTracked, thisHour: map[notOnlineKey]int{}, lastHour: map[notOnlineKey]int{}}
}

// Count counts one 4504; previousHour (meaningful for First only) is the pair's count in the clock hour before.
func (l *NotOnlineLog) Count(relayID, kind string, now time.Time) (line Line, previousHour int) {
	hour := now.UTC().Truncate(time.Hour)
	if !hour.Equal(l.hour) {
		// only the hour right before carries its counts; after a gap there is nothing "previous" to report
		if hour.Equal(l.hour.Add(time.Hour)) {
			l.lastHour = l.thisHour
		} else {
			l.lastHour = map[notOnlineKey]int{}
		}
		l.thisHour = map[notOnlineKey]int{}
		l.hour = hour
	}
	key := notOnlineKey{relayID, kind}
	if seen, ok := l.thisHour[key]; ok {
		l.thisHour[key] = seen + 1
		return Repeat, 0
	}
	if len(l.thisHour) >= l.maxTracked {
		return Untracked, 0
	}
	l.thisHour[key] = 1
	return First, l.lastHour[key]
}
