// Package hub ports RelayHub.cs (plan-relay P4, seams S1 + S6): ONE lock guards every map and every state change;
// sockets are closed only after the state that names them is gone (a freed slot is visible before the close frame
// leaves), and never under the lock. The relay never reads payload.
package hub

import (
	"crypto/rand"
	"net/http"
	"net/netip"
	"strconv"
	"sync"
	"sync/atomic"
	"time"

	"github.com/gorilla/websocket"

	"github.com/nxpanda-ai/panda-relay/relay-go/internal/auth"
	"github.com/nxpanda-ai/panda-relay/relay-go/internal/clock"
	"github.com/nxpanda-ai/panda-relay/relay-go/internal/logx"
	"github.com/nxpanda-ai/panda-relay/relay-go/internal/options"
	"github.com/nxpanda-ai/panda-relay/relay-go/internal/protocol"
	"github.com/nxpanda-ai/panda-relay/relay-go/internal/quotas"
	"github.com/nxpanda-ai/panda-relay/relay-go/internal/socket"
)

// Hosted forwards RELAY_HOST / ROTATE / UNHOST to the hosted bridge (HostedAdmin.cs; plan-relay-go T6, seam S5).
type Hosted interface {
	Configured() bool
	Run(relayID string, request protocol.RelayMessage) protocol.RelayHostResult
}

// browserLink is a registered control socket: the Chrome online under a relayId (S1).
type browserLink struct {
	relayID  string
	sock     *socket.Socket
	linkHash *string                                   // under Hub.mu
	pairHash *string                                   // under Hub.mu
	gone     bool                                      // under Hub.mu
	slots    map[protocol.RelayStreamKind]*relayStream // under Hub.mu: one stream per kind
	hosting  atomic.Bool
}

// Hub is RelayHub.
type Hub struct {
	mu        sync.Mutex
	online    map[string]*browserLink
	pending   map[string]*relayStream // streamId → stream waiting for its accept
	notOnline *quotas.NotOnlineLog    // under mu
	stopping  bool                    // under mu: CloseAll ran (graceful stop)

	o               *options.Options
	clock           clock.Clock
	quotas          *quotas.Quotas // under mu
	hosted          Hosted         // nil = no hosted bridge
	hostingInFlight atomic.Int32   // plan-relay-deploy C3: hosted calls running, all sockets
	upgrader        websocket.Upgrader
	active          atomic.Int64 // WebSocket handlers running (graceful stop waits for them)
}

// New is new RelayHub(options, time, logger, hosted).
func New(o *options.Options, c clock.Clock, hosted Hosted) *Hub {
	return &Hub{
		online:    map[string]*browserLink{},
		pending:   map[string]*relayStream{},
		notOnline: quotas.NewNotOnlineLog(o.MaxTrackedNoise),
		o:         o,
		clock:     c,
		quotas:    quotas.New(o),
		hosted:    hosted,
		upgrader: websocket.Upgrader{
			ReadBufferSize:  16 * 1024,
			WriteBufferSize: 16 * 1024,
			// the Origin rules are checked before the upgrade, per path (FromExtension); this only lets them through
			CheckOrigin: func(*http.Request) bool { return true },
		},
	}
}

func (h *Hub) accept(w http.ResponseWriter, r *http.Request) *socket.Socket {
	ws, err := h.upgrader.Upgrade(w, r, nil)
	if err != nil {
		return nil // the upgrader answered the request (a handshake isWebSocketRequest let through but it refused)
	}
	return socket.New(ws, h.o, h.clock)
}

// ------------------------------------------------------------------ /v1/browser

// RunControl is RunControlAsync (RelayHub.cs:98-155). ip is the client's address (zero = unknown).
func (h *Hub) RunControl(w http.ResponseWriter, r *http.Request, client netip.Addr) {
	h.enter()
	defer h.leave()
	if !isWebSocketRequest(r) {
		w.WriteHeader(http.StatusBadRequest)
		return
	}
	if !fromExtension(r) {
		w.WriteHeader(http.StatusForbidden) // before anything is counted
		return
	}
	ip := quotas.Bucket(client)
	sock := h.accept(w, r)
	if sock == nil {
		return
	}
	defer sock.Dispose()
	h.mu.Lock()
	allowed, retryAfter := h.quotas.TryCountRegistration(ip, h.clock.Now())
	h.mu.Unlock()
	if !allowed {
		logx.Info("registration limit reached for %s", ip)
		closeAndDrain(sock, protocol.CloseDialerLimited, reason(retryAfter))
		return
	}

	// S1 v1.11: an optional `id` names the relayId this socket will prove; malformed or repeated → 4500
	q := parseQuery(r.URL.RawQuery)
	claimed, hasID := q.single("id"), q.has("id")
	if hasID && (claimed == nil || !auth.IsRelayID(*claimed)) {
		closeAndDrain(sock, protocol.CloseProtocolError, "")
		return
	}

	link := h.register(sock, ip, claimed)
	if link == nil {
		return
	}
	defer func() {
		h.closeLink(link, socket.EndpointUnavailable)
		logx.Info("control socket of %s closed (%d)", link.relayID, sock.CloseCode())
	}()
	h.controlLoop(link)
}

// register: CHALLENGE → REGISTER (≤ RegisterTimeout) → REGISTERED; nil when the socket was closed instead. A claimed
// relayId (S1 v1.11 `id`) must be the one the signature proves, else 4503 (RelayHub.cs:161-212).
func (h *Hub) register(sock *socket.Socket, ip netip.Addr, claimed *string) *browserLink {
	nonce := make([]byte, 32)
	_, _ = rand.Read(nonce) // never fails (crypto/rand)
	timer := h.clock.AfterFunc(h.o.RegisterTimeout, func() { sock.CloseAsync(protocol.CloseProtocolError, "register timeout") })
	send(sock, protocol.RelayChallenge{Nonce: auth.EncodeBase64URL(nonce)})
	first := sock.Receive()
	timer.Stop()
	if ended(sock, first) {
		return nil
	}

	var register protocol.RelayRegister
	isRegister := false
	if first.Kind == socket.Text {
		if res := protocol.ParseRelay(first.Bytes); res.Kind == protocol.Ok {
			register, isRegister = res.Message.(protocol.RelayRegister)
		}
	}
	if !isRegister {
		code := protocol.CloseProtocolError
		if first.Kind == socket.TooLarge {
			code = protocol.CloseFrameTooLarge
		}
		closeAndDrain(sock, code, "")
		return nil
	}

	spki, ok := auth.VerifyRegistration(register.Spki, register.Sig, nonce, h.o.PublicHost)
	if !ok || (claimed != nil && *claimed != auth.RelayIDFromSPKI(spki)) {
		logx.Info("registration proof refused from %s", ip)
		closeAndDrain(sock, protocol.CloseRegistrationInvalid, "")
		return nil
	}

	link := &browserLink{relayID: auth.RelayIDFromSPKI(spki), sock: sock, linkHash: register.Link, pairHash: register.Pair,
		slots: map[protocol.RelayStreamKind]*relayStream{}}
	if !send(sock, protocol.RelayRegistered{RelayId: link.relayID}) {
		sock.Drain()
		return nil
	}

	h.mu.Lock()
	if h.stopping { // registered after CloseAll's snapshot: closed 1001 too, never left to the process exit (T6-T7 critic #4)
		h.mu.Unlock()
		closeAndDrain(sock, socket.EndpointUnavailable, "")
		return nil
	}
	older := h.online[link.relayID]
	h.online[link.relayID] = link // the hashes go online with the relayId (S1, critic N1)
	h.mu.Unlock()
	if older != nil {
		h.closeLink(older, protocol.CloseReplaced)
	}
	logx.Info("registered %s from %s", link.relayID, ip)
	return link
}

// controlLoop is ControlLoopAsync (RelayHub.cs:214-257).
func (h *Hub) controlLoop(link *browserLink) {
	sock := link.sock
	idle := h.clock.AfterFunc(h.o.ControlIdleTimeout, func() { sock.CloseAsync(socket.EndpointUnavailable, "idle") })
	defer idle.Stop()
	for {
		frame := sock.Receive()
		idle.Reset(h.o.ControlIdleTimeout)
		if ended(sock, frame) {
			return
		}
		if frame.Kind != socket.Text {
			code := protocol.CloseProtocolError
			if frame.Kind == socket.TooLarge {
				code = protocol.CloseFrameTooLarge
			}
			sock.Close(code, "")
			continue
		}
		res := protocol.ParseRelay(frame.Bytes)
		if res.Kind != protocol.Ok {
			sock.Close(protocol.CloseProtocolError, "")
			continue
		}
		switch m := res.Message.(type) {
		case protocol.RelayDialTokens:
			h.mu.Lock()
			link.linkHash = m.Link // replaces the set; absent = none (S1)
			link.pairHash = m.Pair
			h.mu.Unlock()
			send(sock, protocol.RelayTokensOk{Seq: m.Seq})
		case protocol.RelayPing:
			send(sock, protocol.RelayPong{})
		case protocol.RelayPong:
		case protocol.RelayHost, protocol.RelayRotate, protocol.RelayUnhost:
			h.startHosting(link, m)
		default:
			sock.Close(protocol.CloseProtocolError, "")
		}
	}
}

func seqOf(m protocol.RelayMessage) int64 {
	switch r := m.(type) {
	case protocol.RelayHost:
		return r.Seq
	case protocol.RelayRotate:
		return r.Seq
	case protocol.RelayUnhost:
		return r.Seq
	}
	return 0
}

func hostFailure(seq int64, e protocol.RelayHostError) protocol.RelayHostResult {
	return protocol.RelayHostResult{Seq: seq, Ok: false, Error: &e}
}

// startHosting (plan-relay-url D6, RelayHub.cs:259-315): the control loop never waits for the hosted bridge. One
// request per socket (a second → busy); RELAY_HOST / ROTATE also take one of the relay-wide MaxHosting slots (full →
// busy; UNHOST is exempt). The slot is freed before the result is sent. The message is never formatted or logged.
func (h *Hub) startHosting(link *browserLink, request protocol.RelayMessage) {
	seq := seqOf(request)
	if h.hosted == nil || !h.hosted.Configured() {
		send(link.sock, hostFailure(seq, protocol.RelayHostErrorUnavailable))
		return
	}
	if !link.hosting.CompareAndSwap(false, true) {
		send(link.sock, hostFailure(seq, protocol.RelayHostErrorBusy))
		return
	}
	_, unhost := request.(protocol.RelayUnhost)
	capped := !unhost
	if capped && h.hostingInFlight.Add(1) > int32(h.o.MaxHosting) {
		h.hostingInFlight.Add(-1)
		link.hosting.Store(false)
		send(link.sock, hostFailure(seq, protocol.RelayHostErrorBusy))
		return
	}
	go func() {
		result := h.runHosted(link.relayID, request, seq)
		if capped {
			h.hostingInFlight.Add(-1)
		}
		link.hosting.Store(false)
		send(link.sock, result)
	}()
}

func (h *Hub) runHosted(relayID string, request protocol.RelayMessage, seq int64) (result protocol.RelayHostResult) {
	defer func() {
		if p := recover(); p != nil {
			logx.Warn("Hosting request of relay %s failed with %T", relayID, p) // a bug, not an answer: the Chrome hears failed
			result = hostFailure(seq, protocol.RelayHostErrorFailed)
		}
	}()
	return h.hosted.Run(relayID, request)
}

// closeLink takes a Chrome offline: its slots and pending streamIds go first, then every socket is closed
// (RelayHub.cs:317-342).
func (h *Hub) closeLink(link *browserLink, controlCode int) {
	h.mu.Lock()
	link.gone = true
	if h.online[link.relayID] == link {
		delete(h.online, link.relayID)
	}
	streams := make([]*relayStream, 0, len(link.slots))
	for _, s := range link.slots {
		streams = append(streams, s)
	}
	for _, s := range streams {
		h.releaseLocked(s)
	}
	h.mu.Unlock()
	for _, s := range streams {
		closeSockets(s, socket.EndpointUnavailable, "")
	}
	link.sock.CloseAsync(controlCode, "")
}

// CloseAll takes every Chrome offline with 1001, their streams too (RelayHub.cs:344-356; ApplicationStopping, O6), and
// from then on a registration that completes is closed 1001 instead of going online (the C# relay leaves that one to the
// host's 5 s cut — a stricter Go).
func (h *Hub) CloseAll() {
	h.mu.Lock()
	h.stopping = true
	links := make([]*browserLink, 0, len(h.online))
	for _, l := range h.online {
		links = append(links, l)
	}
	h.mu.Unlock()
	for _, l := range links {
		h.closeLink(l, socket.EndpointUnavailable)
	}
}

// enter / leave count the WebSocket handlers running (Go's http.Server.Shutdown does not track hijacked connections).
func (h *Hub) enter() { h.active.Add(1) }
func (h *Hub) leave() { h.active.Add(-1) }

// Wait returns true once no WebSocket handler runs, false at deadline (Kestrel's ShutdownTimeout: the rest are cut by
// the process exit). It polls: shutdown only, and a handler may start while it waits (no WaitGroup Add/Wait race).
func (h *Hub) Wait(deadline time.Time) bool {
	for h.active.Load() > 0 {
		if !time.Now().Before(deadline) {
			return false
		}
		time.Sleep(10 * time.Millisecond)
	}
	return true
}

// ------------------------------------------------------------------ helpers

// send serializes m (a schema violation is a relay bug: logged by type only, not sent) and sends it.
func send(sock *socket.Socket, m protocol.RelayMessage) bool {
	b, err := protocol.MarshalRelay(m)
	if err != nil {
		logx.Error("refusing to send %s", m.RelayType())
		return false
	}
	return sock.SendText(b)
}

// ended: true when the peer's close or a dead connection ended the socket (the close is answered 1000).
func ended(sock *socket.Socket, f socket.Frame) bool {
	switch f.Kind {
	case socket.Close:
		sock.Close(socket.NormalClosure, "")
		return true
	case socket.Lost:
		return true
	default:
		if sock.IsClosing() {
			sock.Drain()
			return true
		}
		return false
	}
}

func closeAndDrain(sock *socket.Socket, code int, reason string) {
	sock.Close(code, reason)
	sock.Drain()
}

func reason(seconds int) string { return strconv.Itoa(seconds) }
