package socket

import (
	"bufio"
	"encoding/binary"
	"net"
	"net/http"
	"net/http/httptest"
	"strconv"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/gorilla/websocket"

	"github.com/nxpanda-ai/panda-relay/relay-go/internal/clock"
	"github.com/nxpanda-ai/panda-relay/relay-go/internal/options"
)

// counter is a sink that only counts (no allocation per write).
type counter struct{ n atomic.Int64 }

func (c *counter) Write(p []byte) (int, error) { c.n.Add(int64(len(p))); return len(p), nil }

// BenchmarkO7Forward measures suite README O7 (zero allocation per forwarded message; the C# relay measures it): the
// relay side of one socket receives a binary message (1 KiB: its own buffer; 60000 B: a pooled one) and sends it back,
// as a stream's pump does. The peer is a raw
// TCP client writing a pre-built masked frame and counting what comes back, so the allocations it reports are the
// relay side's (plus the runtime's own). Run: go test -run x -bench O7 -benchmem ./internal/socket/
func BenchmarkO7Forward(b *testing.B) {
	for _, size := range []int{1024, 60000} {
		b.Run(strconv.Itoa(size), func(b *testing.B) { forward(b, size) })
	}
}

func forward(b *testing.B, size int) {
	o := options.Default("relay.test")
	o.KeepAliveInterval = 24 * time.Hour
	up := websocket.Upgrader{ReadBufferSize: 16 * 1024, WriteBufferSize: 16 * 1024}
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		ws, err := up.Upgrade(w, r, nil)
		if err != nil {
			return
		}
		s := New(ws, o, clock.Real{})
		defer s.Dispose()
		for f := s.Receive(); f.Kind == Binary; f = s.Receive() {
			if !s.SendBinary(f.Bytes) {
				return
			}
		}
	}))
	defer srv.Close()

	conn, err := net.Dial("tcp", strings.TrimPrefix(srv.URL, "http://"))
	if err != nil {
		b.Fatal(err)
	}
	defer conn.Close()
	_, _ = conn.Write([]byte("GET / HTTP/1.1\r\nHost: relay.test\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
		"Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n"))
	br := bufio.NewReader(conn)
	for {
		line, err := br.ReadString('\n')
		if err != nil {
			b.Fatal(err)
		}
		if line == "\r\n" {
			break
		}
	}
	sink := &counter{}
	go func() { _, _ = br.WriteTo(sink) }()

	frame := make([]byte, 2+2+4+size) // FIN|binary, MASK|126, 16-bit length, mask key, masked payload
	frame[0], frame[1] = 0x82, 0x80|126
	binary.BigEndian.PutUint16(frame[2:], uint16(size))
	copy(frame[4:8], []byte{1, 2, 3, 4})
	for i := 0; i < size; i++ {
		frame[8+i] = byte(i) ^ frame[4+i%4]
	}
	back := int64(4 + size) // the relay's unmasked frame

	send := func(n int) {
		start := sink.n.Load()
		for i := 0; i < n; i++ {
			if _, err := conn.Write(frame); err != nil {
				b.Fatal(err)
			}
		}
		for sink.n.Load() < start+int64(n)*back {
			time.Sleep(time.Millisecond)
		}
	}
	send(100) // warm-up: buffers, pools
	b.ReportAllocs()
	b.SetBytes(int64(size))
	b.ResetTimer()
	send(b.N)
}
