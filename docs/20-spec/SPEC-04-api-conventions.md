---
id: SPEC-04
title: API conventions, concurrency and the error model
prefix: API
status: Accepted
version: 1.1
owner: Vinh Nguyen
created: 2026-08-03
updated: 2026-08-04
depends_on: [SPEC-01]
adrs: [ADR-0003]
milestone: M0
---

# SPEC-04: API conventions, concurrency and the error model

## 1. Scope

Service surface, REST mapping, write semantics, concurrency control, error model.

**Not in this module:**
- Detailed message definitions → `api/proto/wgagent/v1/`, rendered in [30-api](../30-api/)
- Authentication and authorization → [SPEC-05](SPEC-05-security.md)
- Specific validation rules → [SPEC-07](SPEC-07-validation.md)

## 2. Contract source of truth

> **REQ-API-001** — `api/proto/wgagent/v1/*.proto` MUST be the single source of truth for
> the API contract.

> **REQ-API-060** — API documentation and client code MUST be generated from the `.proto`
> definitions.

> **REQ-API-002** — REST mapping MUST be declared with `google.api.http` annotations inside
> the `.proto` rather than in separate configuration.

> **REQ-API-003** — A compatibility-breaking change MUST increment the protobuf package
> version.

> **REQ-API-061** — CI MUST block compatibility-breaking changes within a package version.

## 3. Service surface

| Service | RPCs |
|---|---|
| `InterfaceService` | Create, Get, List, Update, Replace, Delete, RotateKey |
| `PeerService` | Create, Get, List, Update, Replace, Delete, BatchUpdate |
| `RuntimeService` | GetInterfaceStatus, ListPeerStatus, WatchPeerStatus |
| `ConfigService` | GenerateClientConfig, GenerateKeyPair |
| `DiagnosticsService` | DiagnoseInterface — see [SPEC-11](SPEC-11-diagnostics.md) |
| `SystemService` | GetHealth, GetVersion, Reconcile |

> **REQ-API-010** — REST MUST provide a polling endpoint equivalent to `WatchPeerStatus`.

`WatchPeerStatus` itself may exist only on gRPC.

## 4. REST mapping

| Method | Path | RPC |
|---|---|---|
| `POST` | `/v1/interfaces` | CreateInterface |
| `GET` | `/v1/interfaces` | ListInterfaces |
| `GET` | `/v1/interfaces/{name}` | GetInterface |
| `PUT` | `/v1/interfaces/{name}` | ReplaceInterface |
| `PATCH` | `/v1/interfaces/{name}` | UpdateInterface |
| `DELETE` | `/v1/interfaces/{name}` | DeleteInterface |
| `POST` | `/v1/interfaces/{name}:rotateKey` | RotateInterfaceKey |
| `GET` | `/v1/interfaces/{name}/status` | GetInterfaceStatus |
| `GET` | `/v1/interfaces/{name}:diagnose` | DiagnoseInterface |
| `POST` | `/v1/interfaces/{name}/peers` | CreatePeer |
| `GET` | `/v1/interfaces/{name}/peers` | ListPeers |
| `GET` | `/v1/interfaces/{name}/peers/{public_key}` | GetPeer |
| `PUT` | `/v1/interfaces/{name}/peers/{public_key}` | ReplacePeer |
| `PATCH` | `/v1/interfaces/{name}/peers/{public_key}` | UpdatePeer |
| `DELETE` | `/v1/interfaces/{name}/peers/{public_key}` | DeletePeer |
| `POST` | `/v1/interfaces/{name}/peers:batchUpdate` | BatchUpdatePeers |
| `POST` | `/v1/interfaces/{name}/peers/{public_key}:generateConfig` | GenerateClientConfig |
| `GET` | `/v1/healthz`, `/v1/readyz` | GetHealth |

## 5. Write semantics

> **REQ-API-020** — A write MUST be applied to the kernel before returning, in the order:
> validate, store, apply, re-read status, respond.

> **REQ-API-021** — When application fails, the agent MUST retain the desired state and
> return the resource with `condition = DEGRADED` under HTTP `202`.

> **REQ-API-022** — Apply duration MUST be bounded by `reconcile.apply_timeout`.

Synchronous application is chosen because Terraform providers need the outcome within the
call. The reconcile loop is a safety net rather than the primary execution path.

## 6. Concurrency and idempotency

> **REQ-API-030** — Every resource MUST carry an opaque `revision` that changes whenever its
> spec changes.

> **REQ-API-031** — On a mismatched `revision`, the agent MUST return `FAILED_PRECONDITION`,
> mapped to HTTP `412`.

> **REQ-API-032** — When `revision` is omitted, the agent MUST overwrite unconditionally.

> **REQ-API-033** — REST MUST support `revision` through the `ETag` and `If-Match` headers.

### 6.1. BatchUpdatePeers

> **REQ-API-034** — `BatchUpdatePeers` MUST be atomic: one store transaction and one device
> configuration call, either fully applied or not at all.

> **REQ-API-035** — With `replace_all = true`, the agent MUST delete every peer absent from
> the supplied list.

> **REQ-API-062** — With `replace_all = false`, the agent MUST only upsert.

## 7. Error model

> **REQ-API-040** — Errors MUST be returned as `google.rpc.Status` carrying `ErrorInfo`.

> **REQ-API-041** — `ErrorInfo.reason` MUST be one of the values enumerated below.

Clients distinguish errors by reason code rather than by parsing message strings.

| gRPC | HTTP | Case |
|---|---|---|
| `INVALID_ARGUMENT` | 400 | Malformed spec |
| `PERMISSION_DENIED` | 403 | Insufficient role |
| `NOT_FOUND` | 404 | No such resource |
| `ALREADY_EXISTS` | 409 | Duplicate creation |
| `FAILED_PRECONDITION` | 412 / 400 | Revision mismatch, module not loaded, port in use |
| `RESOURCE_EXHAUSTED` | 429 | Limit exceeded |
| `INTERNAL` | 500 | Unexpected failure |
| `UNAVAILABLE` | 503 | Kernel temporarily unresponsive |

### 7.1. Reason codes

```
WG_MODULE_NOT_LOADED        KERNEL_TOO_OLD            MISSING_CAP_NET_ADMIN
INTERFACE_NAME_INVALID      INTERFACE_NOT_FOUND       INTERFACE_EXISTS
INTERFACE_NOT_MANAGED       LISTEN_PORT_IN_USE        ADDRESS_CONFLICT
PEER_NOT_FOUND              PEER_EXISTS               PUBLIC_KEY_INVALID
ALLOWED_IPS_DUPLICATE       ALLOWED_IPS_OVERLAP       ALLOWED_IPS_OUT_OF_SUBNET
REVISION_MISMATCH           RECONCILE_FAILED          PLAINTEXT_TRANSPORT_REJECTED
NFTABLES_UNAVAILABLE        STORE_CORRUPT             STORE_SCHEMA_TOO_NEW
IPV6_NOT_SUPPORTED          FORWARD_POLICY_NEEDS_UPLINK
PEER_INTERFACE_NOT_FOUND    SYSCTL_WRITE_DENIED
```

## 8. Startup checks

> **REQ-API-050** — The agent MUST complete the checks below before serving requests, failing
> early with a clear message otherwise.

1. The kernel supports WireGuard, or the module can be loaded
2. `CAP_NET_ADMIN` is held
3. The store opens, is writable, and its schema is compatible
4. Forwarding sysctl is writable (`REQ-FWD-025`)
5. When NAT is enabled, `nf_tables` is available
6. When mTLS is enabled, the certificate, key and CA are readable and unexpired

> **REQ-API-051** — `/readyz` MUST report success only after every startup check passes and
> the first reconcile pass completes.

## 9. Open questions

- Per-principal request rate limiting: in scope for v1 or not. See
  [open questions](../60-planning/open-questions.md), OQ-03.
