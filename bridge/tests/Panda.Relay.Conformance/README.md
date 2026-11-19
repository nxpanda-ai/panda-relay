# Relay conformance suite

Black-box tests of a relay **executable**, over HTTP and WebSocket only: the contract of spec §78 and
`schema/protocol.v1.json` (relay messages, relay close codes). The suite never references the relay's assembly
(`ConformanceGuardTests`), so a relay written in another language (a Go or Rust port, say) runs the same tests. By
default it tests the C# relay built from this repository.

## Run

From `bridge/`:

```
dotnet test --project tests/Panda.Relay.Conformance
```

The build builds the C# relay first; the suite starts `dotnet <bridge>/src/Panda.Relay/bin/<Configuration>/net10.0/panda-relay.dll`.
The project is in no solution (`Panda.slnx` upstream, `panda-relay.slnx` in the relay repository): it starts dozens of
relay processes, and inside the parallel solution run that load made other suites' real-clock tests fail. Run it as
its own step, not beside another suite.

Another relay:

| Variable | Meaning |
|---|---|
| `PANDA_RELAY_CMD` | Path of a **native executable**. Scripts are refused (`.cmd`, `.bat`, `.ps1`, `.sh`, or a `#!` first line): the suite must own the relay process itself to kill it. |
| `PANDA_RELAY_LABEL` | A name for it in the output (default: the file name). |

While `PANDA_RELAY_CMD` is set, one test is reported **skipped** with the reason `testing <label>: <cmd>`, so every run
summary says which relay it tested. The relay must accept
`--public-host <host:port> --listen 127.0.0.1 --port <n>` plus the flags a test passes (the relay README, "Options").

## How a test relay runs

- One relay per test class; a test that needs flags or a fresh per-IP budget starts its own.
- Each process gets a unique public host `r<8 hex>.relay.test:443`. Ready = the process is alive and a Chrome
  registration signed for that host succeeds (so a test never talks to another process on a reused port); limit 30 s.
  A process that exits before ready is started again on a new port, at most twice; the third early exit fails with
  every exit code and its stderr.
- Windows: every relay is in one Job object that kills it when the test process ends, even on a crash.
  **Linux has no equivalent here: a crashed test host can leave relays running** (`pkill -f panda-relay` or your
  binary's name).
- Stop: the sockets the test opened are closed normally, the output is given up to 3 s to go quiet, the process is
  killed and its stdout / stderr read to the end. Then **G6**: no token, streamId, relayId of the help page or payload
  marker used against that relay may appear in its output (a failure gives the secret's number, its length and the
  number of the output line, never the secret or the line). A relay that exited on its own before its tests ended fails too (its exit code only).
- Restarts and other harness notes are xunit diagnostic messages (`xunit.runner.json`: on). At most 8 test classes run
  at once, which caps how many relays are alive together.
- Tests that count per clock hour or UTC day wait out a boundary closer than their margin (2 min; 4 min for the two
  600-count tests), so a count never straddles it. A run started in the last minutes of an hour is that much longer.
- The relays are started with `DOTNET_gcServer=0` (workstation GC): a test-environment choice for the C# relay, which
  ships with server GC (a GC thread per core); when the suite still ran inside the full solution run, dozens of
  server-GC relays went silent for over 10 s (cause not proven). A relay that is not .NET ignores it.
- Every connect, send, receive and close handshake has a timeout (10 s, longer where a relay timer is awaited): a
  relay that accepts TCP and never answers fails a test, it never hangs the run.

## Coverage

| Group | Classes | What |
|---|---|---|
| G1 HTTP | `HttpSurfaceTests` | `/c/<relayId>` help page with its exact headers; every other path / method 404; plain HTTP on a socket path 400 |
| G2 control socket | `ControlSocketTests`, `RegistrationLimitTests` | Origin rules, challenge → REGISTER → REGISTERED, 4500 / 4503 / 4507 / 4513, `?id=`, PING, DIAL_TOKENS, RELAY_HOST without a hosted bridge, 601st registration from one IP → 4530 |
| G3 streams | `StreamTests` | dial → incoming → accept → byte-exact binary both ways (1 B, 64 KiB, 512 KiB, 200 in a row); 4501 / 4504 / 4508 / 4509 / 4500 / 4513; normal close 1000; 1001 when the control socket goes |
| G4 limits | `QuotaTests`, `RefusedDialLimitTests`, `NoiseBudgetTests`, `ClientIpTests`, `InternalListenerTests` | daily byte / stream quotas → 4529, check order, refused-dial and noise limits per IP → 4530, which client address counts (trusted proxy, header, right-most, one hop, IPv6 /64), the internal listener |
| G5 CLI | `CliTests`, `CliProxyTests`, `CliHostedTests` | a command line the relay cannot run with exits non-zero (quotas, internal port, hosting cap, a trusted proxy that could be anyone, malformed hosting options); a hosted token file is never printed |
| G6 output | every relay the suite starts | no secret used against the relay appears in its output |

A stream's slot (one per Chrome and kind) is free again once the close handshake of the stream holding it has ended:
`StreamTests` dials again right after it, so a relay that frees the slot later fails with 4509.

Close-code reasons that carry a wait are checked as decimal seconds to the end of the UTC day (4529) or the clock hour
(4530). Forwarded client addresses come from documentation ranges only (203.0.113.0/24, 198.51.100.0/24, 2001:db8::/32).

Every test of the C# relay's own suite (`tests/Panda.Relay.Tests`) is mapped to a test here, an obligation below, or
"not applicable" with the reason: [TRACEABILITY.md](TRACEABILITY.md).

## Load report

Not a gate: numbers to compare relays on the same machine. Explicit tests, never part of a normal run:

```
dotnet test --project tests/Panda.Relay.Conformance -c Release --explicit only
```

| Variable | Meaning |
|---|---|
| `PANDA_LOAD_REPORT` | File the JSON lines are appended to (default `load-report.jsonl` beside the test assembly). |
| `PANDA_RELAY_AFFINITY` | Start the relay on this many cores (the harness pins itself while it starts the relay, then checks the relay's own affinity). Windows only: on Linux run `dotnet test` under `taskset` (harness and relay share the cores; such lines say `affinity: null` and `cores: n` and never compare with `affinity` lines). |

Each scenario runs 3 times, each time on a NEW relay started as shipped (no `DOTNET_gcServer` override), with
`--bytes-per-day 1000000000000 --streams-per-day 1000000`. Every Chrome pings every 20 s (the 60 s idle close).

| Scenario | What |
|---|---|
| `echo-1x1KiB`, `echo-100x1KiB`, `echo-100x100KiB` | that many streams (one Chrome each), lockstep: send one message, wait for its echo, again |
| `pipelined-100x1KiB` | 100 streams, each keeping 16 messages in flight (saturation) |
| `idle-1000` | 1 000 Chromes register (each from its own `X-Forwarded-For` address in 198.18.0.0/15, `--trusted-proxy 127.0.0.1`) and stay idle |

Echo scenarios: 3 s warm-up, then a 10 s window. One JSON object per line; MB = 10^6 bytes:

| Field | Meaning |
|---|---|
| `label`, `cmd` | the relay (`cmd` is a file name, never a path) |
| `scenario`, `streams`, `size`, `mode`, `run` | the row |
| `seconds` | the window (idle: the time the 1 000 registrations took) |
| `messages`, `msgPerSec` | round trips completed in the window |
| `mbPerSec` | payload through the relay, both directions (round trips × size × 2) |
| `rttP50Ms`, `rttP99Ms`, `rttMaxMs` | round-trip times in the window |
| `relayCpuSec`, `relayCpuUsPerMsg` | the relay PID's CPU over the window; per **forwarded** message (2 per round trip) — the main figure |
| `relayWsStartMb`, `relayWsEndMb`, `relayWsPeakMb` | the relay's working set at window start / end, peak sampled every 100 ms |
| `bytesPerConn` | idle: working-set growth per registered Chrome (2 s after the last registration) |
| `harnessCpuSec` | the test process's CPU over the window (the load generator's share of the machine) |
| `cores`, `affinity`, `processorCountEnv`, `os`, `cpu`, `gcServer` | the machine and runtime the line holds for |

Per-message fields are null when a scenario has no messages. Compare lines only from the same machine with the same
`cores` / `affinity` / scenario; alternate runs of the two relays.

## Obligations (not testable black-box)

A relay passing this suite still has to show these by its own tests or by review. Each is a behaviour the C# relay's
in-process tests check with a fake clock, a fake hosted bridge or a memory probe, which a black-box run cannot do in
reasonable time or at all. No CLI flag was added to make them testable: the CLI is an operator surface.

- **O1 Idle close.** A control socket with no message for 60 s is closed 1001.
- **O2 Send timeout.** A peer that stops reading is cut off once a send has been stuck for 30 s, and not before.
- **O3 Rollovers.** Per-IP counts start again at each clock hour, per-Chrome quotas at each UTC midnight; a refusal
  "until the day / hour ends" ends then.
- **O4 URL-only hosting.** RELAY_HOST / RELAY_ROTATE / RELAY_UNHOST → RELAY_HOST_RESULT with a hosted bridge (`--hosted-admin-url`,
  `--hosted-admin-token-file`, `--hosted-mcp-url`): forwarding, error mapping, one request per socket, the
  `--max-hosting` cap (UNHOST never held back by it), timeouts with an unknown outcome.
- **O5 Memory caps.** Every per-IP / per-relayId table is capped (tracked IPs, /48 prefixes, offline relayIds seen,
  4504 log lines); past a cap new entries share one bucket or are counted every time, never grow the table.
- **O6 Graceful stop.** On SIGTERM every control socket is closed 1001 before the process exits.
- **O7 Zero allocation.** Forwarding a stream message allocates nothing per message (the C# relay measures it; see
  the relay's AGENTS.md).
- **O8 Log content.** What is logged: an offline relayId's 4504 once per relayId and kind per hour (with the previous
  hour's count), other refusals every time, a trusted proxy without a usable header warned once a minute with a
  count and never a header value. The suite checks only that no secret appears (G6).
- **O9 Internal listener on loopback.** `--internal-port` listens on 127.0.0.1 whatever `--listen` says.
- **O10 Loopback by default.** Without `--listen` the relay listens on loopback only (the suite always passes it).
