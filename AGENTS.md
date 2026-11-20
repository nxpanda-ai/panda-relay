# Agent instructions

Panda Relay, the public relay of Panda AI Browser. `README.md` has the trust model, the layout and the options.
This repository is exported from the Panda AI Browser repository (`README.md`, "About this copy").

## Commands

Run from `bridge/` (.NET 10 SDK per `global.json`; tests run on Microsoft.Testing.Platform):

| Task | Command |
|---|---|
| Build (warnings are errors) | `dotnet build panda-relay.slnx` |
| All tests | `dotnet test --solution panda-relay.slnx` |
| One project | `dotnet test --project tests/Panda.Relay.Tests` |
| One class | `dotnet test --project tests/Panda.Relay.Tests -- --filter-class "*QuotaTests"` |
| One test | `dotnet test --project tests/Panda.Relay.Tests -- --filter-method "*.<MethodName>"` |
| Conformance suite (own step, never beside another test run) | `dotnet test --project tests/Panda.Relay.Conformance` |
| Load report (explicit tests) | `dotnet test --project tests/Panda.Relay.Conformance -c Release --explicit only` |
| Protocol check (repo root) | `node schema/codegen.mjs --check --only=cs` |
| Regenerate C# (repo root) | `node schema/codegen.mjs --only=cs` |

The Go relay, from `relay-go/` (the toolchain is the `go.mod` one; never build it with `GOTOOLCHAIN=local` on an
older Go):

| Task | Command |
|---|---|
| Vet + all tests | `go vet ./... && go test ./...` |
| Build | `go build -trimpath -o bin/panda-relay ./cmd/panda-relay` (`.exe` on Windows) |
| Conformance against it (from `bridge/`) | `PANDA_RELAY_CMD=<abs path to the binary> PANDA_RELAY_LABEL=relay-go dotnet test --project tests/Panda.Relay.Conformance` |
| Protocol check / regenerate (repo root) | `node schema/codegen.mjs --check --only=go` / `node schema/codegen.mjs --only=go` |

## Security rules (the relay is trusted for availability only)

- Never add an authorisation decision to the relay. Who may drive a Chrome is decided end to end by the agent's
  bridge and the extension (HMAC pairing, encrypted tunnel). The relay routes, admits dials and counts quotas.
- Never log, store or return a connection code, a dial token (keep only its SHA-256), tunnel bytes or a request
  query string. Log through `[LoggerMessage]` methods with relay ids, codes and IPs only.
- Keep the `Microsoft` log filter at Warning and the `LogPin` of the forwarded-headers category (`RelayServer.cs`).
  Below Warning, ASP.NET Core logs request URLs (`?dial=` tokens) and header values.
- Never read or interpret tunnel payloads.
- Client IPs come only from `--trusted-proxy` peers. Never trust a forwarded header from any other peer.

## Code rules

- `bridge/src/Panda.Protocol/Protocol/Protocol.g.cs` is generated. Edit `schema/protocol.v1.json`, then regenerate.
- The same schema generates the Panda app's and the extension's protocol upstream: a schema change is a protocol
  change for them too. Say so in the pull request.
- `Panda.Protocol` must stay trim- and Native-AOT-safe (the analyzers are on; a warning fails the build).
- `Panda.Relay` references `Panda.Protocol` only.
- Keep the `Panda.Bridge.*` namespaces and the `InternalsVisibleTo` entries for assemblies not in this repository.
  The same files build the Panda app.
- Tests: xUnit v3, names `Method_condition_result`. Server ports come from `Ports.Free()` (`tests/Shared/TestPorts.cs`),
  never a fixed port or port 0. Time-dependent code takes a `TimeProvider`; tests use `FakeTimeProvider`.
- `schema/auth-vectors.json` is written by `schema/auth-vectors.py`, an independent implementation. Never edit the
  vectors to make a test pass.
- `schema/relay-vectors.json` is written by `schema/relay-vectors.py` the same way. Never edit it to make a test pass.
- The conformance suite (`tests/Panda.Relay.Conformance`) never references the relay's assembly
  (`ConformanceGuardTests`): it is the contract for any relay, including a port to another language. A behaviour
  change of the relay that a client can see needs a conformance test in the same change. It is not in
  `panda-relay.slnx`: run it on its own after the solution's tests.
- Comments that cite `spec §N`, `decisions.md` or plan names refer to the upstream repository.

## Go relay rules (`relay-go/`)

- It is a port of the C# relay, judged by the same conformance suite. Never edit the suite, the C# relay or
  `Panda.Protocol` to make the Go relay pass.
- `relay-go/internal/protocol/protocol.g.go` is generated (`node schema/codegen.mjs --only=go`). Never edit it by hand.
- `schema/fixtures/golden/*.json` are written by upstream tests that are not in this repository. Never edit them.
- `relay-go/internal/hosted/testdata/connection-codes.txt` holds rows measured on .NET. Never edit it by hand.
- Same security rules as above: no log line holds a connection code, dial token, tunnel byte, query string or header
  value.
- Time-dependent code takes the `clock` package's clock; tests use `clock.Fake`.
- `gofmt -l .` must print nothing (`.gitattributes` keeps `.go` files LF on Windows).

## Forwarding path: zero allocation per message

`RelayHub.PumpAsync` and `RelaySocket` allocate nothing in Panda code per forwarded message (the .NET WebSocket
and Kestrel below them still do).

- `Frame.Bytes` is lent by its `RelaySocket`: valid only until the next receive, drain or dispose on that socket.
  Never keep it longer. A frame over 16 KiB is rented from `ArrayPool` and returned cleared.
- No per-send `CancellationTokenSource` or cancelable token: one timer per socket aborts a stuck send.
- In `PumpAsync`, never call `ReceiveAsync`, `SendAsync` or another `async` helper per message. Its inlined steps
  mirror those two methods: change both copies together.
- `AllocationTests` guards this (a loose gate in Debug). Before a change to this path, run the strict gate:
  `dotnet test --project tests/Panda.Relay.Tests -c Release -- --filter-class "*AllocationTests"`

## Pull requests

- Accepted changes are made upstream and come back with the next export. Keep a change small and focused: no
  reformatting or renaming across files.
- Before opening one: the build has 0 warnings, all tests pass (the conformance suite too, as its own step) and the
  protocol check passes. A change under `relay-go/` also passes `go vet`, `go test` and the conformance suite against
  the Go binary.
- AI-assisted commits carry a `Co-Authored-By:` trailer that names the agent.
