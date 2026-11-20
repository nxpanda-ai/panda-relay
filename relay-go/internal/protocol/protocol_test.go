package protocol

import (
	"bytes"
	"encoding/json"
	"errors"
	"go/format"
	"io/fs"
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"
)

// Fixtures shared with the C# and TS parsers (bridge/tests/Panda.Bridge.Tests/RelayProtocolTests.cs,
// extension/test/protocol.test.ts): the Go reader must accept and refuse exactly the same texts (plan-relay-go S2).
func fixture(t *testing.T, parts ...string) []byte {
	t.Helper()
	b, err := os.ReadFile(filepath.Join(append([]string{"..", "..", "..", "schema", "fixtures"}, parts...)...))
	if err != nil {
		t.Fatal(err)
	}
	return b
}

func rawList(t *testing.T, name string) []json.RawMessage {
	t.Helper()
	var list []json.RawMessage
	if err := json.Unmarshal(fixture(t, name), &list); err != nil {
		t.Fatal(err)
	}
	return list
}

func sameJSON(t *testing.T, a, b []byte) bool {
	t.Helper()
	var x, y any
	if json.Unmarshal(a, &x) != nil || json.Unmarshal(b, &y) != nil {
		return false
	}
	return reflect.DeepEqual(x, y)
}

func mustOk(t *testing.T, text []byte) RelayMessage {
	t.Helper()
	r := ParseRelay(text)
	if r.Kind != Ok {
		t.Fatalf("kind %d (%s %s) for %s", r.Kind, r.Type, r.Reason, text)
	}
	return r.Message
}

func TestValidFixturesRoundTripToAnEqualObject(t *testing.T) {
	for _, raw := range rawList(t, "relay-valid.json") {
		out, err := MarshalRelay(mustOk(t, raw))
		if err != nil {
			t.Fatal(err)
		}
		if !sameJSON(t, raw, out) {
			t.Errorf("round trip changed\n in: %s\nout: %s", raw, out)
		}
	}
}

func TestParsesEveryTypeScriptAndCsharpWrittenMessageToTheFixtureObject(t *testing.T) {
	valid := rawList(t, "relay-valid.json")
	for _, golden := range []string{"relay-ts.json", "relay-cs.json"} {
		var texts []string
		if err := json.Unmarshal(fixture(t, "golden", golden), &texts); err != nil {
			t.Fatal(err)
		}
		if len(texts) != len(valid) {
			t.Fatalf("%s: %d texts, %d fixtures", golden, len(texts), len(valid))
		}
		for i, text := range texts {
			out, err := MarshalRelay(mustOk(t, []byte(text)))
			if err != nil {
				t.Fatal(err)
			}
			if !sameJSON(t, valid[i], out) {
				t.Errorf("%s[%d]: %s", golden, i, text)
			}
		}
	}
}

func TestInvalidFixturesAreRefused(t *testing.T) {
	var cases []struct{ Why, JSON string }
	if err := json.Unmarshal(fixture(t, "relay-invalid.json"), &cases); err != nil {
		t.Fatal(err)
	}
	if len(cases) == 0 {
		t.Fatal("no cases")
	}
	for _, c := range cases {
		if r := ParseRelay([]byte(c.JSON)); r.Kind == Ok {
			t.Errorf("accepted (%s): %s", c.Why, c.JSON)
		}
	}
}

// exportMarker is written at the root of the open-source relay repo by the upstream exporter
// (oss/relay/export.ps1), which leaves the bridge-channel fixtures home (plan-oss-relay-go AC1).
const exportMarker = ".panda-relay-export"

// readOtherChannel reads a bridge-channel fixture under root/schema/fixtures. A missing one is a skip (with the reason)
// only in the open-source copy (root holds the export marker); upstream it is an error.
func readOtherChannel(root, name string) (b []byte, skip string, err error) {
	b, err = os.ReadFile(filepath.Join(root, "schema", "fixtures", name))
	if err == nil || !errors.Is(err, fs.ErrNotExist) {
		return b, "", err
	}
	if _, merr := os.Stat(filepath.Join(root, exportMarker)); merr == nil {
		return nil, name + " is a bridge-channel fixture, not in the open-source relay repo", nil
	}
	return nil, "", err
}

func TestNoMessageOfAnotherChannelIsARelayMessage(t *testing.T) {
	for _, name := range []string{"valid.json", "native-valid.json"} {
		b, skip, err := readOtherChannel(filepath.Join("..", "..", ".."), name)
		if err != nil {
			t.Fatal(err)
		}
		if skip != "" {
			t.Skip(skip)
		}
		var list []json.RawMessage
		if err := json.Unmarshal(b, &list); err != nil {
			t.Fatal(err)
		}
		for _, raw := range list {
			if r := ParseRelay(raw); r.Kind == Ok {
				t.Errorf("%s: accepted %s", name, raw)
			}
		}
	}
}

func TestReadOtherChannelSkipsOnlyInTheExport(t *testing.T) {
	// upstream (no marker): a missing bridge fixture is an error, never a quiet skip
	root := t.TempDir()
	if _, skip, err := readOtherChannel(root, "valid.json"); err == nil || skip != "" {
		t.Errorf("missing without the marker: skip %q, err %v; want an error", skip, err)
	}
	// the open-source copy (marker at the root): the same missing fixture skips, saying why
	if err := os.WriteFile(filepath.Join(root, exportMarker), []byte("x"), 0o600); err != nil {
		t.Fatal(err)
	}
	if _, skip, err := readOtherChannel(root, "valid.json"); err != nil || skip == "" {
		t.Errorf("missing with the marker: skip %q, err %v; want a skip", skip, err)
	}
	// a present fixture is read in both cases
	dir := filepath.Join(root, "schema", "fixtures")
	if err := os.MkdirAll(dir, 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, "valid.json"), []byte("[]"), 0o600); err != nil {
		t.Fatal(err)
	}
	if b, skip, err := readOtherChannel(root, "valid.json"); err != nil || skip != "" || string(b) != "[]" {
		t.Errorf("present: %q, skip %q, err %v", b, skip, err)
	}
}

func TestEveryRelayMessageTypeHasAValidFixture(t *testing.T) {
	covered := map[string]bool{}
	for _, raw := range rawList(t, "relay-valid.json") {
		covered[mustOk(t, raw).RelayType()] = true
	}
	for typ := range relayChecks {
		if !covered[typ] {
			t.Errorf("%s has no fixture", typ)
		}
	}
}

// The reader's own rules, beyond the shared fixtures: each row is a variant of a valid RELAY_PING / RELAY_INCOMING.
func TestStrictReader(t *testing.T) {
	const incoming = `{"type":"RELAY_INCOMING","streamId":"yMnKy8zNzs_Q0dLT1NXW1w","kind":"pair"}`
	mustOk(t, []byte(incoming))
	refused := map[string]string{
		"null member":                `{"type":"RELAY_INCOMING","streamId":null,"kind":"pair"}`,
		"unknown member":             `{"type":"RELAY_INCOMING","streamId":"yMnKy8zNzs_Q0dLT1NXW1w","kind":"pair","x":1}`,
		"case of a member name":      `{"type":"RELAY_INCOMING","StreamId":"yMnKy8zNzs_Q0dLT1NXW1w","kind":"pair"}`,
		"enum value case":            `{"type":"RELAY_INCOMING","streamId":"yMnKy8zNzs_Q0dLT1NXW1w","kind":"Pair"}`,
		"trailing data":              incoming + `x`,
		"two values":                 incoming + incoming,
		"lone high surrogate":        `{"type":"RELAY_INCOMING","streamId":"\ud800","kind":"pair"}`,
		"lone low surrogate":         `{"type":"RELAY_INCOMING","streamId":"\udc00","kind":"pair"}`,
		"invalid UTF-8":              "{\"type\":\"RELAY_INCOMING\",\"streamId\":\"\xff\",\"kind\":\"pair\"}",
		"raw control character":      "{\"type\":\"RELAY_INCOMING\",\"streamId\":\"a\tb\",\"kind\":\"pair\"}",
		"type not a string":          `{"type":1}`,
		"array root":                 `[]`,
		"empty":                      ``,
		"leading zero":               `{"type":"RELAY_INCOMING","streamId":01}`,
		"duplicate, last one wrong":  `{"type":"RELAY_INCOMING","streamId":"yMnKy8zNzs_Q0dLT1NXW1w","kind":"pair","kind":"nope"}`,
		"null in an unknown message": `{"type":"RELAY_INCOMING","streamId":"yMnKy8zNzs_Q0dLT1NXW1w","kind":"pair","x":[null]}`,
	}
	for why, text := range refused {
		if r := ParseRelay([]byte(text)); r.Kind != Invalid {
			t.Errorf("%s: kind %d for %s", why, r.Kind, text)
		}
	}
	// whitespace around the value is fine
	mustOk(t, []byte(" \r\n\t"+incoming+" \n"))
	if r := ParseRelay([]byte(`{"type":"NOT_RELAY","x":null}`)); r.Kind != Unknown || r.Type != "NOT_RELAY" {
		t.Errorf("unknown type: %+v", r)
	}
	deep := strings.Repeat("[", 64) + strings.Repeat("]", 64)
	if r := ParseRelay([]byte(`{"type":"NOT_RELAY","x":` + deep + `}`)); r.Kind != Invalid {
		t.Errorf("depth 65 accepted: %+v", r)
	}
	ok := strings.Repeat("[", 63) + strings.Repeat("]", 63)
	if r := ParseRelay([]byte(`{"type":"NOT_RELAY","x":` + ok + `}`)); r.Kind != Unknown {
		t.Errorf("depth 64 refused: %+v", r)
	}
}

// Repeated member names: each row's answer was measured on the C# relay's parser (ProtocolJson.ParseRelay, a probe
// console 2026-11-20, plan-relay-go T4 critic finding 2) — true = Ok there.
func TestDuplicateMembersAsCsharp(t *testing.T) {
	const s = "yMnKy8zNzs_Q0dLT1NXW1w"
	for text, ok := range map[string]bool{
		`{"type":"RELAY_INCOMING","streamId":"` + s + `","kind":"nope","kind":"pair"}`:           false,
		`{"type":"RELAY_INCOMING","streamId":"` + s + `","kind":"pair","kind":"pair"}`:           true,
		`{"type":"RELAY_INCOMING","streamId":"short","streamId":"` + s + `","kind":"pair"}`:      true,
		`{"type":"RELAY_INCOMING","streamId":"` + s + `","streamId":"short","kind":"pair"}`:      false,
		`{"type":"RELAY_TOKENS_OK","seq":null,"seq":1}`:                                          false,
		`{"type":"RELAY_TOKENS_OK","seq":"x","seq":1}`:                                           false,
		`{"type":"RELAY_TOKENS_OK","seq":0,"seq":1}`:                                             true,
		`{"type":"RELAY_TOKENS_OK","seq":1,"seq":2}`:                                             true,
		`{"type":"RELAY_TOKENS_OK","type":"RELAY_PING","seq":1}`:                                 false,
		`{"type":"RELAY_PING","type":"RELAY_TOKENS_OK","seq":1}`:                                 true,
		`{"type":"NOPE","type":"RELAY_PING"}`:                                                    true,
		`{"type":"RELAY_PING","type":"NOPE"}`:                                                    false, // Unknown
		`{"type":"RELAY_PING","type":"RELAY_PING"}`:                                              true,
		`{"type":null,"type":"RELAY_PING"}`:                                                      false, // only containsNull refuses this one (critic round 2 N3)
		`{"type":1,"type":"RELAY_PING"}`:                                                         true,
		`{"type":"\ud800","type":"RELAY_PING"}`:                                                  true,
		`{"type":"RELAY_TOKENS_OK","seq":1.5,"seq":1}`:                                           false,
		`{"type":"RELAY_TOKENS_OK","seq":1e0,"seq":1}`:                                           false,
		`{"type":"RELAY_TOKENS_OK","seq":9007199254740993,"seq":1}`:                              true,
		`{"type":"RELAY_TOKENS_OK","seq":99999999999999999999,"seq":1}`:                          false,
		`{"type":"RELAY_TOKENS_OK","seq":1,"seq":9007199254740993}`:                              false,
		`{"type":"RELAY_TOKENS_OK","seq":-5,"seq":1}`:                                            true,
		`{"type":"RELAY_INCOMING","streamId":"\ud800","streamId":"` + s + `","kind":"pair"}`:     false,
		`{"type":"RELAY_INCOMING","streamId":1,"streamId":"` + s + `","kind":"pair"}`:            false,
		`{"type":"RELAY_INCOMING","streamId":"` + s + `","kind":"Pair","kind":"pair"}`:           false,
		`{"type":"RELAY_HOST_RESULT","seq":1,"ok":1,"ok":true}`:                                  false,
		`{"type":"RELAY_HOST_RESULT","seq":1,"ok":true,"error":"busy","error":"nope"}`:           false,
		`{"type":"RELAY_HOST_RESULT","seq":1,"ok":true,"error":"nope","error":"busy"}`:           false,
		`{"type":"RELAY_HOST_RESULT","seq":1,"ok":true,"mcpUrl":"x","mcpUrl":"https://a.b/mcp"}`: true,
		`{"type":"RELAY_PING","x":{"a":1,"a":null}}`:                                             false,
	} {
		if got := ParseRelay([]byte(text)).Kind == Ok; got != ok {
			t.Errorf("Ok=%v, C# says %v: %s", got, ok, text)
		}
	}
}

func TestIntegers(t *testing.T) {
	// RELAY_TOKENS_OK.seq: integer, minimum 1
	seq := func(n string) Result { return ParseRelay([]byte(`{"type":"RELAY_TOKENS_OK","seq":` + n + `}`)) }
	for _, n := range []string{"0", "-1", "1.0", "1e3", "1E3", "9007199254740992", "99999999999999999999", `"1"`, "true", "01", "+1"} {
		if r := seq(n); r.Kind == Ok {
			t.Errorf("%s accepted", n)
		}
	}
	for _, n := range []string{"1", "9007199254740991"} {
		if r := seq(n); r.Kind != Ok {
			t.Errorf("%s refused: %s", n, r.Reason)
		}
	}
}

func TestMarshalRefusesAnInvalidMessage(t *testing.T) {
	if _, err := MarshalRelay(RelayIncoming{StreamId: "short", Kind: RelayStreamKindPair}); err == nil {
		t.Fatal("an invalid streamId was serialized")
	}
	out, err := MarshalRelay(RelayIncoming{StreamId: "yMnKy8zNzs_Q0dLT1NXW1w", Kind: RelayStreamKindPair})
	if err != nil || !bytes.Contains(out, []byte(`"kind":"pair"`)) {
		t.Fatalf("%s %v", out, err)
	}
}

func TestGeneratedFileIsGofmtClean(t *testing.T) {
	src, err := os.ReadFile("protocol.g.go")
	if err != nil {
		t.Fatal(err)
	}
	formatted, err := format.Source(src)
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(src, formatted) {
		t.Fatal("protocol.g.go is not gofmt-clean: fix emitGo in schema/codegen.mjs")
	}
}
