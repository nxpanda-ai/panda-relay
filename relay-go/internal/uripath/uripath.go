// Package uripath holds the URI path rules shared by the HTTP routing (server) and the connection-code parser (hosted).
package uripath

import "strings"

// RemoveDotSegments is RFC 3986 §5.2.4.
func RemoveDotSegments(in string) string {
	var out []byte
	dropLast := func() {
		if i := strings.LastIndexByte(string(out), '/'); i >= 0 {
			out = out[:i]
		} else {
			out = out[:0]
		}
	}
	for len(in) > 0 {
		switch {
		case strings.HasPrefix(in, "../"):
			in = in[3:]
		case strings.HasPrefix(in, "./"):
			in = in[2:]
		case strings.HasPrefix(in, "/./"):
			in = in[2:]
		case in == "/.":
			in = "/"
		case strings.HasPrefix(in, "/../"):
			in = in[3:]
			dropLast()
		case in == "/..":
			in = "/"
			dropLast()
		case in == "." || in == "..":
			in = ""
		default:
			end := strings.IndexByte(in[1:], '/') + 1 // the next segment, with its leading '/'
			if end == 0 {
				end = len(in)
			}
			out = append(out, in[:end]...)
			in = in[end:]
		}
	}
	return string(out)
}
