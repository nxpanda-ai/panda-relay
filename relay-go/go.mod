module github.com/nxpanda-ai/panda-relay/relay-go

go 1.25

toolchain go1.27.4 // govulncheck 2026-11-20: go1.25.3 reached 29 stdlib vulnerabilities, some fixed only on 1.26+

require github.com/gorilla/websocket v1.5.3
