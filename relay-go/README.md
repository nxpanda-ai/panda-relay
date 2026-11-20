# relay-go — the Panda relay in Go

A port of the C# relay (`bridge/src/Panda.Relay`) to Go: the same command line, wire contract, close codes and log
events. It is judged by the conformance suite (`bridge/tests/Panda.Relay.Conformance`), which it passes unchanged.
Module: `github.com/nxpanda-ai/panda-relay/relay-go`. Its plan, decisions and review answers are
`docs/plan-relay-go.md` of the Panda AI Browser repository (not in the open-source copy).

Status: it is in the open-source relay repo beside the C# relay, but it is **not deployed** and does **not replace**
the C# relay (see "Not met, or different" and the load report below).

## Build

```
cd relay-go
go build -trimpath -o bin/panda-relay ./cmd/panda-relay        # Linux / macOS
go build -trimpath -o bin/panda-relay.exe ./cmd/panda-relay    # Windows
```

- `go.mod` says `go 1.25` (the language version and GODEBUG defaults) and `toolchain go1.27.4`. Under the default
  `GOTOOLCHAIN=auto`, the go command fetches that toolchain by itself (this needs network access to the Go proxy the
  first time). With `GOTOOLCHAIN=local` an older local Go builds it on a vulnerable standard library: check with
  `go version -m bin/panda-relay`.
- Go 1.25.3 reached 29 standard-library vulnerabilities (govulncheck, 2026-11-20). Some of them are fixed only on
  1.26 and later. With go1.27.4, govulncheck v1.1.4 reports none.
- The one dependency is `github.com/gorilla/websocket` v1.5.3.
- `bin/` is gitignored.

## Run

The command line is the C# relay's (a command line it cannot run with prints the usage and exits 2):

```
bin/panda-relay --public-host relay.example.com:443 [--listen <ip>] [--port <n>] [--trusted-proxy <ip|cidr>]...
    [--client-ip-header <name>] [--bytes-per-day <n>] [--streams-per-day <n>] [--max-hosting <n>]
    [--internal-port <n>] [--noise-per-ip-per-hour <n>]
    [--hosted-admin-url http://127.0.0.1:<port> --hosted-admin-token-file <path> --hosted-mcp-url https://<host>/mcp]
```

- Without `--listen`, the relay listens on loopback.
- `--internal-port` always listens on 127.0.0.1.
- The relay logs one line per event to stdout. A log line never holds a connection code, dial token, tunnel byte,
  query string or header value.
- SIGTERM or Ctrl+C stops the relay gracefully:
  - The listeners stop accepting. At the same time (not ordered), every control socket and its streams are closed
    with 1001. A registration that completes after that is closed with 1001 too.
  - The handlers get 5 s to end, as with the C# host.

## Test

```
cd relay-go
go vet ./... && go test ./...                         # unit + obligation tests
go test -run x -bench O7 -benchmem ./internal/socket/  # O7: allocations per forwarded message

cd ../bridge                                         # the conformance suite, on its own (never beside another suite)
# bash:       export PANDA_RELAY_CMD=<absolute path>/relay-go/bin/panda-relay PANDA_RELAY_LABEL=relay-go
# PowerShell: $env:PANDA_RELAY_CMD = "<absolute path>\relay-go\bin\panda-relay.exe"; $env:PANDA_RELAY_LABEL = "relay-go"
dotnet test --project tests/Panda.Relay.Conformance
```

In the open-source copy, `TestNoMessageOfAnotherChannelIsARelayMessage` skips: its bridge-channel fixtures stay in the
Panda AI Browser repository.

Results on 2026-11-20 (Windows, the machine the port was written on):

- **Conformance suite:** 159 tests. 0 failed, 143 passed, 16 skipped. The 15 opt-in load-report rows and the guard's
  "testing relay-go" row are the skips. It was green on every run from T5 on.
- **Black-box obligations (suite README O1-O10).** Go tests cover these on a fake clock:

  | Obligation | Covered by |
  |---|---|
  | O1 idle close | `hub` |
  | O2 send timeout, the close timeout, the keepalive, the ping / pong bounds | `socket` |
  | O3 rollovers, O5 memory caps | `quotas` |
  | O4 hosted forwarding | `hosted` against a fake admin server, and `hub` (one request per socket, the `--max-hosting` cap) |
  | O6 graceful stop | `hub`, `server`: control sockets 1001, a late registration too. Their streams go through the same `closeLink` the suite's `Losing_the_control_socket_closes_every_stream_of_that_relayId_1001` checks |
  | O8 log content | `hub`: the 4504 line once per relayId and kind per hour, no dial token. `hosted`: its lines, no secret. "Other refusals every time" has no Go test |
  | O9 internal listener on loopback, O10 loopback default | `cmd/panda-relay` |

  O7 is measured, not met (see below). **relay-go is not "O1-O10 conformant".**
- **Known answers.** The schema's vectors and fixtures cover:
  - relayId, dial tokens and base64url;
  - relay message parsing, including repeated-member semantics measured on the C# parser;
  - `ConnectionCode.TryParse`, 149 rows measured on .NET (`internal/hosted/testdata`).

  The ECDSA registration proof has no vector. The suite's 4503 cases and its signed-registration ready probe pin it.

## What is ported

| Go | C# |
|---|---|
| `cmd/panda-relay` | `Program.cs` |
| `internal/options` | `RelayOptions.cs`, Program's parser |
| `internal/server` | `RelayServer.cs`, `HelpPage.cs`, `ProxyWarning.cs`, ASP.NET's forwarded headers and Kestrel's path rules |
| `internal/hub` | `RelayHub.cs`: control sockets, streams, hosting dispatch |
| `internal/socket` | `RelaySocket.cs` on gorilla |
| `internal/quotas` | `RelayQuotas.cs`, `NotOnlineLog.cs` |
| `internal/auth` | `RegistrationProof.cs` and the relay's part of `AuthCrypto` / `DialTokens` |
| `internal/hosted` | `HostedAdmin.cs`, `ConnectionCode.TryParse` (code form) |
| `internal/protocol` | generated by `schema/codegen.mjs` (output `go`): never edit `protocol.g.go` by hand |
| `internal/clock`, `internal/logx`, `internal/uripath`, `internal/testkit` | the TimeProvider, the logger, RFC 3986 dot segments, a test Chrome (tests only) |

## Not met, or different (recorded trade-offs)

- **O7, zero allocation per forwarded message: not met.** BenchmarkO7Forward on 2026-11-20 measured:

  | Message size | Allocations per forwarded message |
  |---|---|
  | 1 KiB | 1 (8 B) |
  | 60 000 B | 3 (about 400 B) |

  gorilla allocates a message reader per message, and more for large writes. This needs a framer of our own, which
  is out of this plan.
- **Inputs that only crafted clients send.** For these, Go is stricter or differs slightly:
  - a trailing `\n` in an option value;
  - legacy IPv4 forms (`127.1`) in `--listen` and `X-Forwarded-For`;
  - an oversized text message with invalid UTF-8: Go closes 4513, .NET closes 1007;
  - `%25` inside query keys.
- **Hosted admin client:**
  - The JSON body is escaped differently from System.Text.Json: Go writes `<` as `\u003c` where
    System.Text.Json writes `\u003C`, and Go leaves non-ASCII raw where System.Text.Json escapes it.
    It is the same JSON value.
  - A response charset other than UTF-8 is ignored. C# would throw, which means failed.
  - `ParseCode` follows the measured rows. Beyond them it uses two assumptions (UNVERIFIED): a control character
    anywhere refuses, and a non-ASCII host character must be a letter, digit or mark.
- **Graceful stop:**
  - The listener close and the 1001 closes run together. ASP.NET runs ApplicationStopping first, then stops Kestrel.
  - A registration that completes during the stop is closed 1001 at once. C# leaves it online until the host's 5 s
    cut (1006).
  - A close requested after a socket's Dispose has started is not waited for. This is the same as C#.
- **Load numbers** are below. Compare them only on the same machine, with alternated runs.

## Load report (AC7)

The full table is in `docs/plan-relay-go.md` of the Panda AI Browser repository (Status, T7): relay-go beside the C#
relay, the same machine, alternated
runs, all cores and 2 cores, 3 runs per scenario. It ran on 2026-11-20 on a noisy machine and is not a gate.

Go did better in these cases:

- 100 KiB messages. With all cores, CPU per message was 5.5 ms against 26.7 ms, and p99 was 411 ms against 833 ms.
- A single stream: less CPU and a lower p99.
- Memory per active stream (working set at the end, all cores: 45 MB against 82 MB).

C# did better in these cases:

- The saturated pipelined case: 25 100 msg/s against 12 200 msg/s.
- Memory per idle Chrome: about 53 KB against 82 KB.
- 100 streams of 1 KiB lockstep echo on 2 cores. Go reached 786 msg/s against 4 497, with p50 117 ms. Go used under
  one core there, so it is latency-bound, not CPU-bound. The cause is unknown; it is not GOMAXPROCS.

Some rows wrote no line because of the harness's 10 s silence (the conformance plan's parked finding 4). C# lost 5 of
those rows and Go lost 1.

Verdict: relay-go is **not** a drop-in replacement by the conformance plan's proposed rule.
