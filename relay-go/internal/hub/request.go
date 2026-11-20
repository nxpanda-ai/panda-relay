package hub

import (
	"encoding/base64"
	"net/http"
	"strings"
	"unicode/utf8"
)

const extensionOriginPrefix = "chrome-extension://"

// isWebSocketRequest is ASP.NET's HttpContext.WebSockets.IsWebSocketRequest for HTTP/1.1: a GET whose Connection
// carries the "upgrade" token, Upgrade is "websocket", Sec-WebSocket-Version is 13 and Sec-WebSocket-Key is base64 of
// 16 bytes. Anything else gets 400 from the relay (RelayHub.cs:100-104) — never gorilla's own refusal.
func isWebSocketRequest(r *http.Request) bool {
	if r.Method != http.MethodGet || r.ProtoMajor != 1 {
		return false
	}
	if !hasToken(r.Header.Values("Connection"), "upgrade") || !strings.EqualFold(single(r.Header, "Upgrade"), "websocket") {
		return false
	}
	if single(r.Header, "Sec-WebSocket-Version") != "13" {
		return false
	}
	key, err := base64.StdEncoding.DecodeString(single(r.Header, "Sec-WebSocket-Key"))
	return err == nil && len(key) == 16
}

// single is a header's one value ("" when absent or repeated).
func single(h http.Header, name string) string {
	if v := h.Values(name); len(v) == 1 {
		return strings.TrimSpace(v[0])
	}
	return ""
}

func hasToken(values []string, token string) bool {
	for _, v := range values {
		for _, t := range strings.Split(v, ",") {
			if strings.EqualFold(strings.TrimSpace(t), token) {
				return true
			}
		}
	}
	return false
}

// fromExtension (RelayHub.cs:867-877): only an extension opens /v1/browser or accepts a stream; a bridge sends no
// Origin. One Origin, chrome-extension:// + a 32-letter id (a–p).
func fromExtension(r *http.Request) bool {
	origin := r.Header.Values("Origin")
	if len(origin) != 1 || len(origin[0]) != 51 || !strings.HasPrefix(origin[0], extensionOriginPrefix) {
		return false
	}
	for _, c := range origin[0][len(extensionOriginPrefix):] {
		if c < 'a' || c > 'p' {
			return false
		}
	}
	return true
}

// query is ASP.NET's IQueryCollection: keys compared case-insensitively, each with its values in order.
type query map[string][]string

// parseQuery is QueryHelpers.ParseQuery: pairs split on '&', empty pairs skipped, a name without '=' has the value "",
// '+' is a space and %XX is decoded where it is valid (an invalid escape stays as written).
func parseQuery(raw string) query {
	q := query{}
	for _, pair := range strings.Split(raw, "&") {
		if pair == "" {
			continue
		}
		name, value, _ := strings.Cut(pair, "=")
		key := asciiLower(unescape(name))
		q[key] = append(q[key], unescape(value))
	}
	return q
}

func (q query) has(name string) bool { _, ok := q[name]; return ok }

// HasQueryKey: the raw query names key (ASP.NET Query.ContainsKey: decoded, case-insensitive).
func HasQueryKey(raw, key string) bool { return parseQuery(raw).has(asciiLower(key)) }

// asciiLower folds A-Z only: .NET OrdinalIgnoreCase never matches 'İ' to 'i' or the Kelvin sign to 'k'.
func asciiLower(s string) string {
	b := []byte(s)
	for i, c := range b {
		if c >= 'A' && c <= 'Z' {
			b[i] = c + 'a' - 'A'
		}
	}
	return string(b)
}

// single is the value of a name given exactly once, else nil (RelayHub.Single).
func (q query) single(name string) *string {
	if v := q[name]; len(v) == 1 {
		return &v[0]
	}
	return nil
}

func unescape(s string) string {
	if !strings.ContainsAny(s, "+%") {
		return s
	}
	b := make([]byte, 0, len(s))
	for i := 0; i < len(s); i++ {
		switch c := s[i]; {
		case c == '+':
			b = append(b, ' ')
		case c == '%' && i+2 < len(s) && isHex(s[i+1]) && isHex(s[i+2]):
			b = append(b, unhex(s[i+1])<<4|unhex(s[i+2]))
			i += 2
		default:
			b = append(b, c)
		}
	}
	if !utf8.Valid(b) {
		// Uri.UnescapeDataString leaves an escape that is not UTF-8 as written; such a value is never a well-formed
		// relayId / streamId / kind, so keeping the whole text escaped gives the same answer
		return strings.ReplaceAll(s, "+", " ")
	}
	return string(b)
}

func isHex(c byte) bool { return c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F' }

func unhex(c byte) byte {
	switch {
	case c >= 'a':
		return c - 'a' + 10
	case c >= 'A':
		return c - 'A' + 10
	default:
		return c - '0'
	}
}
