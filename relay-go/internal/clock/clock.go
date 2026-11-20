// Package clock is the relay's one source of time (plan-relay-go D5): the real clock in main, a fake one in tests
// (the C# relay's TimeProvider).
package clock

import "time"

// Timer is a one-shot timer started by AfterFunc.
type Timer interface {
	// Stop prevents the function from running; false when it already ran or was stopped.
	Stop() bool
	// Reset makes the function run after d (again); false when the timer had expired or been stopped.
	Reset(d time.Duration) bool
}

// Clock gives the time and timers.
type Clock interface {
	Now() time.Time
	AfterFunc(d time.Duration, f func()) Timer
}

// Real is the system clock.
type Real struct{}

// Now is time.Now.
func (Real) Now() time.Time { return time.Now() }

// AfterFunc is time.AfterFunc.
func (Real) AfterFunc(d time.Duration, f func()) Timer { return time.AfterFunc(d, f) }
