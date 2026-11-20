package hosted

import (
	"bufio"
	"os"
	"strconv"
	"strings"
	"testing"
)

// unescape decodes the escapes the probe's Regex.Unescape decoded in testdata/connection-codes.txt.
func unescape(t *testing.T, s string) string {
	t.Helper()
	var b strings.Builder
	for i := 0; i < len(s); i++ {
		if s[i] != '\\' || i+1 == len(s) {
			b.WriteByte(s[i])
			continue
		}
		i++
		switch s[i] {
		case 't':
			b.WriteByte('\t')
		case 'n':
			b.WriteByte('\n')
		case 'r':
			b.WriteByte('\r')
		case '\\':
			b.WriteByte('\\')
		case 'u':
			v, err := strconv.ParseUint(s[i+1:i+5], 16, 16)
			if err != nil {
				t.Fatalf("bad escape in %q", s)
			}
			b.WriteRune(rune(v))
			i += 4
		default:
			t.Fatalf("unknown escape \\%c in %q", s[i], s)
		}
	}
	return b.String()
}

// The C# relay's ConnectionCode.TryParse, row for row (MEASURED, see the file's header): the relay forwards a
// RELAY_HOST only for a code that parses and names the socket's own relayId (HostedAdmin.cs:52-60).
func TestParseCodeAsCsharp(t *testing.T) {
	f, err := os.Open("testdata/connection-codes.txt")
	if err != nil {
		t.Fatal(err)
	}
	defer f.Close()
	rows := 0
	sc := bufio.NewScanner(f)
	for sc.Scan() {
		line := sc.Text()
		if line == "" || line[0] == '#' {
			continue
		}
		cols := strings.SplitN(line, "\t", 3)
		if len(cols) != 3 {
			t.Fatalf("bad row %q", line)
		}
		rows++
		input := unescape(t, cols[2])
		id, ok := ParseCode(input)
		if want := cols[0] == "OK"; ok != want || (ok && id != cols[1]) {
			t.Errorf("ParseCode(%q) = %q, %v; C# %s %q", cols[2], id, ok, cols[0], cols[1])
		}
	}
	if err := sc.Err(); err != nil {
		t.Fatal(err)
	}
	if rows != 149 {
		t.Fatalf("%d rows, want the 149 measured", rows)
	}
}
