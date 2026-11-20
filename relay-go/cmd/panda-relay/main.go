// Command panda-relay is the Go port of the Panda relay (plan-relay-go): the same command line, wire contract and
// close codes as the C# relay (bridge/src/Panda.Relay), judged by bridge/tests/Panda.Relay.Conformance.
package main

import (
	"errors"
	"fmt"
	"net"
	"net/http"
	"net/netip"
	"os"
	"os/signal"
	"strconv"
	"syscall"

	"github.com/nxpanda-ai/panda-relay/relay-go/internal/clock"
	"github.com/nxpanda-ai/panda-relay/relay-go/internal/hosted"
	"github.com/nxpanda-ai/panda-relay/relay-go/internal/options"
	"github.com/nxpanda-ai/panda-relay/relay-go/internal/server"
)

func main() { os.Exit(run(os.Args[1:])) }

// listen opens the public listener on --listen (loopback by default, O10) and the internal one on 127.0.0.1 whatever
// --listen says (C5, O9); internal is nil without --internal-port.
func listen(o *options.Options) (public, internal net.Listener, err error) {
	public, err = net.Listen("tcp", netip.AddrPortFrom(o.ListenAddress, uint16(o.Port)).String())
	if err != nil {
		return nil, nil, err
	}
	if o.InternalPort != nil {
		internal, err = net.Listen("tcp", "127.0.0.1:"+strconv.Itoa(*o.InternalPort))
		if err != nil {
			_ = public.Close()
			return nil, nil, err
		}
	}
	return public, internal, nil
}

// run is Program.Main: 2 = a command line the relay cannot run with, 1 = cannot listen, 0 = stopped.
func run(args []string) int {
	o, err := options.Parse(args)
	if err != nil {
		fmt.Fprintf(os.Stderr, "panda-relay: %v\n%s\n", err, options.Usage)
		return 2
	}
	public, internal, err := listen(o)
	if err != nil {
		fmt.Fprintf(os.Stderr, "panda-relay: cannot listen: %v\n", err)
		return 1
	}

	srv := server.New(o, clock.Real{}, hosted.New(o)) // unconfigured without --hosted-admin-url: RELAY_HOST → unavailable
	line := fmt.Sprintf("panda-relay listening on %s:%d for %s", o.ListenAddress, public.Addr().(*net.TCPAddr).Port, o.PublicHost)
	if internal != nil {
		line += fmt.Sprintf("; internal 127.0.0.1:%d", internal.Addr().(*net.TCPAddr).Port)
	}
	fmt.Println(line)

	done := make(chan error, 1)
	go func() { done <- srv.Serve(public, internal) }()
	stop := make(chan os.Signal, 1)
	signal.Notify(stop, os.Interrupt, syscall.SIGTERM)
	select {
	case <-stop:
		srv.Shutdown(server.ShutdownTimeout) // every control socket closed 1001 first (O6)
		return 0
	case err := <-done:
		if errors.Is(err, http.ErrServerClosed) {
			return 0
		}
		fmt.Fprintf(os.Stderr, "panda-relay: %v\n", err)
		return 1
	}
}
