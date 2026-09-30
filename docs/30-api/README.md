# API contract

> **This directory holds GENERATED documentation. Never hand-edit it.**

The source of truth is `api/openapi.yaml` — see `REQ-API-001` in
[SPEC-04](../20-spec/SPEC-04-api-conventions.md) and
[ADR-0014](../10-decisions/ADR-0014-rest-api-described-by-openapi.md).

## State

Nothing is generated, and the document itself is not yet written. It is written at the start of
phase P2 of the [roadmap](../60-planning/roadmap.md), before the server that serves it, and this
directory then holds its rendering. The `.proto` files of the earlier contract were removed with
the change that adopted OpenAPI, so the repository never holds a contract `REQ-API-001` does not
name.

## What the contract must hold

The properties below cannot be corrected within `v1`, because a wrong field or cardinality is a
breaking change, so the contract test asserts them:

- every route of `REQ-API-063` is present, and no other
- `listen_port`, `mtu` and `enabled` carry explicit presence under `REQ-API-076`
- identity is outside `spec` and `status` under `REQ-RES-034`
- `last_handshake_at` and `handshake_age_seconds` are nullable under `REQ-RES-023`
- a public key is unpadded base64url in a path and padded standard base64 in a body, under
  `REQ-RES-021` and `REQ-RES-027`
- an error is a problem document carrying `reason`, with the status of `REQ-API-083`
- a write that restarted an interface says so, under `REQ-APL-007`
- the response to `CreatePeer` with a generated key pair carries the client configuration, under
  `REQ-KEY-042`
- no field reaches a write-only value — `private_key`, `preshared_key` — in any response, under
  `REQ-RES-013` and `REQ-RES-022`

The operations that arrive later — among them `WatchPeerStatus`, a stream — are absent from the
document until their requirements return from the [backlog](../60-planning/backlog.md); an
operation added later breaks no client.

## Compatibility checking

Blocking a compatibility-breaking change within a version is `REQ-API-061`, deferred under
`B-05`. Until then `REQ-API-003` governs by hand: a breaking change moves the path to `/v2`.
