package quotas

import (
	"testing"
	"time"
)

func TestNotOnlineLogFirstOfAnHourWithThePreviousHoursCount(t *testing.T) {
	l := NewNotOnlineLog(2)
	at := time.Date(2026, 11, 20, 10, 5, 0, 0, time.UTC)
	if line, prev := l.Count("r1", "link", at); line != First || prev != 0 {
		t.Fatalf("first: %v %d", line, prev)
	}
	for i := 0; i < 3; i++ {
		if line, _ := l.Count("r1", "link", at); line != Repeat {
			t.Fatalf("repeat %d: %v", i, line)
		}
	}
	if line, _ := l.Count("r1", "pair", at); line != First {
		t.Fatal("the other kind is its own pair")
	}
	if line, _ := l.Count("r2", "link", at); line != Untracked {
		t.Fatal("a full table (2 pairs) must log every time")
	}
	next := at.Add(time.Hour)
	if line, prev := l.Count("r1", "link", next); line != First || prev != 4 {
		t.Fatalf("next hour: %v %d, want First with 4", line, prev)
	}
	later := next.Add(2 * time.Hour) // a gap: nothing "previous" to report
	if line, prev := l.Count("r1", "link", later); line != First || prev != 0 {
		t.Fatalf("after a gap: %v %d", line, prev)
	}
}
