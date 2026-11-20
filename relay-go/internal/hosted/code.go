package hosted

import (
	"net/netip"
	"strings"
	"unicode"

	"github.com/nxpanda-ai/panda-relay/relay-go/internal/auth"
	"github.com/nxpanda-ai/panda-relay/relay-go/internal/uripath"
)

// ParseCode is ConnectionCode.TryParse (bridge/src/Panda.Protocol/Relay/ConnectionCode.cs:26-71), code form only, as far
// as the relay needs it: whether input is a connection code `panda-relay://host[:port]/<relayId>#<code>`
// (`panda-relay+ws://` only on a loopback host) and the relayId it names. The rules are System.Uri's as MEASURED on
// .NET 10 (testdata/connection-codes.txt, 149 rows; no length cap: 200-char labels and a 70 000-char fragment parse): the input trimmed; the scheme case-insensitive and followed by
// "//"; no query (even "?"), no user info (an empty one is allowed); the host a bracketed IPv6 address or a name of
// [A-Za-z0-9-_] / non-ASCII labels (a trailing dot allowed); the port decimal ≤ 65535; in the path and the fragment a
// %XX of an unreserved character decoded and any other kept; the path's dot segments removed; the fragment a pair
// code once '-' is dropped (a space is escaped by Uri, so it never passes). The code itself is a secret: never logged.
// Beyond the rows (UNVERIFIED): a control character anywhere refuses, a non-ASCII host character must be a letter,
// digit or mark.
func ParseCode(input string) (relayID string, ok bool) {
	s := strings.TrimFunc(input, unicode.IsSpace) // String.Trim: the same White_Space set
	for i := 0; i < len(s); i++ {
		if s[i] < 0x20 || s[i] == 0x7f {
			return "", false
		}
	}
	colon := strings.IndexByte(s, ':')
	if colon < 0 {
		return "", false
	}
	var secure bool
	switch asciiLower(s[:colon]) {
	case "panda-relay":
		secure = true
	case "panda-relay+ws":
	default:
		return "", false
	}
	// the "//" after the scheme: either slash may be a '\' (MEASURED: `panda-relay:\\host/…`, `:/\`, `:\/` parse); a
	// '\' later in the authority does not (measured NO)
	rest := s[colon+1:]
	if len(rest) < 2 || (rest[0] != '/' && rest[0] != '\\') || (rest[1] != '/' && rest[1] != '\\') {
		return "", false
	}
	rest = rest[2:]
	rest, fragment, found := strings.Cut(rest, "#")
	if !found || strings.IndexByte(rest, '?') >= 0 {
		return "", false
	}
	slash := strings.IndexByte(rest, '/')
	if slash < 0 {
		return "", false
	}
	authority, path := rest[:slash], rest[slash:]
	if at := strings.IndexByte(authority, '@'); at >= 0 {
		if at != 0 {
			return "", false // user info
		}
		authority = authority[1:]
	}
	loopback, ok := parseAuthority(authority)
	if !ok || (!secure && !loopback) {
		return "", false
	}
	// Uri turns a raw '\' in the path into '/' before removing dot segments (MEASURED: "/x\..\<id>" parses; "%5C" stays)
	path, ok = decodeUnreserved(strings.ReplaceAll(path, `\`, "/"))
	if !ok {
		return "", false
	}
	id := strings.TrimPrefix(uripath.RemoveDotSegments(path), "/")
	if !auth.IsRelayID(id) {
		return "", false
	}
	fragment, _ = decodeUnreserved(fragment) // a malformed % stays and fails the pair code
	if !isPairCode(fragment) {
		return "", false
	}
	return id, true
}

// parseAuthority checks host[:port]; loopback is Uri.IsLoopback for it.
func parseAuthority(a string) (loopback, ok bool) {
	var host, port string
	if strings.HasPrefix(a, "[") {
		end := strings.IndexByte(a, ']')
		if end < 0 {
			return false, false
		}
		literal, after := a[1:end], a[end+1:]
		if after != "" {
			p, found := strings.CutPrefix(after, ":")
			if !found {
				return false, false
			}
			port = p
		}
		addr, err := netip.ParseAddr(strings.Replace(literal, "%25", "%", 1))
		if err != nil || !addr.Is6() {
			return false, false
		}
		loopback = addr.Unmap().IsLoopback()
	} else {
		host, port, _ = strings.Cut(a, ":")
		if !isHostName(host) {
			return false, false
		}
		loopback = asciiLower(host) == "localhost" || isLoopbackQuad(host)
	}
	return loopback, isPort(port)
}

// isHostName: dot-separated labels of [A-Za-z0-9-_] or non-ASCII letters / digits / marks, none empty but a trailing one.
func isHostName(h string) bool {
	if h == "" {
		return false
	}
	labels := strings.Split(strings.TrimSuffix(h, "."), ".")
	for _, l := range labels {
		if l == "" {
			return false
		}
		for _, r := range l {
			switch {
			case r < 0x80:
				if !(r >= 'a' && r <= 'z' || r >= 'A' && r <= 'Z' || r >= '0' && r <= '9' || r == '-' || r == '_') {
					return false
				}
			case !unicode.IsLetter(r) && !unicode.IsDigit(r) && !unicode.IsMark(r):
				return false
			}
		}
	}
	return true
}

// isLoopbackQuad: a canonical dotted-quad IPv4 address in 127/8 ("127.1", "0x7f.1", "127.000.0.1" are names to Uri).
func isLoopbackQuad(h string) bool {
	addr, err := netip.ParseAddr(h) // Go accepts only canonical dotted decimal, no leading zeros
	return err == nil && addr.Is4() && addr.IsLoopback()
}

func isPort(p string) bool {
	if p == "" {
		return true // "host:" = the default port
	}
	for i := 0; i < len(p); i++ {
		if p[i] < '0' || p[i] > '9' {
			return false
		}
	}
	p = strings.TrimLeft(p, "0")
	if len(p) > 5 {
		return false
	}
	n := 0
	for i := 0; i < len(p); i++ {
		n = n*10 + int(p[i]-'0')
	}
	return n <= 65535
}

// decodeUnreserved decodes each %XX whose byte is unreserved (RFC 3986 ALPHA / DIGIT / - . _ ~) and keeps the others;
// false for a '%' not followed by two hex digits.
func decodeUnreserved(s string) (string, bool) {
	if strings.IndexByte(s, '%') < 0 {
		return s, true
	}
	b := make([]byte, 0, len(s))
	for i := 0; i < len(s); i++ {
		if s[i] != '%' {
			b = append(b, s[i])
			continue
		}
		if i+2 >= len(s) || !isHex(s[i+1]) || !isHex(s[i+2]) {
			return s, false
		}
		c := unhex(s[i+1])<<4 | unhex(s[i+2])
		if c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-' || c == '.' || c == '_' || c == '~' {
			b = append(b, c)
		} else {
			b = append(b, s[i:i+3]...)
		}
		i += 2
	}
	return string(b), true
}

// isPairCode is AuthCrypto.TryNormalizePairCode (AuthCrypto.cs:45-69) on a Uri fragment: '-' dropped, ASCII letters
// folded, exactly 26 of [A-Z2-7] (a space never reaches it unescaped).
func isPairCode(f string) bool {
	n := 0
	for i := 0; i < len(f); i++ {
		c := f[i]
		switch {
		case c == '-':
			continue
		case c >= 'a' && c <= 'z', c >= 'A' && c <= 'Z', c >= '2' && c <= '7':
			n++
		default:
			return false
		}
	}
	return n == 26
}

func isHex(c byte) bool { return c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F' }

func unhex(c byte) byte {
	switch {
	case c <= '9':
		return c - '0'
	case c <= 'F':
		return c - 'A' + 10
	default:
		return c - 'a' + 10
	}
}

// asciiLower folds A-Z only (Uri's scheme and host comparisons; a Unicode fold would let U+212A match 'k').
func asciiLower(s string) string {
	b := []byte(s)
	for i, c := range b {
		if c >= 'A' && c <= 'Z' {
			b[i] = c + 32
		}
	}
	return string(b)
}
