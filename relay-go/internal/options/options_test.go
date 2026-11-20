package options

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

const host = "relay.test:443"

// token is a well-formed hosted admin token (32 bytes, base64url) — the conformance suite's Cli.Token.
var token = strings.Repeat("A", 43)

func tokenFile(t *testing.T, content string) string {
	t.Helper()
	f := filepath.Join(t.TempDir(), "token")
	if err := os.WriteFile(f, []byte(content), 0o600); err != nil {
		t.Fatal(err)
	}
	return f
}

// The rows of the conformance suite's CliTests / CliProxyTests / CliHostedTests (Program.cs TryParse + RelayOptions.Validate).
func TestRefusedCommandLines(t *testing.T) {
	file := tokenFile(t, token)
	const mcp = "https://relay.test/mcp"
	rows := []struct {
		why  string
		args []string
	}{
		{"no --public-host", nil},
		{"an upper-case public host", []string{"--public-host", "Relay.Test:443"}},
		{"a public host port out of range", []string{"--public-host", "relay.test:65536"}},
		{"a public host port 0", []string{"--public-host", "relay.test:0"}},
		{"an unknown option", []string{"--public-host", host, "--no-such-option", "1"}},
		{"an option without its value", []string{"--public-host"}},
		{"a negative quota", []string{"--public-host", host, "--bytes-per-day", "-1"}},
		{"a signed quota", []string{"--public-host", host, "--bytes-per-day", "+1"}},
		{"a zero daily stream quota", []string{"--public-host", host, "--streams-per-day", "0"}},
		{"a zero daily byte quota", []string{"--public-host", host, "--bytes-per-day", "0"}},
		{"a stream quota over int32", []string{"--public-host", host, "--streams-per-day", "2147483648"}},
		{"an internal port equal to the public port", []string{"--public-host", host, "--internal-port", "8080", "--port", "8080"}},
		{"an internal port 0", []string{"--public-host", host, "--internal-port", "0"}},
		{"a hosting cap below one", []string{"--public-host", host, "--max-hosting", "0"}},
		{"a port over 65535", []string{"--public-host", host, "--port", "65536"}},
		{"a listen address that is no address", []string{"--public-host", host, "--listen", "localhost"}},
		{"a trusted proxy written non-canonically (octal)", []string{"--public-host", host, "--trusted-proxy", "010.0.0.1"}},
		{"a trusted proxy written short (127.1)", []string{"--public-host", host, "--trusted-proxy", "127.1"}},
		{"a trusted proxy with host bits set", []string{"--public-host", host, "--trusted-proxy", "10.0.0.1/8"}},
		{"a trusted proxy /0", []string{"--public-host", host, "--trusted-proxy", "0.0.0.0/0"}},
		{"a trusted proxy ::/0", []string{"--public-host", host, "--trusted-proxy", "::/0"}},
		{"an IPv4-mapped trusted proxy", []string{"--public-host", host, "--trusted-proxy", "::ffff:203.0.113.7"}},
		{"a client IP header with no proxy to trust", []string{"--public-host", host, "--client-ip-header", "CF-Connecting-IP"}},
		{"a client IP header that is no header name", []string{"--public-host", host, "--trusted-proxy", "127.0.0.1", "--client-ip-header", "X Bad"}},
		{"one hosted option without the other two", []string{"--public-host", host, "--hosted-mcp-url", mcp}},
		{"no token file", []string{"--public-host", host, "--hosted-admin-url", "http://127.0.0.1:47822", "--hosted-mcp-url", mcp}},
		{"an admin URL off loopback", []string{"--public-host", host, "--hosted-admin-url", "http://admin.example.com:47822", "--hosted-admin-token-file", file, "--hosted-mcp-url", mcp}},
		{"an https admin URL", []string{"--public-host", host, "--hosted-admin-url", "https://127.0.0.1:47822", "--hosted-admin-token-file", file, "--hosted-mcp-url", mcp}},
		{"an admin URL with a path", []string{"--public-host", host, "--hosted-admin-url", "http://127.0.0.1:47822/admin", "--hosted-admin-token-file", file, "--hosted-mcp-url", mcp}},
		{"an admin URL with a query", []string{"--public-host", host, "--hosted-admin-url", "http://127.0.0.1:47822/?a=1", "--hosted-admin-token-file", file, "--hosted-mcp-url", mcp}},
		{"an admin URL with user info", []string{"--public-host", host, "--hosted-admin-url", "http://u:p@127.0.0.1:47822", "--hosted-admin-token-file", file, "--hosted-mcp-url", mcp}},
		{"an http MCP URL off loopback", []string{"--public-host", host, "--hosted-admin-url", "http://127.0.0.1:47822", "--hosted-admin-token-file", file, "--hosted-mcp-url", "http://relay.test/mcp"}},
		{"an MCP URL on another host", []string{"--public-host", host, "--hosted-admin-url", "http://127.0.0.1:47822", "--hosted-admin-token-file", file, "--hosted-mcp-url", "https://other.test/mcp"}},
		{"an empty MCP URL alone (given, so the hosted set is checked)", []string{"--public-host", host, "--hosted-mcp-url", ""}},
		{"a blank token file alone", []string{"--public-host", host, "--hosted-admin-token-file", tokenFile(t, " \r\n")}},
		{"a token file that does not exist", []string{"--public-host", host, "--hosted-admin-url", "http://127.0.0.1:47822", "--hosted-admin-token-file", filepath.Join(t.TempDir(), "none"), "--hosted-mcp-url", mcp}},
	}
	for _, r := range rows {
		t.Run(r.why, func(t *testing.T) {
			if o, err := Parse(r.args); err == nil {
				t.Fatalf("accepted: %+v", o)
			}
		})
	}
}

func TestAcceptedCommandLines(t *testing.T) {
	file := tokenFile(t, token+"\r\n") // .NET Trim(): surrounding whitespace is not part of the token
	rows := []struct {
		why  string
		args []string
	}{
		{"the public host alone", []string{"--public-host", host}},
		{"no port in the public host", []string{"--public-host", "relay.test"}},
		{"the suite's launch line", []string{"--public-host", "r0123abcd.relay.test:443", "--listen", "127.0.0.1", "--port", "0"}},
		{"IPv6 listen", []string{"--public-host", host, "--listen", "::1"}},
		{"a trusted proxy address and network", []string{"--public-host", host, "--trusted-proxy", "127.0.0.1", "--trusted-proxy", "10.0.0.0/8", "--trusted-proxy", "2001:db8::/32"}},
		{"a client IP header with a proxy", []string{"--public-host", host, "--trusted-proxy", "127.0.0.1", "--client-ip-header", "CF-Connecting-IP"}},
		{"a zero noise budget", []string{"--public-host", host, "--noise-per-ip-per-hour", "0"}},
		{"leading zeros", []string{"--public-host", host, "--port", "08080"}},
		{"internal port", []string{"--public-host", host, "--internal-port", "9000"}},
		{"a well-formed hosted set", []string{"--public-host", host, "--hosted-admin-url", "http://127.0.0.1:47822", "--hosted-admin-token-file", file, "--hosted-mcp-url", "https://relay.test/mcp"}},
		{"a token file with a UTF-8 BOM (File.ReadAllText drops it)", []string{"--public-host", host, "--hosted-admin-url", "http://127.0.0.1:47822", "--hosted-admin-token-file", tokenFile(t, "\xef\xbb\xbf"+token+"\n"), "--hosted-mcp-url", "https://relay.test/mcp"}},
		{"a hosted set on localhost with an http loopback MCP URL", []string{"--public-host", "localhost:8443", "--hosted-admin-url", "http://localhost:47822/", "--hosted-admin-token-file", file, "--hosted-mcp-url", "http://localhost/mcp"}},
	}
	for _, r := range rows {
		t.Run(r.why, func(t *testing.T) {
			if _, err := Parse(r.args); err != nil {
				t.Fatalf("refused: %v", err)
			}
		})
	}
}

func TestParsedValues(t *testing.T) {
	file := tokenFile(t, token)
	o, err := Parse([]string{"--public-host", host, "--port", "9", "--bytes-per-day", "5", "--streams-per-day", "6",
		"--noise-per-ip-per-hour", "7", "--max-hosting", "3", "--internal-port", "10", "--trusted-proxy", "10.0.0.0/8",
		"--client-ip-header", "CF-Connecting-IP", "--hosted-admin-url", "http://127.0.0.1:47822",
		"--hosted-admin-token-file", file, "--hosted-mcp-url", "https://relay.test/mcp"})
	if err != nil {
		t.Fatal(err)
	}
	if o.PublicHost != host || o.Port != 9 || o.BytesPerDay != 5 || o.StreamsPerDay != 6 || o.NoisePerIPPerHour != 7 ||
		o.MaxHosting != 3 || o.InternalPort == nil || *o.InternalPort != 10 || len(o.TrustedProxies) != 1 ||
		o.ClientIPHeader != "CF-Connecting-IP" || o.HostedAdminToken != token || o.HostedAdminURL == nil ||
		o.HostedMCPURL != "https://relay.test/mcp" || o.ListenAddress.String() != "127.0.0.1" {
		t.Fatalf("parsed %+v", o)
	}
	d, err := Parse([]string{"--public-host", host})
	if err != nil {
		t.Fatal(err)
	}
	if d.Port != 8080 || d.BytesPerDay != 1<<30 || d.StreamsPerDay != 2000 || d.RegistrationsPerIPPerHour != 600 ||
		d.RefusedDialsPerIPPerHour != 600 || d.NoisePerIPPerHour != 600 || d.MaxFrameBytes != 512*1024 ||
		d.ClientIPHeader != "X-Forwarded-For" || d.MaxHosting != 32 || !d.ListenAddress.IsLoopback() {
		t.Fatalf("defaults %+v", d)
	}
}

// A refusal never prints the token file's content (conformance A_hosted_token_file_that_holds_no_token...).
func TestRefusalNeverEchoesTheTokenFile(t *testing.T) {
	marker := "secret-0123456789abcdef"
	file := tokenFile(t, marker)
	_, err := Parse([]string{"--public-host", host, "--hosted-admin-url", "http://127.0.0.1:47822",
		"--hosted-admin-token-file", file, "--hosted-mcp-url", "https://relay.test/mcp"})
	if err == nil || strings.Contains(err.Error(), marker) {
		t.Fatalf("err = %v", err)
	}
	_, err = Parse([]string{"--public-host", host, "--bytes-per-day", marker})
	if err == nil || strings.Contains(err.Error(), marker) {
		t.Fatalf("a bad value was echoed: %v", err)
	}
}
