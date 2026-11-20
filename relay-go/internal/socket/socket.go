// Package socket ports RelaySocket.cs on gorilla/websocket (plan-relay-go D2): one accepted WebSocket with one receiver
// (its handler) and any number of senders. Sends are serialized and each bounded by SendTimeout (a peer that stops
// reading is aborted); Close is idempotent — the first requested code wins — and bounded by CloseTimeout.
package socket

import (
	"errors"
	"io"
	"math"
	"net"
	"sync"
	"sync/atomic"
	"time"
	"unicode/utf8"

	"github.com/gorilla/websocket"

	"github.com/nxpanda-ai/panda-relay/relay-go/internal/clock"
	"github.com/nxpanda-ai/panda-relay/relay-go/internal/options"
)

// Kind of one receive: a whole message, the peer's close, an oversized message, or a dead connection.
type Kind int

const (
	Text Kind = iota
	Binary
	Close
	TooLarge
	Lost
)

// Frame is one received message. Bytes is lent by its socket: valid only until the next Receive / Drain / Dispose on
// that socket (RelaySocket.cs:17-21). Empty for Close, TooLarge and Lost.
type Frame struct {
	Kind  Kind
	Bytes []byte
}

const (
	noSend  int64 = 0
	aborted int64 = math.MinInt64 // never a timestamp
	// NormalClosure etc.: the WebSocket codes the relay closes with besides its own (RFC 6455 §7.4.1).
	NormalClosure       = websocket.CloseNormalClosure           // 1000
	EndpointUnavailable = websocket.CloseGoingAway               // 1001
	invalidPayload      = websocket.CloseInvalidFramePayloadData // 1007
)

// Socket is RelaySocket.
type Socket struct {
	ws    *websocket.Conn
	conn  net.Conn
	o     *options.Options
	clock clock.Clock

	born time.Time // the socket's start: times below are offsets from it (monotonic with the real clock)

	send      sync.Mutex // one data or close write at a time (the C# _send semaphore)
	sendTimer clock.Timer
	sendStart atomic.Int64 // since() at the send in flight's start, noSend, or aborted

	closing   atomic.Bool
	closeCode atomic.Int32
	closeDone chan struct{} // set under timersMu by the close request; closed once its frame is written (Dispose waits, T5 critic F2)
	timersMu  sync.Mutex
	abortT    clock.Timer // the close timeout, armed when the close is requested
	pingT     clock.Timer
	pongT     clock.Timer // the keepalive's wait for a pong
	disposed  bool

	lastPong atomic.Int64 // since() at the last pong; 0 = none yet
	pongBusy atomic.Bool  // a pong reply is being written

	own  []byte  // the socket's own buffer (16 KiB at most), for the messages that fit
	big  *[]byte // a pooled buffer lent to the last frame, returned (cleared) by the next Receive or Dispose
	used int     // bytes of big written
}

const firstBuffer = 16 * 1024

// pool holds the buffers of messages over firstBuffer, each MaxFrameBytes + 1 long (one size per process).
var pool sync.Pool

func (s *Socket) takeBig() *[]byte {
	if p, ok := pool.Get().(*[]byte); ok && len(*p) == s.o.MaxFrameBytes+1 {
		return p
	}
	b := make([]byte, s.o.MaxFrameBytes+1)
	return &b
}

// returnBig ends the last frame's lease: no tunnel bytes or control secrets are left in the shared pool (F12).
func (s *Socket) returnBig() {
	if s.big != nil {
		clear((*s.big)[:s.used])
		pool.Put(s.big)
		s.big, s.used = nil, 0
	}
}

// New wraps an upgraded connection: no automatic answer to the peer's close (the hub frees state first, then answers
// 1000 — never the peer's code), keepalive pings every KeepAliveInterval, abort when no pong within KeepAliveTimeout.
func New(ws *websocket.Conn, o *options.Options, c clock.Clock) *Socket {
	s := &Socket{ws: ws, conn: ws.NetConn(), o: o, clock: c, born: c.Now()}
	ws.SetCloseHandler(func(int, string) error { return nil })
	// gorilla's default answers a ping with a 1 s write deadline on the read path: a peer slow to read for 1 s would
	// break the socket. .NET queues the pong; here it is written by its own goroutine within SendTimeout (gorilla's
	// lock orders it with the data writes; after a close it fails with ErrCloseSent, harmlessly). At most ONE pong is in
	// flight: a ping that comes while one is written goes unanswered (RFC 6455 §5.5.3 lets a peer answer only the most
	// recent ping), so a ping flood never piles up goroutines (T4 critic round 3)
	ws.SetPingHandler(func(data string) error {
		if !s.pongBusy.CompareAndSwap(false, true) {
			return nil
		}
		go func() {
			defer s.pongBusy.Store(false)
			_ = ws.WriteControl(websocket.PongMessage, []byte(data), time.Now().Add(o.SendTimeout))
		}()
		return nil
	})
	ws.SetPongHandler(func(string) error {
		s.lastPong.Store(s.since())
		return nil
	})
	s.sendTimer = c.AfterFunc(time.Duration(math.MaxInt64), s.onSendTimer)
	s.sendTimer.Stop()
	s.timersMu.Lock()
	s.pingT = c.AfterFunc(o.KeepAliveInterval, s.ping)
	s.timersMu.Unlock()
	return s
}

// IsClosing: the relay asked to close this socket.
func (s *Socket) IsClosing() bool { return s.closing.Load() }

// CloseCode is the code this socket was closed with by the relay (0 while open).
func (s *Socket) CloseCode() int { return int(s.closeCode.Load()) }

// Receive returns one whole message of at most MaxFrameBytes, in this socket's buffer (lent to the frame). Never panics
// or fails for the peer's faults: they are Lost.
func (s *Socket) Receive() Frame {
	s.returnBig()
	typ, r, err := s.ws.NextReader() // discards the rest of an earlier oversized message
	if err != nil {
		return ended(err)
	}
	if s.own == nil {
		s.own = make([]byte, min(firstBuffer, s.o.MaxFrameBytes+1))
	}
	buf, n := s.own, 0
	for {
		if n == len(buf) { // full: move to a pooled buffer of MaxFrameBytes + 1 (only reached when that is bigger)
			s.big = s.takeBig()
			copy(*s.big, buf[:n])
			buf = *s.big
		}
		m, err := r.Read(buf[n:])
		n += m
		if s.big != nil {
			s.used = n
		}
		if n > s.o.MaxFrameBytes {
			return Frame{Kind: TooLarge}
		}
		if errors.Is(err, io.EOF) {
			break
		}
		if err != nil {
			return ended(err)
		}
	}
	msg := buf[:n]
	if typ == websocket.TextMessage {
		if !utf8.Valid(msg) {
			// .NET's ManagedWebSocket validates text: it closes 1007 itself and the receive throws (= Lost)
			_ = s.ws.WriteControl(websocket.CloseMessage, websocket.FormatCloseMessage(invalidPayload, ""), time.Now().Add(s.o.CloseTimeout))
			return Frame{Kind: Lost}
		}
		return Frame{Kind: Text, Bytes: msg}
	}
	return Frame{Kind: Binary, Bytes: msg}
}

// ended maps a read error: the peer's close frame is Close, anything else (reset, abort, protocol error) is Lost.
func ended(err error) Frame {
	// gorilla reports a TCP EOF as a CloseError 1006 it made up (a received 1006 is a protocol error there): Lost, as
	// the C# WebSocketException (RelaySocket.cs:106-109)
	var ce *websocket.CloseError
	if errors.As(err, &ce) && ce.Code != websocket.CloseAbnormalClosure {
		return Frame{Kind: Close}
	}
	return Frame{Kind: Lost}
}

// Drain receives and discards until the peer's close or the end of the connection (after the relay closed).
func (s *Socket) Drain() {
	for {
		if k := s.Receive().Kind; k == Close || k == Lost {
			return
		}
	}
}

// SendText sends a text message; false when the socket is closing or gone.
func (s *Socket) SendText(b []byte) bool { return s.Send(websocket.TextMessage, b) }

// SendBinary sends a binary message (a tunnel frame, forwarded whole); false when the socket is closing or gone.
func (s *Socket) SendBinary(b []byte) bool { return s.Send(websocket.BinaryMessage, b) }

// Send writes one whole message; false when the socket is closing or gone. A send that runs SendTimeout is aborted by
// the socket's one send timer (RelaySocket.cs:208-299): a stuck write leaves the socket unusable anyway.
func (s *Socket) Send(typ int, b []byte) bool {
	s.send.Lock()
	if s.closing.Load() {
		s.send.Unlock()
		return false
	}
	s.sendStart.Store(s.since())
	s.sendTimer.Reset(s.o.SendTimeout)
	err := s.ws.WriteMessage(typ, b)
	// disarm BEFORE releasing the lock: a disarm after could wipe the next send's arm (RelaySocket.cs:258-262)
	ended := s.sendStart.Swap(noSend)
	s.sendTimer.Stop()
	s.send.Unlock()
	return err == nil && ended != aborted
}

// onSendTimer aborts only the send whose start it read, and only once it really ran SendTimeout: a callback that
// fires late for an older send re-arms or does nothing.
func (s *Socket) onSendTimer() {
	start := s.sendStart.Load()
	if start == noSend || start == aborted {
		return
	}
	elapsed := time.Duration(s.since() - start)
	if elapsed < s.o.SendTimeout {
		s.sendTimer.Reset(s.o.SendTimeout - elapsed)
		return
	}
	if s.sendStart.CompareAndSwap(start, aborted) {
		s.Abort()
	}
}

// since is the time since the socket's start, in nanoseconds, at least 1 (0 is noSend / no pong): monotonic with the
// real clock, as the C# GetTimestamp (a wall-clock step cannot abort a healthy socket).
func (s *Socket) since() int64 { return int64(s.clock.Now().Sub(s.born)) + 1 }

// Close sends a close frame once (the first caller's code wins; from then on no data send starts), after any send in
// progress, and gives the peer CloseTimeout to answer before aborting — that timer is armed BEFORE waiting for the
// send lock, so a stuck send cannot hold it off (RelaySocket.cs:301-331). The receiver sees the answer as Close.
func (s *Socket) Close(code int, reason string) {
	if s.requestClose(code) {
		s.writeClose(code, reason)
	}
}

// CloseAsync is the C# fire-and-forget `_ = CloseAsync(code)`: the close is requested now — its code fixed, sends
// refused, the close timeout armed, as CloseAsync does before its first await — and only the frame is written later.
func (s *Socket) CloseAsync(code int, reason string) {
	if s.requestClose(code) {
		go s.writeClose(code, reason)
	}
}

// requestClose: true for the first request (its code wins), which must then write the frame.
func (s *Socket) requestClose(code int) bool {
	if !s.closing.CompareAndSwap(false, true) {
		return false
	}
	s.closeCode.Store(int32(code))
	s.timersMu.Lock()
	s.closeDone = make(chan struct{})
	if !s.disposed {
		s.abortT = s.clock.AfterFunc(s.o.CloseTimeout, s.Abort)
	}
	s.timersMu.Unlock()
	return true
}

// writeClose writes the requested close frame, then marks it written for Dispose. Only requestClose's winner calls it.
func (s *Socket) writeClose(code int, reason string) {
	defer close(s.closeDone) // set by requestClose before this call, never again
	s.send.Lock()
	defer s.send.Unlock()
	err := s.ws.WriteControl(websocket.CloseMessage, websocket.FormatCloseMessage(code, reason), time.Now().Add(s.o.CloseTimeout))
	if err != nil && !errors.Is(err, websocket.ErrCloseSent) {
		s.Abort()
	}
}

// Abort drops the TCP connection: every receive and send in flight fails.
func (s *Socket) Abort() { _ = s.conn.Close() }

// ping is the keepalive (RelayOptions.cs:45-48): a ping every KeepAliveInterval; no pong within KeepAliveTimeout →
// abort. Pongs are read by the receive in flight, as with .NET (a socket is always being received while it lives).
func (s *Socket) ping() {
	// as .NET's keep-alive: skipped while a send holds the lock (that send is bounded by its own timer); holding the
	// lock, no data or close frame is written, and the only other writes (a pong, a 1007 close) are short and bounded
	// by gorilla's own lock — so a ping not written within KeepAliveTimeout means the write side is stuck → abort
	if !s.send.TryLock() {
		s.rearmPing()
		return
	}
	if s.closing.Load() {
		s.send.Unlock()
		return // closing: the close timeout bounds the rest
	}
	sent := s.since()
	err := s.ws.WriteControl(websocket.PingMessage, nil, time.Now().Add(s.o.KeepAliveTimeout))
	s.send.Unlock()
	s.timersMu.Lock()
	defer s.timersMu.Unlock()
	if s.disposed || errors.Is(err, websocket.ErrCloseSent) {
		return
	}
	if err != nil {
		go s.Abort()
		return
	}
	if s.pongT != nil {
		s.pongT.Stop()
	}
	s.pongT = s.clock.AfterFunc(s.o.KeepAliveTimeout, func() {
		if s.lastPong.Load() < sent {
			s.Abort()
		}
	})
	s.pingT = s.clock.AfterFunc(s.o.KeepAliveInterval, s.ping)
}

func (s *Socket) rearmPing() {
	s.timersMu.Lock()
	defer s.timersMu.Unlock()
	if !s.disposed {
		s.pingT = s.clock.AfterFunc(s.o.KeepAliveInterval, s.ping)
	}
}

// Dispose ends the socket: stops its timers and keepalive and closes the TCP connection (Go's server does not close a
// hijacked connection). A CloseAsync frame still being written is waited for first, timers still armed, so it reaches
// the wire (the C# close frame is queued before its `using` disposes — T5 critic F2); the wait is bounded: the send
// timer ends a stuck send ahead of it and the close timeout armed by requestClose aborts the write.
func (s *Socket) Dispose() {
	s.timersMu.Lock()
	pending := s.closeDone
	s.timersMu.Unlock()
	if pending != nil {
		<-pending
	}
	s.timersMu.Lock()
	s.disposed = true
	if s.abortT != nil {
		s.abortT.Stop()
	}
	s.pingT.Stop()
	if s.pongT != nil {
		s.pongT.Stop()
	}
	s.timersMu.Unlock()
	s.sendTimer.Stop()
	_ = s.conn.Close()
	s.returnBig() // Dispose runs on the receiving goroutine, after its last Receive
}
