package hub

import (
	"crypto/rand"
	"net/http"
	"net/netip"
	"sync"
	"sync/atomic"

	"github.com/nxpanda-ai/panda-relay/relay-go/internal/auth"
	"github.com/nxpanda-ai/panda-relay/relay-go/internal/logx"
	"github.com/nxpanda-ai/panda-relay/relay-go/internal/protocol"
	"github.com/nxpanda-ai/panda-relay/relay-go/internal/quotas"
	"github.com/nxpanda-ai/panda-relay/relay-go/internal/socket"
)

type streamState int

const (
	waiting streamState = iota
	open
	closed
)

// relayStream is RelayStream (RelayHub.cs:38-55): a dialer and, once accepted, the extension's socket. State changes
// under the hub's lock.
type relayStream struct {
	id     string
	owner  *browserLink
	kind   protocol.RelayStreamKind
	dialer *socket.Socket
	ext    atomic.Pointer[socket.Socket] // set under the hub's lock; read by closes outside it
	state  streamState                   // under Hub.mu
	up     int64                         // under Hub.mu
	down   int64                         // under Hub.mu

	wakeOnce sync.Once
	woken    chan struct{} // Accepted: the extension accepted, or the stream closed first (RunAdmitted re-reads state)
	doneOnce sync.Once
	done     chan struct{} // the dialer's handler finished both directions (the acceptor's request waits for it)
}

func (s *relayStream) wake()   { s.wakeOnce.Do(func() { close(s.woken) }) }
func (s *relayStream) finish() { s.doneOnce.Do(func() { close(s.done) }) }

// kindName is how the C# logs print a RelayStreamKind (the enum member's name).
func kindName(k protocol.RelayStreamKind) string {
	if k == protocol.RelayStreamKindLink {
		return "Link"
	}
	return "Pair"
}

// ipText is how the C# logs print an IP bucket; nil (the internal listener) prints as LoggerMessage's "(null)".
func ipText(ip *netip.Addr) string {
	if ip == nil {
		return "(null)"
	}
	return ip.String()
}

// RunStream is RunStreamAsync (RelayHub.cs:360-403). internal = the C5 listener: its dials have no IP bucket.
func (h *Hub) RunStream(w http.ResponseWriter, r *http.Request, client netip.Addr, internal bool) {
	h.enter()
	defer h.leave()
	if !isWebSocketRequest(r) {
		w.WriteHeader(http.StatusBadRequest)
		return
	}
	var ip *netip.Addr
	if !internal {
		b := quotas.Bucket(client)
		ip = &b
	}
	q := parseQuery(r.URL.RawQuery)
	dial := q.single("dial")
	var kind *protocol.RelayStreamKind
	if k := q.single("kind"); k != nil && (*k == "link" || *k == "pair") {
		v := protocol.RelayStreamKind(*k)
		kind = &v
	}
	accept := q.single("accept")
	var token *string
	if t := r.Header.Values("X-Panda-Dial"); len(t) == 1 {
		token = &t[0]
	}
	refused := len(r.Header.Values("Origin")) != 0
	if q.has("accept") {
		refused = !fromExtension(r)
	}
	if refused {
		w.WriteHeader(http.StatusForbidden) // before anything is counted
		return
	}
	sock := h.accept(w, r)
	if sock == nil {
		return
	}
	defer sock.Dispose()
	_, acceptOK := auth.DecodeBase64URL(deref(accept), 16)
	owner := q.single("id")
	switch {
	case len(q) == 2 && dial != nil && auth.IsRelayID(*dial) && kind != nil:
		// plan-relay-rename D6: a dial on a renamed relay's old name (the internal listener's Host is loopback)
		if !internal && h.isAliasHost(r.Host) {
			h.countAliasUse(quotas.Dial)
		}
		h.runDial(sock, *dial, *kind, token, ip)
	case accept != nil && acceptOK && (len(q) == 1 || (len(q) == 2 && owner != nil && auth.IsRelayID(*owner))):
		h.runAccept(sock, *accept, owner, ip)
	default:
		h.countNoise(ip) // C6
		closeAndDrain(sock, protocol.CloseProtocolError, "")
	}
}

func deref(s *string) string {
	if s == nil {
		return ""
	}
	return *s
}

func (h *Hub) countNoise(ip *netip.Addr) {
	if ip != nil {
		h.mu.Lock()
		h.quotas.CountNoise(*ip, h.clock.Now())
		h.mu.Unlock()
	}
}

func (h *Hub) runDial(dialer *socket.Socket, relayID string, kind protocol.RelayStreamKind, token *string, ip *netip.Addr) {
	stream, code, retryAfter := h.admit(dialer, relayID, kind, token, ip)
	if stream == nil {
		line, previousHour := quotas.Untracked, 0
		if code == protocol.CloseNotOnline {
			h.mu.Lock()
			line, previousHour = h.notOnline.Count(relayID, string(kind), h.clock.Now())
			h.mu.Unlock()
		}
		switch line {
		case quotas.First:
			logx.Info("dial %s for %s refused %d from %s (logged once an hour; %d in the previous hour)", kindName(kind), relayID, code, ipText(ip), previousHour)
		case quotas.Untracked:
			logx.Info("dial %s for %s refused %d from %s", kindName(kind), relayID, code, ipText(ip)) // every other code, and a 4504 past the table
		}
		r := ""
		if retryAfter > 0 {
			r = reason(retryAfter)
		}
		closeAndDrain(dialer, code, r)
		return
	}
	defer func() {
		h.release(stream)
		stream.finish()
		h.mu.Lock()
		up, down := stream.up, stream.down
		h.mu.Unlock()
		extCode := 0
		if ext := stream.ext.Load(); ext != nil {
			extCode = ext.CloseCode()
		}
		logx.Info("stream %s of %s closed: %d B up, %d B down, dialer %d, extension %d", kindName(kind), relayID, up, down, dialer.CloseCode(), extCode)
	}()
	h.runAdmitted(stream)
}

// admit is the S1 check order: 4530 → 4504 → 4501 (counts on the IP) → 4529 → 4509 → slot reserved
// (RelayHub.cs:456-502). retryAfter > 0 for 4530 / 4529 only.
func (h *Hub) admit(dialer *socket.Socket, relayID string, kind protocol.RelayStreamKind, token *string, ip *netip.Addr) (*relayStream, int, int) {
	now := h.clock.Now()
	h.mu.Lock()
	defer h.mu.Unlock()
	if ip != nil {
		if limited, retry := h.quotas.DialerLimited(*ip, now); limited {
			return nil, protocol.CloseDialerLimited, retry
		}
	}
	link, online := h.online[relayID]
	if !online {
		if ip != nil {
			h.quotas.CountNotOnline(*ip, relayID, now) // C6: once per (IP, relayId) per hour
		}
		return nil, protocol.CloseNotOnline, 0
	}
	registered := link.pairHash
	if kind == protocol.RelayStreamKindLink {
		registered = link.linkHash
	}
	if !auth.TokenMatches(registered, token) {
		if ip != nil {
			h.quotas.CountRefusedDial(*ip, now)
		}
		return nil, protocol.CloseDialRefused, 0
	}
	if blocked, retry := h.quotas.Blocked(relayID, now); blocked {
		return nil, protocol.CloseQuotaExceeded, retry
	}
	if _, taken := link.slots[kind]; taken {
		return nil, protocol.CloseSlotTaken, 0
	}
	id := make([]byte, 16)
	_, _ = rand.Read(id)
	stream := &relayStream{id: auth.EncodeBase64URL(id), owner: link, kind: kind, dialer: dialer, woken: make(chan struct{}), done: make(chan struct{})}
	link.slots[kind] = stream
	h.pending[stream.id] = stream
	return stream, 0, 0
}

// runAdmitted is RunAdmittedAsync (RelayHub.cs:504-562).
func (h *Hub) runAdmitted(stream *relayStream) {
	dialer := stream.dialer
	timedOut := make(chan struct{})
	timer := h.clock.AfterFunc(h.o.AcceptTimeout, func() { close(timedOut) })
	defer timer.Stop()
	// A failed send means the control socket is closing: its closeLink closes this stream (1001).
	send(stream.owner.sock, protocol.RelayIncoming{StreamId: stream.id, Kind: stream.kind})
	firstCh := make(chan socket.Frame, 1)
	go func() { firstCh <- dialer.Receive() }()
	var first socket.Frame
	firstDone := false
	select {
	case <-stream.woken:
	case first = <-firstCh:
		firstDone = true
	case <-timedOut:
	}

	h.mu.Lock()
	isOpen := stream.state == open
	if stream.state == waiting {
		h.releaseLocked(stream) // the dialer spoke or left first, or the accept timed out
	}
	h.mu.Unlock()

	awaitFirst := func() socket.Frame {
		if !firstDone {
			first, firstDone = <-firstCh, true
		}
		return first
	}
	drainAfter := func() {
		if f := awaitFirst(); f.Kind != socket.Close && f.Kind != socket.Lost {
			dialer.Drain()
		}
	}

	if !isOpen {
		stream.wake()
		if !firstDone {
			select {
			case first = <-firstCh:
				firstDone = true
			default:
			}
		}
		if !firstDone {
			dialer.Close(protocol.CloseAcceptTimeout, "") // no-op when the stream was closed by others
		} else {
			endOrRefuse(dialer, first)
		}
		drainAfter()
		return
	}

	ext := stream.ext.Load()
	if !firstDone {
		select {
		case first = <-firstCh:
			firstDone = true
		default:
		}
	}
	if firstDone {
		// The dialer spoke or left before STREAM_OPEN.
		h.release(stream)
		endOrRefuse(dialer, first)
		ext.Close(socket.NormalClosure, "")
		drainAfter()
		ext.Drain()
		return
	}

	if retryAfter, blocked := h.spendStream(stream); blocked {
		closeSockets(stream, protocol.CloseQuotaExceeded, reason(retryAfter))
		drainAfter()
		ext.Drain()
		return
	}

	send(dialer, protocol.RelayStreamOpen{})
	var wg sync.WaitGroup
	wg.Add(1)
	go func() {
		defer wg.Done()
		h.pump(stream, dialer, ext, firstCh, true)
	}()
	h.pump(stream, ext, dialer, nil, false)
	wg.Wait()
}

// runAccept is RunAcceptAsync (RelayHub.cs:564-610): a given owner (S1 v1.11 `id`) is checked BEFORE the stream is
// taken, so a wrong id gets 4504 and leaves the stream to its real accept.
func (h *Hub) runAccept(ext *socket.Socket, streamID string, owner *string, ip *netip.Addr) {
	var stream *relayStream
	limitedFor := 0
	h.mu.Lock()
	if p, ok := h.pending[streamID]; ok && (owner == nil || *owner == p.owner.relayID) {
		delete(h.pending, streamID)
		if p.state == waiting && !p.owner.gone {
			p.state = open
			p.ext.Store(ext)
			stream = p
		}
	}
	// C6: a refused accept is noise; over the budget it is 4530 (a known pending accept above is never refused)
	if stream == nil && ip != nil {
		now := h.clock.Now()
		if limited, retry := h.quotas.DialerLimited(*ip, now); limited {
			limitedFor = retry
		} else {
			h.quotas.CountNoise(*ip, now)
		}
	}
	h.mu.Unlock()

	if stream == nil {
		if limitedFor > 0 {
			closeAndDrain(ext, protocol.CloseDialerLimited, reason(limitedFor))
		} else {
			closeAndDrain(ext, protocol.CloseNotOnline, "") // unknown, expired, used, its Chrome left, or another relayId's
		}
		return
	}
	stream.wake()
	<-stream.done // the dialer's handler reads and closes this socket too
}

// pump is PumpAsync (RelayHub.cs:612-712): forwards whole binary frames from one side to the other until from ends.
func (h *Hub) pump(stream *relayStream, from, to *socket.Socket, first <-chan socket.Frame, up bool) {
	var frame socket.Frame
	if first != nil {
		frame = <-first
	} else {
		frame = from.Receive()
	}
	for {
		switch {
		case frame.Kind == socket.Close:
			h.release(stream)
			from.Close(socket.NormalClosure, "")
			to.Close(socket.NormalClosure, "") // never the peer's own code (S1)
			return
		case frame.Kind == socket.Lost:
			h.release(stream)
			to.Close(socket.NormalClosure, "")
			return
		case from.IsClosing():
			// draining after a close
		case frame.Kind == socket.TooLarge || frame.Kind == socket.Text:
			h.release(stream)
			code := protocol.CloseProtocolError
			if frame.Kind == socket.TooLarge {
				code = protocol.CloseFrameTooLarge
			}
			from.Close(code, "")
			to.Close(socket.NormalClosure, "")
		default: // binary
			if over, retryAfter := h.count(stream, len(frame.Bytes), up); over != nil {
				for _, s := range over {
					closeSockets(s, protocol.CloseQuotaExceeded, reason(retryAfter))
				}
				break
			}
			if !to.SendBinary(frame.Bytes) {
				h.release(stream)
				from.Close(socket.NormalClosure, "")
				if !to.IsClosing() {
					to.Abort() // dead or stuck; a socket already closing keeps its close code (S1, S6)
				}
			}
		}
		frame = from.Receive()
	}
}

// spendStream (S6): a stream is spent at STREAM_OPEN. blocked → the seconds to the next day, and the stream is
// released. A stream another close already released is left to that close (not blocked, not counted).
func (h *Hub) spendStream(stream *relayStream) (int, bool) {
	now := h.clock.Now()
	h.mu.Lock()
	defer h.mu.Unlock()
	if stream.state != open {
		return 0, false
	}
	if blocked, retry := h.quotas.Blocked(stream.owner.relayID, now); blocked {
		h.releaseLocked(stream)
		return retry, true
	}
	h.quotas.CountStream(stream.owner.relayID, now)
	return 0, false
}

// count adds piped bytes; on crossing the day's quota it returns every stream of that Chrome (already released).
func (h *Hub) count(stream *relayStream, bytes int, up bool) ([]*relayStream, int) {
	h.mu.Lock()
	defer h.mu.Unlock()
	if up {
		stream.up += int64(bytes)
	} else {
		stream.down += int64(bytes)
	}
	over, retryAfter := h.quotas.AddBytes(stream.owner.relayID, int64(bytes), h.clock.Now())
	if !over {
		return nil, 0
	}
	all := make([]*relayStream, 0, len(stream.owner.slots)+1)
	included := false
	for _, s := range stream.owner.slots {
		all = append(all, s)
		included = included || s == stream
	}
	if !included {
		all = append(all, stream)
	}
	for _, s := range all {
		h.releaseLocked(s)
	}
	return all, retryAfter
}

// release frees a stream's slot and streamId (idempotent).
func (h *Hub) release(stream *relayStream) {
	h.mu.Lock()
	h.releaseLocked(stream)
	h.mu.Unlock()
}

func (h *Hub) releaseLocked(stream *relayStream) {
	if stream.state == closed {
		return
	}
	stream.state = closed
	if stream.owner.slots[stream.kind] == stream {
		delete(stream.owner.slots, stream.kind)
	}
	delete(h.pending, stream.id)
}

// closeSockets closes both sides of a stream without waiting (CloseSockets, RelayHub.cs:802-807). The waiting dialer is
// woken only AFTER both close requests: woken first, it could claim 4508 ahead of this code (T5 critic F1).
func closeSockets(stream *relayStream, code int, reason string) {
	stream.dialer.CloseAsync(code, reason)
	if ext := stream.ext.Load(); ext != nil {
		ext.CloseAsync(code, reason)
	}
	stream.wake()
}

// endOrRefuse answers a peer close, or closes 4513 / 4500 for anything else the dialer sent before STREAM_OPEN.
func endOrRefuse(dialer *socket.Socket, f socket.Frame) {
	switch f.Kind {
	case socket.Close:
		dialer.Close(socket.NormalClosure, "")
	case socket.Lost:
	case socket.TooLarge:
		dialer.Close(protocol.CloseFrameTooLarge, "")
	default:
		dialer.Close(protocol.CloseProtocolError, "")
	}
}
