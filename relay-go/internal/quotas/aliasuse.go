package quotas

import "time"

// AliasUse is what used an alias host (AliasUseLog.cs, plan-relay-rename D6).
type AliasUse int

const (
	// Registration: a RELAY_REGISTER whose proof verified for an alias host.
	Registration AliasUse = iota
	// Dial: a /v1/stream dial whose request Host is an alias host.
	Dial
)

// String is the C# enum's name, as the log line prints it.
func (u AliasUse) String() string {
	if u == Registration {
		return "Registration"
	}
	return "Dial"
}

// AliasUseLog is AliasUseLog.cs: how much a renamed relay's old name is still used, counted per UTC clock hour; the
// first use of each hour is logged with the counts of the last hour that had a use (named, so a gap between uses loses
// nothing), the rest only counted — never a relayId. Not thread-safe: the hub calls it under its lock.
type AliasUseLog struct {
	hour                 time.Time // zero: no use yet
	registrations, dials int
}

// Count counts one use; first = the first of this hour (log it) with the last hour that had a use (zero: none) and
// that hour's counts.
func (l *AliasUseLog) Count(use AliasUse, now time.Time) (first bool, lastHour time.Time, lastRegistrations, lastDials int) {
	hour := now.UTC().Truncate(time.Hour)
	first = !hour.Equal(l.hour)
	if first {
		lastHour, lastRegistrations, lastDials = l.hour, l.registrations, l.dials
		l.hour, l.registrations, l.dials = hour, 0, 0
	}
	if use == Registration {
		l.registrations++
	} else {
		l.dials++
	}
	return first, lastHour, lastRegistrations, lastDials
}

// HourName is AliasUseLog.Name: the hour as the log line names it (2026-11-24T10Z), or "none".
func HourName(hour time.Time) string {
	if hour.IsZero() {
		return "none"
	}
	return hour.UTC().Format("2006-01-02T15Z")
}
