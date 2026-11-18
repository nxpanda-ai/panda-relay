# Panda Relay

The public relay of **Panda AI Browser**. Panda is a Chrome extension plus a local bridge that lets MCP clients
(Claude Code, Cursor, Codex, Gemini CLI, …) control the user's own running Chrome. Panda is a browser-control layer
only and has no AI inside. The relay lets an agent on one network reach a Chrome on another network. It needs no
accounts: a person pastes a one-time connection code from the extension into their agent.

This repository contains the relay server (`panda-relay`), the protocol library it shares with the Panda app, their
tests, and the protocol schema. It does not contain the extension or the Panda app (`panda-browser`).

## Trust model

- **The relay is trusted for availability only.** It routes connections by a Chrome's relay id. It admits a dial by
  the SHA-256 hash of a dial token, so the token itself is never stored. It also counts per-Chrome and per-IP quotas.
  The relay never decides who may drive a Chrome.
- **Authorisation and confidentiality are end to end.** The agent-side bridge and the extension pair with a mutual
  HMAC handshake that uses single-use nonces. They talk through a tunnel that only they can open: ECDH P-256 keys,
  bound to the pairing secret, and AES-256-GCM with replay counters. The relay sees relay ids, message sizes, timings
  and IP addresses. It does not see URLs, page content, commands or answers.
- **The relay never logs** the connection code, dial tokens or tunnel bytes. It also pins ASP.NET Core's
  forwarded-headers log category at Warning or above, whatever the logging configuration says.
- **URL-only mode is optional.** A *hosted bridge* runs next to the relay: the Panda app in server mode, which is
  not part of this repository. The relay passes an extension's hosting requests to it and tells the extension its MCP
  URL; the reverse proxy routes `/mcp` to it (`handle /mcp` in `docs/deploy/caddy/Caddyfile`). In this mode the
  server can read and control that Chrome. Each user turns it on for one Chrome, with an explicit click next to a
  text that says so. The `--hosted-*` options below and the `panda-hosted` lines in `docs/deploy/caddy/` refer to
  it. Without them the relay runs in end-to-end mode only.

## Layout

```
bridge/                      the .NET solution (the folder name comes from the Panda repository)
  panda-relay.slnx
  src/Panda.Relay/           the relay: ASP.NET Core, assembly panda-relay
  src/Panda.Protocol/        shared with the Panda app: protocol messages (generated), AuthCrypto, dial tokens,
                             connection codes, the relay address rules, a log-level floor
  tests/Panda.Relay.Tests/   relay tests (xUnit v3, Microsoft.Testing.Platform)
  tests/Panda.Protocol.Tests/  AuthCrypto against schema/auth-vectors.json, connection codes
  tests/Shared/              test helpers (free ports, repository root)
  VERSION                    the Panda release this copy comes from
schema/
  protocol.v1.json           the protocol: one source of truth for C# and TypeScript
  codegen.mjs                generates bridge/src/Panda.Protocol/Protocol/Protocol.g.cs (and the extension's TS)
  auth-vectors.json / .py    test vectors from an independent Python implementation
docs/deploy/caddy/           a systemd unit and a Caddyfile for the relay behind Caddy
```

## Build and test

You need the .NET 10 SDK (`bridge/global.json`) and Node.js for the schema check.

```
cd bridge
dotnet build panda-relay.slnx
dotnet test --solution panda-relay.slnx
cd ..
node schema/codegen.mjs --check --only=cs
```

`Protocol.g.cs` is generated. After you change `schema/protocol.v1.json`, run `node schema/codegen.mjs --only=cs`.
Never edit the generated file by hand. Warnings are errors, and `Panda.Protocol` builds with the trimming and Native
AOT analyzers on (the Panda app ships it in a Native AOT executable).

## Run

```
dotnet run --project bridge/src/Panda.Relay -- --public-host relay.example.com
```

By default the relay listens on plain HTTP at `127.0.0.1:8080`. Put a TLS-terminating proxy in front of it; see
`docs/deploy/caddy/Caddyfile`. Endpoints:

| Path | Use |
|---|---|
| `/v1/browser` | WebSocket control socket of an extension |
| `/v1/stream` | WebSocket tunnel streams (the extension's side, and an agent's dial with `?dial=`) |
| `/c/<relayId>` | a static help page for the link form of a connection code (the code stays in the URL fragment, which is never sent to the server) |

Options:

```
panda-relay --public-host <host[:port]> [--listen <ip>] [--port <n>] [--bytes-per-day <n>] [--streams-per-day <n>]
            [--trusted-proxy <ip|cidr>]... [--client-ip-header <name>] [--max-hosting <n>] [--internal-port <n>]
            [--noise-per-ip-per-hour <n>]
            [--hosted-admin-url <http://127.0.0.1:port> --hosted-admin-token-file <path> --hosted-mcp-url <https://host/mcp>]
```

- `--public-host` is the lowercase `host[:port]` that extensions dial. Extensions sign it when they register.
- `--trusted-proxy` names the proxies whose `X-Forwarded-For` header (or `--client-ip-header`) is believed. Quotas
  are counted per client IP.
- `--internal-port` opens a loopback-only listener for the hosted bridge.
- The three `--hosted-*` options turn on URL-only mode. Give all three or none.

To publish a self-contained Linux build:
`dotnet publish bridge/src/Panda.Relay -c Release -r linux-x64 --self-contained`.

## About this copy

This repository is exported from the Panda AI Browser repository by a script, as a byte-for-byte copy of committed
files. That is why:

- comments cite plans, spec sections (`spec §78`) and `decisions.md` of that repository;
- the namespaces of `Panda.Protocol` are `Panda.Bridge.*`, because the same files build the Panda app;
- `InternalsVisibleTo` names assemblies that are not in this repository.

Issues and pull requests are welcome. Accepted changes are made upstream and come back with the next export.

## License

MIT. See `LICENSE`.
