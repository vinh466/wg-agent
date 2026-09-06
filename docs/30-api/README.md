# API contract

> **This directory is GENERATED. Never hand-edit it.**

The source of truth is `api/proto/wgagent/v1/*.proto` — see `REQ-API-001` in
[SPEC-04](../20-spec/SPEC-04-api-conventions.md).

## Regenerating

```bash
make proto
```

The target runs `buf generate` inside a container carrying a pinned toolchain, so the output
does not depend on what happens to be installed on the machine.

| Output | Location |
|---|---|
| Go gRPC server and client | `gen/go/wgagent/v1/` |
| grpc-gateway (REST) | `gen/go/wgagent/v1/` |
| OpenAPI v2 specification | `docs/30-api/openapi.swagger.yaml` |

grpc-gateway generates OpenAPI v2. The project has no v3 generator, so v2 is what this
publishes rather than what an earlier version of this page claimed.

Generated code is **committed** so builds do not depend on the network and contract changes are
visible in review.

## Compatibility checking

```bash
make proto-check
```

That lints the contract and runs `buf breaking --against '.git#branch=main'`. CI blocks a
compatibility-breaking change within a package version, per `REQ-API-061`.

Two lint rules are excepted in `buf.yaml`: `RPC_REQUEST_RESPONSE_UNIQUE` and
`RPC_RESPONSE_STANDARD_NAME`. Both require a dedicated response message per RPC, while
returning the resource itself is what AIP-131 prescribes and what the REST table of
`REQ-API-063` implies — `GET /v1/interfaces/{name}` answers with an `Interface`.

## What the generated contract is checked for

`gen/gen_test.go` asserts the properties `REQ-API-061` freezes, because a wrong field
cardinality cannot be corrected within `v1`:

- `mtu`, `manage_routes` and `enabled` carry explicit presence under `REQ-API-076`, while
  `listen_port` and `fwmark` do not
- identity is outside `spec` and `status` under `REQ-RES-034`
- `handshake_age_seconds` is nullable under `REQ-RES-023`
- the adoption request can express omission under `REQ-RCN-066`
- every REST route of `REQ-API-063` reached the gateway

## State

Generated and committed. `WatchPeerStatus` is deliberately absent from the surface: nothing
specified its request scope, its stream element or what emitted an event, and an RPC frozen in
the wrong shape cannot be changed within `v1`. It arrives with the watch work deferred under
`B-05` in the [backlog](../60-planning/backlog.md).
