---
id: ADR-0014
title: A REST API whose contract is an OpenAPI document
status: Proposed
owner: Vinh Nguyen
created: 2026-09-29
updated: 2026-09-29
supersedes: [ADR-0003]
superseded_by: null
affects: [SPEC-01, SPEC-04, SPEC-12]
---

# ADR-0014: A REST API whose contract is an OpenAPI document

## Context

[ADR-0003](ADR-0003-protobuf-source-of-truth.md) makes the `.proto` files the contract, served
as gRPC and, through `google.api.http` annotations, as REST. The operator of v1 asked for a REST
API; gRPC was offered and not chosen.

Measured on .NET 10 NativeAOT, recorded as K-14 of the
[specification audit](../60-planning/spec-audit.md):

- The only first-party route from a `.proto` to REST, JSON transcoding, raises 39 trim and AOT
  warnings — reflection-based serialisation, `MakeGenericType` — which the repository's
  `TreatWarningsAsErrors` turns into a failed build.
- A plaintext endpoint set to HTTP/1.1 and HTTP/2 serves HTTP/1.1 only, so gRPC and REST
  cannot share one.
- Minimal APIs with source-generated JSON publish with no warning.

No code implements the `.proto` files, and no client depends on them.

## Alternatives considered

### A — gRPC with JSON transcoding, as ADR-0003
- For: one contract, two surfaces
- Against: does not build under the warning policy; two endpoints; the REST side is the one
  the operator wants, and it is the fragile one

### B — gRPC only
- For: generated clients in every language; streaming
- Against: not what the operator asked for; `curl` cannot call it

### C — REST only, the contract an OpenAPI document
- For: any HTTP client and `curl`; no AOT warning; the error body, paths and `ETag` semantics
  SPEC-04 already specifies stay meaningful
- Against: no streaming; clients are generated from OpenAPI rather than protobuf; conformance
  has to be tested because no code is generated from the document

## Decision

Adopt **C**.

- `api/openapi.yaml`, OpenAPI 3.1, is the single source of truth for the API contract. It is
  written before the code that serves it, as the specification is.
- The server is ASP.NET Core Minimal APIs with source-generated JSON serialisation.
- A contract test compares the routes, parameters and schemas the server exposes with the
  document, so a divergence fails the build rather than a client.
- The `.proto` files are removed when this ADR is accepted.

## Consequences

### Positive
- The API the operator calls is the one that builds cleanly under NativeAOT
- One listener serves the whole surface
- `curl` is a complete client, which is how the operator works by hand

### Negative — the price paid
- Streaming — the `WatchPeerStatus` of the backlog — needs server-sent events or a WebSocket
  rather than a gRPC stream
- Clients generated from OpenAPI are of more uneven quality across languages than protobuf's
- The document and the code can drift; only the contract test stops them

### Follow-on work
- Author `api/openapi.yaml` for the v1 surface; strike `REQ-API-002` and the requirements that
  exist only for the gRPC mapping, and amend `REQ-API-001`, `REQ-API-040` and `REQ-API-060`
- Replace `docs/30-api/` with a rendering of the OpenAPI document

## Conditions for revisiting

- A consumer that needs streaming the REST surface cannot serve adequately.
- A platform integration that requires gRPC.
- First-party REST generation from `.proto` that builds under NativeAOT without warnings.
