// Package logx writes the relay's log: one line per event straight to stdout, never buffered (the conformance
// harness reads the output after ≤ 3 s of quiet, then kills the process — plan-relay-go AC6, D6).
//
// Rule (spec §78, AC6): callers pass relay ids, close codes, IPs and counts only — never a connection code, dial
// token, tunnel byte, query string, URL or header value.
package logx

import (
	"fmt"
	"io"
	"log"
	"os"
	"sync"
)

var (
	mu  sync.Mutex
	out io.Writer = os.Stdout // under mu
)

func write(level, format string, args ...any) {
	line := level + ": " + fmt.Sprintf(format, args...) + "\n"
	mu.Lock()
	defer mu.Unlock()
	_, _ = io.WriteString(out, line) // os.Stdout is unbuffered; a failed log write must not fail the relay
}

// Capture sends the log to w until restore is called — for tests of what is logged (suite README O8).
func Capture(w io.Writer) (restore func()) {
	mu.Lock()
	prev := out
	out = w
	mu.Unlock()
	return func() {
		mu.Lock()
		out = prev
		mu.Unlock()
	}
}

// Info logs an event.
func Info(format string, args ...any) { write("info", format, args...) }

// Warn logs a refusal or an unusual condition.
func Warn(format string, args ...any) { write("warn", format, args...) }

// Error logs a failure of the relay itself.
func Error(format string, args ...any) { write("fail", format, args...) }

// ServerLog is http.Server.ErrorLog: net/http's own messages (bad requests, TLS-less noise) through the same writer.
// They never carry a header value or query string (net/http logs the remote address and the error).
func ServerLog() *log.Logger { return log.New(serverWriter{}, "", 0) }

type serverWriter struct{}

func (serverWriter) Write(p []byte) (int, error) {
	msg := string(p)
	if n := len(msg); n > 0 && msg[n-1] == '\n' {
		msg = msg[:n-1]
	}
	write("warn", "http: %s", msg)
	return len(p), nil
}
