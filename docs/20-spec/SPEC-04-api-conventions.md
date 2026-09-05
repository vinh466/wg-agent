---
id: SPEC-04
title: API conventions, concurrency and the error model
prefix: API
status: Accepted
version: 1.7
owner: Vinh Nguyen
created: 2026-08-03
updated: 2026-09-05
depends_on: [SPEC-01]
adrs: [ADR-0003, ADR-0009, ADR-0011]
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
| `InterfaceService` | Create, Get, List, Update, Delete, RotateKey, Adopt, Release |
| `PeerService` | Create, Get, List, Update, Delete, BatchUpdate |
| `RuntimeService` | GetInterfaceStatus, ListPeerStatus, WatchPeerStatus |
| `ConfigService` | GenerateClientConfig, GenerateKeyPair |
| `DiagnosticsService` | DiagnoseInterface, GetOverview — see [SPEC-11](SPEC-11-diagnostics.md) |
| `SystemService` | GetHealth, GetVersion, Reconcile |

> **REQ-API-010** — REST MUST provide a polling endpoint equivalent to `WatchPeerStatus`.

`ListPeerStatus` is that endpoint. `WatchPeerStatus` exists only on gRPC, because a streaming
RPC has no faithful REST equivalent and a polling caller is served by the list form.

## 4. REST mapping

> **REQ-API-063** — Every RPC in section 3 apart from `WatchPeerStatus` MUST carry the REST
> mapping given in the table below.

| Method | Path | RPC |
|---|---|---|
| `POST` | `/v1/interfaces` | CreateInterface |
| `GET` | `/v1/interfaces` | ListInterfaces |
| `GET` | `/v1/interfaces/{name}` | GetInterface |
| `PUT` | `/v1/interfaces/{name}` | UpdateInterface |
| `DELETE` | `/v1/interfaces/{name}` | DeleteInterface |
| `POST` | `/v1/interfaces/{name}:rotateKey` | RotateInterfaceKey |
| `POST` | `/v1/interfaces/{name}:adopt` | AdoptInterface |
| `POST` | `/v1/interfaces/{name}:release` | ReleaseInterface |
| `GET` | `/v1/interfaces/{name}/status` | GetInterfaceStatus |
| `GET` | `/v1/interfaces/{name}:diagnose` | DiagnoseInterface |
| `POST` | `/v1/interfaces/{name}/peers` | CreatePeer |
| `GET` | `/v1/interfaces/{name}/peers` | ListPeers |
| `GET` | `/v1/interfaces/{name}/peers:status` | ListPeerStatus |
| `GET` | `/v1/interfaces/{name}/peers/{public_key}` | GetPeer |
| `PUT` | `/v1/interfaces/{name}/peers/{public_key}` | UpdatePeer |
| `DELETE` | `/v1/interfaces/{name}/peers/{public_key}` | DeletePeer |
| `POST` | `/v1/interfaces/{name}/peers:batchUpdate` | BatchUpdatePeers |
| `POST` | `/v1/interfaces/{name}/peers/{public_key}:generateConfig` | GenerateClientConfig |
| `POST` | `/v1/keys:generate` | GenerateKeyPair |
| `POST` | `/v1/reconcile` | Reconcile |
| `GET` | `/v1/version` | GetVersion |
| `GET` | `/v1/overview` | GetOverview |
| `GET` | `/v1/health` | GetHealth |

`ListPeerStatus` uses the `:status` custom-method form rather than a `/status` path segment,
which would otherwise match the `{public_key}` variable in the sibling route. `Reconcile`
takes its scope from the request body, so a single collection-level path covers both the
whole-node and single-interface cases in `REQ-RCN-020`.

## 5. Write semantics

> **REQ-API-020** — A write MUST be applied to the kernel before returning, in the order:
> validate, store, apply, re-read status, respond.

> **REQ-API-021** — When application fails, the agent MUST retain the desired state and
> return the resource with `condition = DEGRADED` under HTTP `202`.

> **REQ-API-022** — Apply duration MUST be bounded by `reconcile.apply_timeout`.

Synchronous application is chosen because Terraform providers need the outcome within the
call. The reconcile loop is a safety net rather than the primary execution path.

### 5.1. Update is whole-spec replacement

A single mutation verb per resource removes the class of defect where a partial update cannot
distinguish an omitted field from one deliberately set to its zero value. That distinction
carries meaning throughout this API: `listen_port = 0` asks the kernel to choose,
`fwmark = 0` disables the mark, `persistent_keepalive = 0` disables keepalive, and
`enabled = false` holds the link down. A `PATCH` verb over proto3 scalars cannot express those
four cases without a field mask, so the verb is absent instead.

> **REQ-API-064** — `UpdateInterface` and `UpdatePeer` MUST replace the whole spec of the
> target resource with the supplied one.

> **REQ-API-065** — A write-only field omitted from an update MUST retain its stored value.

> **REQ-API-066** — An update supplying an immutable field whose value differs from the stored
> resource MUST be rejected with `INVALID_ARGUMENT`.

`REQ-API-065` covers `private_key` and `preshared_key`, which no read returns under
`REQ-RES-013` and `REQ-RES-022`. Without it a read-modify-write cycle would erase or
regenerate a key the caller never had the chance to send back. `REQ-API-066` covers `name`,
`interface_name` and `public_key`, rejecting a mismatch rather than silently creating a second
resource.

### 5.2. Pagination

> **REQ-API-011** — `ListInterfaces`, `ListPeers` and `ListPeerStatus` MUST accept `page_size`
> and `page_token`, and return `next_page_token`.

> **REQ-API-012** — An omitted or zero `page_size` MUST default to 100.

> **REQ-API-013** — A `page_size` above 1,000 MUST be capped at 1,000 rather than rejected.

> **REQ-API-014** — An empty `next_page_token` MUST mean the page returned is the last one.

> **REQ-API-015** — List results MUST be ordered by resource identity, so that paging over a
> changing collection stays stable.

Pagination is specified before the first list call is written because adding these fields
later changes the response message, which `REQ-API-061` blocks within a package version.
Resource identity is `name` for interfaces and `public_key` for peers, per `REQ-RES-010` and
`REQ-RES-020`.

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
| `UNAUTHENTICATED` | 401 | Missing or unrecognized token |
| `PERMISSION_DENIED` | 403 | Insufficient role |
| `NOT_FOUND` | 404 | No such resource |
| `ALREADY_EXISTS` | 409 | Duplicate creation |
| `FAILED_PRECONDITION` | 412 / 400 | Revision mismatch, module not loaded, port in use |
| `INTERNAL` | 500 | Unexpected failure |
| `UNAVAILABLE` | 503 | Kernel temporarily unresponsive |

`RESOURCE_EXHAUSTED` is absent because no requirement produces it. Per-principal rate
limiting is deferred; see the scope table in [product.md](../00-overview/product.md).

### 7.1. Reason codes

```
WG_MODULE_NOT_LOADED        KERNEL_TOO_OLD            MISSING_CAP_NET_ADMIN
INTERFACE_NAME_INVALID      INTERFACE_NOT_FOUND       INTERFACE_EXISTS
INTERFACE_NOT_MANAGED       LISTEN_PORT_IN_USE        ADDRESS_CONFLICT
PEER_NOT_FOUND              PEER_EXISTS               PUBLIC_KEY_INVALID
ALLOWED_IPS_DUPLICATE       ALLOWED_IPS_OVERLAP       ALLOWED_IPS_OUT_OF_SUBNET
REVISION_MISMATCH           RECONCILE_FAILED          TOKEN_MISSING
TOKEN_INVALID               NON_LOOPBACK_BIND         STORE_SCHEMA_TOO_NEW
NFTABLES_UNAVAILABLE        STORE_CORRUPT
IPV6_NOT_SUPPORTED          FORWARD_POLICY_NEEDS_UPLINK
PEER_INTERFACE_NOT_FOUND    SYSCTL_WRITE_DENIED
ADOPTION_BLOCKED            INTERFACE_NOT_ADOPTED     INTERFACE_NOT_FOREIGN
ADOPTION_FIELD_REQUIRED     PEER_NOT_REPRESENTABLE    ADDRESSES_REQUIRED
```

The adoption codes have their producing requirements in
[SPEC-03](SPEC-03-state-reconcile.md) section 6.3: `ADOPTION_BLOCKED` in `REQ-RCN-064`,
`INTERFACE_NOT_ADOPTED` in `REQ-RCN-072`, `INTERFACE_NOT_FOREIGN` in `REQ-RCN-074`,
`ADOPTION_FIELD_REQUIRED` in `REQ-RCN-066` and `PEER_NOT_REPRESENTABLE` in `REQ-RCN-068`.
`ADDRESSES_REQUIRED` is produced by `REQ-VAL-016`.

> **REQ-API-067** — An `ADOPTION_BLOCKED` status MUST carry the findings of `REQ-DIA-040` for
> the named interface.

> **REQ-API-068** — `AdoptInterface` MUST accept a validate-only mode that returns the spec the
> request would store without writing it.

`REQ-API-067` is what lets a caller act on a refusal: the reason code says the adoption was
blocked, and the findings say by what. `REQ-API-068` gives an API caller the preview that
`REQ-CLI-006` gives an operator, so discovering a blocking finding does not require attempting
the write. Both carry write-only fields redacted under `REQ-RES-013` and `REQ-RES-022`. The findings behind it travel
in the response rather than in the reason code, because `REQ-DIA-042` through `REQ-DIA-046`
classify more conditions than a closed code set should carry, and `REQ-DIA-030` already gives
each one a stable `hint_code`.

## 8. Startup checks

> **REQ-API-050** — The agent MUST complete the checks below before serving requests, failing
> early with a clear message otherwise.

1. The kernel supports WireGuard, or the module can be loaded
2. `CAP_NET_ADMIN` is held
3. The store opens, is writable, and its schema is compatible
4. Forwarding sysctl is writable (`REQ-FWD-025`)
5. When NAT is enabled, `nf_tables` is available
6. When the HTTP listener is enabled, its bind address is loopback and at least one token is
   configured (`REQ-SEC-070`, `REQ-SEC-072`)

> **REQ-API-051** — `/v1/health` MUST report success only after every startup check passes and
> the first reconcile pass completes.

A single endpoint covers both liveness and readiness because the agent runs under systemd
rather than an orchestrator that distinguishes them. Splitting it later adds a path without
changing this one, so the simpler form carries no cost to reverse. `/v1/health` answers yes or
no; `GET /v1/overview` under `REQ-DIA-020` is what names the component that failed.

## 9. Open questions

None.
