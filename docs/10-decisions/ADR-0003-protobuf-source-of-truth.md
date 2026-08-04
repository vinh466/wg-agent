---
id: ADR-0003
title: Protobuf as the source of truth, gRPC plus a REST gateway
status: Accepted
owner: Vinh Nguyen
created: 2026-08-03
updated: 2026-08-04
supersedes: []
superseded_by: null
affects: [SPEC-04]
---

# ADR-0003: Protobuf as the source of truth

## Context

The consumers are diverse: Terraform providers (Go), Kubernetes operators (Go), VPN
platforms in any language, and manual operational scripting with curl.

Terraform providers and command-line tooling work most conveniently against REST.
Internal services and streaming flows suit gRPC. The original concept listed
"REST/gRPC" without stating where the contract is defined.

## Alternatives considered

### A — REST only, OpenAPI written by hand
- For: simplest, easy to debug with curl
- Against: no streaming; the contract drifts from the implementation; client SDKs must be
  written separately

### B — gRPC only
- For: a strict contract, streaming, good performance
- Against: awkward for manual operations; Terraform providers need an extra translation
  layer

### C — Protobuf as the source of truth, generating both
`.proto` with `google.api.http` annotations; `buf` generates the gRPC server, the
grpc-gateway and OpenAPI.

- For: defined once and used for both; the two cannot drift because both are generated;
  client SDKs come free in several languages; streaming available where needed
- Against: a `buf`/`protoc` toolchain must be set up; generated code enlarges the
  repository

## Decision

Adopt **alternative C**.

Setting up the toolchain is a one-time cost. Maintaining two divergent API definitions is
a permanent cost, and divergence is always discovered late — typically after a consumer
has relied on the wrong one.

Generated code is **committed to the repository** so builds do not depend on the network
and so contract changes are visible in review.

## Consequences

### Positive
- `WatchPeerStatus` uses gRPC streaming while REST retains an equivalent polling endpoint
- Breaking contract changes appear directly in the `.proto` diff
- `buf breaking` blocks compatibility breaks in CI

### Negative — the price paid
- Contributors must install `buf`
- A few REST behaviors do not map cleanly from gRPC, such as partial status codes, and
  need explicit handling

### Follow-on work
- `api/proto/wgagent/v1/` is the source of truth; `docs/30-api/` is **generated** and
  must never be hand-edited
- Versioning follows the protobuf package: a compatibility break moves to `v2`

## Conditions for revisiting

If only one consumer class remains and the other transport goes unused, drop it to reduce
the maintenance surface.
