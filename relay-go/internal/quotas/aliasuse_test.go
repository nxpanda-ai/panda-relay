package quotas

import (
	"testing"
	"time"
)

// AliasHostTests.The_alias_count_reports_the_last_hour_with_a_use (plan-relay-rename D6, diff critic F2).
func TestAliasUseReportsTheLastHourWithAUse(t *testing.T) {
	var l AliasUseLog
	t0 := time.Date(2026, 11, 24, 10, 15, 0, 0, time.UTC)
	if first, h, r, d := l.Count(Registration, t0); !first || HourName(h) != "none" || r != 0 || d != 0 {
		t.Fatalf("first use: %v %s %d %d", first, HourName(h), r, d)
	}
	for _, m := range []time.Duration{time.Minute, 44 * time.Minute} { // 10:16, 10:59
		if first, _, _, _ := l.Count(Dial, t0.Add(m)); first {
			t.Fatal("a repeat in the same hour is first")
		}
	}
	if first, h, r, d := l.Count(Dial, t0.Add(45*time.Minute)); !first || HourName(h) != "2026-11-24T10Z" || r != 1 || d != 2 {
		t.Fatalf("11:00: %v %s %d %d, want true 2026-11-24T10Z 1 2", first, HourName(h), r, d)
	}
	if first, h, r, d := l.Count(Registration, t0.Add(3*time.Hour)); !first || HourName(h) != "2026-11-24T11Z" || r != 0 || d != 1 {
		t.Fatalf("after a gap: %v %s %d %d, want true 2026-11-24T11Z 0 1", first, HourName(h), r, d)
	}
}
