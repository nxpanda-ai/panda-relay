package server

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// The page must be HelpPage.Body byte for byte: the C# raw literal's lines with its 8-space indent removed, LF, no
// trailing newline (the conformance suite only checks that every id gets the same bytes).
func TestHelpBodyIsTheCsharpPage(t *testing.T) {
	src, err := os.ReadFile(filepath.Join("..", "..", "..", "bridge", "src", "Panda.Relay", "HelpPage.cs"))
	if err != nil {
		t.Fatal(err)
	}
	text := strings.ReplaceAll(string(src), "\r\n", "\n")
	start := strings.Index(text, "GetBytes(\"\"\"\n")
	end := strings.Index(text, "\n        \"\"\");")
	if start < 0 || end < 0 {
		t.Fatal("HelpPage.cs: the raw literal was not found")
	}
	lines := strings.Split(text[start+len("GetBytes(\"\"\"\n"):end], "\n")
	for i, l := range lines {
		lines[i] = strings.TrimPrefix(l, "        ")
	}
	if want := strings.Join(lines, "\n"); want != helpBody {
		t.Fatalf("helpBody differs from HelpPage.cs (%d vs %d bytes)", len(helpBody), len(want))
	}
}
