package server

import (
	"net/http"
	"strconv"
	"strings"

	"github.com/nxpanda-ai/panda-relay/relay-go/internal/auth"
)

// HelpPage.cs (plan-https-code T2, AC4): what a browser shows for the connection code's link form
// https://<relay>/c/<relayId>#<code>. A constant page: no script can run (CSP), the request is never echoed (no relayId,
// no query), nothing is logged. The #code never reaches here — a browser does not send the fragment.
const helpPrefix = "/c/"

const contentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'"

// helpBody is HelpPage.Body, byte for byte (help_test.go checks it against HelpPage.cs).
const helpBody = `<!doctype html>
<html lang="vi">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="robots" content="noindex">
<title>Panda — mã kết nối / connection code</title>
<style>
body { font: 16px/1.55 system-ui, sans-serif; max-width: 40rem; margin: 2rem auto; padding: 0 1rem; color: #1d2433; background: #fff; }
h1 { font-size: 1.3rem; } h2 { font-size: 1.1rem; margin-top: 2rem; }
code { background: #f1f3f7; padding: .1rem .35rem; border-radius: 4px; }
.warn { background: #fff4e5; border: 1px solid #f0b46a; padding: .75rem 1rem; border-radius: 8px; }
@media (prefers-color-scheme: dark) { body { color: #e6e9ef; background: #14171d; } code { background: #262b35; } .warn { background: #3a2a14; border-color: #8a5a20; } }
</style>
</head>
<body>
<section lang="vi">
<h1>Đây là mã kết nối Panda</h1>
<p class="warn">Link này dùng để <b>dán vào panda-browser</b>, không phải để mở trong trình duyệt. Đừng gửi tiếp cho người khác: ai dùng nó trước trong 5 phút sẽ điều khiển được Chrome đã tạo ra nó.</p>
<p>Copy toàn bộ địa chỉ trên thanh địa chỉ (gồm cả phần sau dấu <code>#</code>), rồi chạy trên máy có Panda:</p>
<p><code>panda-browser connect "&lt;địa chỉ vừa copy&gt;"</code></p>
<p>Cần panda-browser 0.1.15 trở lên. Bản cũ hơn không nhận dạng link này: hãy xin người gửi mã dạng <code>panda-relay://</code>, hoặc cập nhật Panda.</p>
</section>
<section lang="en">
<h2>This is a Panda connection code</h2>
<p class="warn">This link is meant to be <b>pasted into panda-browser</b>, not opened in a browser. Do not pass it on: whoever uses it first within 5 minutes controls the Chrome that made it.</p>
<p>Copy the whole address from the address bar (including the part after <code>#</code>), then run on the computer with Panda:</p>
<p><code>panda-browser connect "&lt;the copied address&gt;"</code></p>
<p>Needs panda-browser 0.1.15 or newer. An older one does not take this link: ask the sender for the <code>panda-relay://</code> code, or update Panda.</p>
</section>
</body>
</html>`

// serveHelp is HelpPage.ServeAsync for a GET / HEAD under /c/: the page for a well-formed id (exact, case-sensitive
// path), else 404. HEAD gets the GET's headers and Content-Length, no body.
func serveHelp(w http.ResponseWriter, r *http.Request, path string) {
	id, ok := strings.CutPrefix(path, helpPrefix)
	if !ok || !auth.IsRelayID(id) {
		w.WriteHeader(http.StatusNotFound)
		return
	}
	h := w.Header()
	h.Set("Content-Type", "text/html; charset=utf-8")
	h.Set("Content-Length", strconv.Itoa(len(helpBody)))
	h.Set("Content-Security-Policy", contentSecurityPolicy)
	h.Set("Referrer-Policy", "no-referrer")
	h.Set("X-Robots-Tag", "noindex")
	h.Set("X-Content-Type-Options", "nosniff")
	h.Set("Cache-Control", "public, max-age=3600") // the same bytes for every id (D4)
	w.WriteHeader(http.StatusOK)
	if r.Method != http.MethodHead {
		_, _ = w.Write([]byte(helpBody))
	}
}
