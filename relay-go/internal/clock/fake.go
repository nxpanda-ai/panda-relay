package clock

import (
	"sort"
	"sync"
	"time"
)

// Fake is a manual clock for tests (the C# tests' FakeTimeProvider): time moves only by Advance, which runs every timer
// that falls due, in due order, on the caller's goroutine and outside the clock's lock (a callback may use the clock).
type Fake struct {
	mu     sync.Mutex
	now    time.Time
	timers []*fakeTimer
}

// NewFake starts a fake clock at start.
func NewFake(start time.Time) *Fake { return &Fake{now: start} }

// Now is the fake time.
func (f *Fake) Now() time.Time {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.now
}

// AfterFunc arms a timer on the fake time.
func (f *Fake) AfterFunc(d time.Duration, fn func()) Timer {
	f.mu.Lock()
	defer f.mu.Unlock()
	t := &fakeTimer{clock: f, fn: fn, due: f.now.Add(d), armed: true}
	f.timers = append(f.timers, t)
	return t
}

// Advance moves the time by d, running each timer due by then.
func (f *Fake) Advance(d time.Duration) {
	f.mu.Lock()
	end := f.now.Add(d)
	f.mu.Unlock()
	for {
		f.mu.Lock()
		sort.SliceStable(f.timers, func(i, j int) bool { return f.timers[i].due.Before(f.timers[j].due) })
		var next *fakeTimer
		for _, t := range f.timers {
			if t.armed && !t.due.After(end) {
				next = t
				break
			}
		}
		if next == nil {
			f.now = end
			f.mu.Unlock()
			return
		}
		if next.due.After(f.now) {
			f.now = next.due
		}
		next.armed = false
		f.mu.Unlock()
		next.fn()
	}
}

// Armed counts the timers waiting to run (a test's check that a handler stopped its timers).
func (f *Fake) Armed() int {
	f.mu.Lock()
	defer f.mu.Unlock()
	n := 0
	for _, t := range f.timers {
		if t.armed {
			n++
		}
	}
	return n
}

type fakeTimer struct {
	clock *Fake
	fn    func()
	due   time.Time
	armed bool // under clock.mu
}

func (t *fakeTimer) Stop() bool {
	t.clock.mu.Lock()
	defer t.clock.mu.Unlock()
	was := t.armed
	t.armed = false
	return was
}

func (t *fakeTimer) Reset(d time.Duration) bool {
	t.clock.mu.Lock()
	defer t.clock.mu.Unlock()
	was := t.armed
	t.due, t.armed = t.clock.now.Add(d), true
	return was
}
