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
| Protocol check (repo root) | `node schema/codegen.mjs --check --only=cs` |
| Regenerate C# (repo root) | `node schema/codegen.mjs --only=cs` |

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
- Comments that cite `spec §N`, `decisions.md` or plan names refer to the upstream repository.

## Pull requests

- Accepted changes are made upstream and come back with the next export. Keep a change small and focused: no
  reformatting or renaming across files.
- Before opening one: the build has 0 warnings, all tests pass and the protocol check passes.
- AI-assisted commits carry a `Co-Authored-By:` trailer that names the agent.
