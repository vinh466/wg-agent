---
id: SPEC-04
title: API conventions, concurrency and the error model
prefix: API
status: Accepted
version: 2.0
owner: Vinh Nguyen
created: 2026-08-03
updated: 2026-09-30
depends_on: [SPEC-01]
adrs: [ADR-0011, ADR-0013, ADR-0014, ADR-0015, ADR-0017]
milestone: P2
---

# SPEC-04: API conventions, concurrency and the error model

## 1. Scope

The REST surface, write semantics, concurrency control, the error model, startup and shutdown.

Pagination in section 5.2, revisions and batch writes in section 6, and the adoption operations
of section 7 are delivered after the wrapper of
[ADR-0013](../10-decisions/ADR-0013-drive-wg-and-wg-quick.md); the
[roadmap](../60-planning/roadmap.md) and the [backlog](../60-planning/backlog.md) hold the split.

**Not in this module:**
- Request and response schemas → `api/openapi.yaml`, rendered in [30-api](../30-api/)
- Authentication and authorization → [SPEC-05](SPEC-05-security.md)
- Specific validation rules → [SPEC-07](SPEC-07-validation.md)
- How a write reaches WireGuard → [SPEC-13](SPEC-13-applying-changes.md)

## 2. Contract source of truth

> **REQ-API-001** — `api/openapi.yaml` MUST be the single source of truth for the API contract.

> **REQ-API-060** — API documentation MUST be rendered from `api/openapi.yaml`.

> **REQ-API-003** — A compatibility-breaking change MUST increment the major version in the path
> prefix, from `/v1` to `/v2`.

[ADR-0014](../10-decisions/ADR-0014-rest-api-described-by-openapi.md) fixes the format. The
document is written before the server that serves it, as this specification is, and a contract
test compares what the server exposes with it.

## 3. Service surface

| Resource | Operations |
|---|---|
| Interface | CreateInterface, GetInterface, ListInterfaces, UpdateInterface, DeleteInterface |
| Peer | CreatePeer, GetPeer, ListPeers, UpdatePeer, DeletePeer |
| System | GetHealth, GetVersion |

A resource carries its status under `REQ-RES-001`, so reading one needs no separate status
operation, and `ListPeers` returns every peer of an interface with its status in one call.
`ListInterfaces` returns the interfaces desired state describes — the ones the agent created;
the interfaces of other parties join it with adoption, in backlog B-10.

The operations that arrive later — RotateInterfaceKey, AdoptInterface, ReleaseInterface,
BatchUpdatePeers, GenerateClientConfig, GenerateKeyPair, DiagnoseInterface, GetOverview,
Reconcile, and a streaming WatchPeerStatus — are described by the requirements the
[backlog](../60-planning/backlog.md) lists with them. An operation absent from v1 can be added
without breaking a client, while one frozen in the wrong shape cannot be changed.

## 4. REST mapping

> **REQ-API-063** — The API MUST expose each operation of section 3 at the method and path the
> table below gives.

| Method | Path | Operation |
|---|---|---|
| `POST` | `/v1/interfaces` | CreateInterface |
| `GET` | `/v1/interfaces` | ListInterfaces |
| `GET` | `/v1/interfaces/{name}` | GetInterface |
| `PUT` | `/v1/interfaces/{name}` | UpdateInterface |
| `DELETE` | `/v1/interfaces/{name}` | DeleteInterface |
| `POST` | `/v1/interfaces/{name}/peers` | CreatePeer |
| `GET` | `/v1/interfaces/{name}/peers` | ListPeers |
| `GET` | `/v1/interfaces/{name}/peers/{public_key}` | GetPeer |
| `PUT` | `/v1/interfaces/{name}/peers/{public_key}` | UpdatePeer |
| `DELETE` | `/v1/interfaces/{name}/peers/{public_key}` | DeletePeer |
| `GET` | `/v1/version` | GetVersion |
| `GET` | `/v1/health` | GetHealth |

> **REQ-API-080** — A `public_key` request field MUST carry the encoding of `REQ-RES-027`.

> **REQ-API-081** — The server MUST convert the path segment encoding of `REQ-RES-021` into the
> field encoding of `REQ-API-080`.

Three rows bind `{public_key}` into a request field. A server that passed the segment through
unchanged would compare unpadded base64url from the path with padded standard base64 from the
store, so the same peer would be addressable by two different strings and an equality check
would fail for one of them. Converting at the edge keeps one encoding inside the service.

## 5. Write semantics

> **REQ-API-020** — A write MUST be validated, applied and stored, in that order, before the
> response returns.

> **REQ-API-022** — Applying a change MUST be bounded by `apply_timeout`.

Synchronous application is chosen because a caller — an operator's script or a platform — needs
the outcome within the call. Storing last keeps the store from ever describing a configuration
that failed to apply, which `REQ-APL-008` completes by restoring the previous one.

### 5.1. Update is whole-spec replacement

A single mutation verb per resource removes the class of defect where a partial update cannot
distinguish an omitted field from one deliberately set to its zero value. That distinction
carries meaning throughout this API: `persistent_keepalive = 0` disables keepalive and
`enabled = false` stops the interface. A `PATCH` verb cannot express those cases without a field
mask, so the verb is absent instead.

> **REQ-API-064** — `UpdateInterface` and `UpdatePeer` MUST replace the whole spec of the
> target resource with the supplied one.

> **REQ-API-075** — A spec field a request omits MUST take the default given in
> [SPEC-01](SPEC-01-resource-model.md) section 3.2 or section 4.2.

> **REQ-API-076** — A field whose default differs from its zero value MUST be encoded so that
> an absent value is distinguishable from that zero value.

`REQ-API-076` is the one that cannot be deferred. `enabled` defaults to `true`, `mtu` to 1420 and
`listen_port` to 51820, so a wire format without explicit presence reads an omitted field as
`false` or `0`: with `REQ-API-064` replacing the whole spec, a caller that omits `enabled` would
stop the interface. The fields `REQ-API-076` reaches are `listen_port`, `mtu` and `enabled`;
`manage_routes` joins them when it arrives.

The fields section 5.1 names are a different set: their default is their zero value, so they need
no presence marker.

> **REQ-API-065** — A write-only field omitted from an update MUST retain its stored value.

> **REQ-API-066** — An update supplying an immutable field whose value differs from the stored
> resource MUST be rejected with `FIELD_IMMUTABLE`.

`REQ-API-065` covers `private_key` and `preshared_key`, which no read returns under
`REQ-RES-013` and `REQ-RES-022`. Without it a read-modify-write cycle would erase or
regenerate a key the caller never had the chance to send back. `REQ-API-066` covers `name`,
`interface_name` and `public_key`, rejecting a mismatch rather than silently creating a second
resource.

> **REQ-API-084** — An API response MUST NOT carry an interface's `post_up` or `post_down`.

> **REQ-API-085** — An API write MUST leave an interface's `post_up` and `post_down` as the CLI last
> set them.

The two hook lists run as root, and the API's token crosses the operator's private network in
clear, so under [ADR-0017](../10-decisions/ADR-0017-operator-hooks-through-the-cli.md) they belong
to the CLI alone. `REQ-API-085` is what keeps `REQ-API-064` from erasing them: a whole-spec
replacement arriving over the API carries no hooks, and replacing them with none would silently
undo what the operator set on the node.

> **REQ-API-069** — An operation other than `CreateInterface` MUST reject an interface that
> desired state does not describe with `INTERFACE_NOT_MANAGED`.

> **REQ-API-070** — `GetPeer`, `UpdatePeer` and `DeletePeer` MUST reject a peer that desired state
> does not describe with `PEER_NOT_FOUND`.

> **REQ-API-071** — `CreatePeer` MUST reject a public key already described on that interface
> with `PEER_EXISTS`.

Update is not an upsert. A `PUT` to a name desired state does not describe is far more often a
typo than an intent to create, and `REQ-VAL-015` already refuses the mirror case on create, so
accepting one here would leave the two verbs disagreeing.

`INTERFACE_NOT_MANAGED` is distinct from `INTERFACE_NOT_FOUND`: the link exists on the host but
nobody has asked the agent to manage it. That is a `FOREIGN` interface under `REQ-RES-017`, and
the answer is to adopt it under `REQ-RCN-060` rather than to update it.

### 5.2. Pagination

> **REQ-API-011** — `ListInterfaces`, `ListPeers` and `ListPeerStatus` MUST accept `page_size`
> and `page_token`, and return `next_page_token`.

> **REQ-API-012** — An omitted or zero `page_size` MUST default to 100.

> **REQ-API-013** — A `page_size` above 1,000 MUST be capped at 1,000 rather than rejected.

> **REQ-API-014** — An empty `next_page_token` MUST mean the page returned is the last one.

> **REQ-API-015** — List results MUST be ordered by resource identity, so that paging over a
> changing collection stays stable.

Pagination is specified before the first list call is written because adding these fields
later changes the response message, a breaking change that `REQ-API-003` answers only with a new
path version.
Resource identity is `name` for interfaces and `public_key` for peers, per `REQ-RES-010` and
`REQ-RES-020`.

## 6. Concurrency and idempotency

> **REQ-API-030** — Every resource MUST carry an opaque `revision` that changes whenever its
> spec changes.

> **REQ-API-077** — An interface's `revision` MUST also change when the set of peers on that
> interface changes.

`REQ-API-077` is what makes `REQ-API-072` work. Peers are a separate collection under
`REQ-RES-002`, so an interface revision bound to its own spec alone would not move when a peer
was added, and the batch write it guards would not detect the change it exists to detect. The
cost is that an interface revision turns over more often, which is correct: a peer set is part
of what that interface does.

> **REQ-API-031** — On a mismatched `revision`, the agent MUST return `FAILED_PRECONDITION`
> with reason `REVISION_MISMATCH`, mapped to HTTP `412`.

> **REQ-API-032** — When `revision` is omitted, the agent MUST overwrite unconditionally.

> **REQ-API-033** — REST MUST support `revision` through the `ETag` and `If-Match` headers.

### 6.1. BatchUpdatePeers

> **REQ-API-034** — `BatchUpdatePeers` MUST be atomic: one store transaction and one device
> configuration call, either fully applied or not at all.

> **REQ-API-035** — With `replace_all = true`, the agent MUST delete every peer absent from
> the supplied list.

> **REQ-API-062** — With `replace_all = false`, the agent MUST only upsert.

> **REQ-API-072** — `BatchUpdatePeers` MUST accept the interface `revision` and reject a
> mismatch under `REQ-API-031`.

Without it the one operation that can replace an entire peer set is also the one with no way to
detect that the set changed underneath the caller. `replace_all = true` deletes every peer the
request omits, so a stale read followed by a batch write is how a caller silently removes a peer
somebody else added.

## 7. Error model

> **REQ-API-082** — An error response MUST be an `application/problem+json` body whose `reason`
> member carries the error's reason code.

> **REQ-API-041** — The `reason` member MUST carry one of the values enumerated below.

> **REQ-API-083** — An error response MUST carry the HTTP status the table below assigns to its
> reason code.

Clients branch on the reason code, never on the message. The body is the problem document of
RFC 9457, which a generic HTTP client reads without knowing the codes.

| HTTP | Reason codes |
|---|---|
| 400 | `INTERFACE_NAME_INVALID`, `PUBLIC_KEY_INVALID`, `KEY_INVALID`, `ADDRESSES_REQUIRED`, `ALLOWED_IPS_REQUIRED`, `ALLOWED_IPS_DUPLICATE`, `ALLOWED_IPS_NOT_CANONICAL`, `IPV6_NOT_SUPPORTED`, `LISTEN_PORT_INVALID`, `KEEPALIVE_INVALID`, `MTU_INVALID`, `ENDPOINT_INVALID`, `ENDPOINT_REQUIRED`, `PEER_IS_INTERFACE`, `CLIENT_ADDRESS_MISSING`, `ALLOWED_IPS_DEFAULT_ROUTE`, `HOOK_INVALID`, `CIDR_INVALID`, `FIELD_IMMUTABLE`, `FIELD_UNKNOWN`, `FORWARD_POLICY_NEEDS_UPLINK`, `PEER_INTERFACE_NOT_FOUND`, `ADOPTION_FIELD_REQUIRED` |
| 401 | `TOKEN_INVALID` |
| 404 | `INTERFACE_NOT_FOUND`, `INTERFACE_NOT_MANAGED`, `PEER_NOT_FOUND` |
| 409 | `INTERFACE_EXISTS`, `PEER_EXISTS`, `SUBNET_FULL`, `LISTEN_PORT_IN_USE`, `ADDRESS_CONFLICT`, `INTERFACE_NOT_FOREIGN`, `INTERFACE_NOT_ADOPTED`, `ADOPTION_BLOCKED` |
| 412 | `REVISION_MISMATCH` |
| 500 | `APPLY_FAILED`, `RECONCILE_FAILED` |
| 503 | `STORE_BUSY` |

The startup codes — `MISSING_CAP_NET_ADMIN`, `STORE_CORRUPT`, `STORE_SCHEMA_TOO_NEW`,
`NON_LOOPBACK_BIND`, `SYSCTL_WRITE_DENIED`, `NFTABLES_UNAVAILABLE` — end the process before it
serves, so they appear in its log rather than in a response. The warning codes appear in
`status.warnings` under `REQ-VAL-002`, never as an error.

### 7.1. Reason codes

```
MISSING_CAP_NET_ADMIN       STORE_CORRUPT             STORE_SCHEMA_TOO_NEW
INTERFACE_NAME_INVALID      INTERFACE_NOT_FOUND       INTERFACE_EXISTS
INTERFACE_NOT_MANAGED       LISTEN_PORT_IN_USE        ADDRESS_CONFLICT
PEER_NOT_FOUND              PEER_EXISTS               PUBLIC_KEY_INVALID
ALLOWED_IPS_DUPLICATE       ALLOWED_IPS_OVERLAP       ALLOWED_IPS_OUT_OF_SUBNET
REVISION_MISMATCH           RECONCILE_FAILED          TOKEN_INVALID
NON_LOOPBACK_BIND           NFTABLES_UNAVAILABLE      SYSCTL_WRITE_DENIED
IPV6_NOT_SUPPORTED          FORWARD_POLICY_NEEDS_UPLINK
PEER_INTERFACE_NOT_FOUND    APPLY_FAILED              FIELD_IMMUTABLE
ADOPTION_BLOCKED            INTERFACE_NOT_ADOPTED     INTERFACE_NOT_FOREIGN
ADOPTION_FIELD_REQUIRED     ADDRESSES_REQUIRED        ALLOWED_IPS_REQUIRED
ENDPOINT_REQUIRED           MTU_OUT_OF_RANGE          ENDPOINT_NOT_IP
INTER_INTERFACE_ONE_SIDED   EXTERNAL_WITHOUT_NAT      ALLOWED_PEER_INTERFACES_IGNORED
LISTEN_PORT_INVALID         KEEPALIVE_INVALID         MTU_INVALID
KEY_INVALID                 PEER_IS_INTERFACE         ALLOWED_IPS_NOT_CANONICAL
ENDPOINT_INVALID            CLIENT_ADDRESS_MISSING    ALLOWED_IPS_DEFAULT_ROUTE
HOOK_INVALID                SUBNET_FULL               CIDR_INVALID
STORE_BUSY                  FIELD_UNKNOWN
```

`TOKEN_MISSING` was removed in v1.9. `REQ-SEC-078` treats a missing token and a wrong one
alike, so a second code described a distinction the agent deliberately does not make.
`WG_MODULE_NOT_LOADED` and `KERNEL_TOO_OLD` were removed in v2.0: every supported kernel carries
WireGuard in tree, the agent loads no module itself, and the startup check that produced them is
gone.

Every value has a producing requirement, in both directions: no code is unreachable, and no rule
that has to report one lacks it. `REQ-VAL-010` to `REQ-VAL-050` each name their own code, error
and warning alike, and the startup table of `REQ-API-050` names the codes for the checks it
performs. The rest are named where the behaviour is defined:

| Code | Produced by |
|---|---|
| `INTERFACE_NOT_FOUND` | `REQ-RCN-067` |
| `INTERFACE_EXISTS` | `REQ-VAL-015` |
| `INTERFACE_NOT_MANAGED` | `REQ-API-069` |
| `PEER_NOT_FOUND` | `REQ-API-070` |
| `PEER_EXISTS` | `REQ-API-071` |
| `FIELD_IMMUTABLE` | `REQ-API-066` |
| `APPLY_FAILED` | `REQ-APL-008`, `REQ-APL-011` |
| `STORE_BUSY` | `REQ-RCN-075` |
| `REVISION_MISMATCH` | `REQ-API-031` |
| `RECONCILE_FAILED` | `REQ-RCN-040` |
| `TOKEN_INVALID` | `REQ-SEC-078`, and check 3 of `REQ-API-050` |
| `NON_LOOPBACK_BIND` | `REQ-SEC-083` |
| `SYSCTL_WRITE_DENIED` | `REQ-FWD-025` |
| `NFTABLES_UNAVAILABLE` | the startup checks SPEC-02 brings with it |
| `ALLOWED_IPS_OVERLAP` | `REQ-VAL-030` |
| `ALLOWED_IPS_OUT_OF_SUBNET` | `REQ-VAL-031` |
| `ENDPOINT_REQUIRED` | `REQ-KEY-038` |

The adoption codes have their producing requirements in
[SPEC-03](SPEC-03-state-reconcile.md) section 6.3: `ADOPTION_BLOCKED` in `REQ-RCN-064`,
`INTERFACE_NOT_ADOPTED` in `REQ-RCN-072`, `INTERFACE_NOT_FOREIGN` in `REQ-RCN-074`,
and `ADOPTION_FIELD_REQUIRED` in `REQ-RCN-066`.
`ADDRESSES_REQUIRED` is produced by `REQ-VAL-016` and `ALLOWED_IPS_REQUIRED` by `REQ-VAL-017`.

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
> early with the reason code given for the check that failed.

| # | Check | Reason on failure |
|---|---|---|
| 1 | The store opens and is writable | `STORE_CORRUPT` |
| 2 | The store schema is one this build understands (`REQ-RCN-005`) | `STORE_SCHEMA_TOO_NEW` |
| 3 | A token is configured (`REQ-SEC-072`) | `TOKEN_INVALID` |
| 4 | `CAP_NET_ADMIN` is held | `MISSING_CAP_NET_ADMIN` |

A startup failure carries a reason code for the same purpose a request failure does: an
installer or a unit log should be able to branch on the cause without matching message text.
Checks 1 and 2 are separate because a corrupt store and a store from a newer build call for
opposite actions — restore one, downgrade nothing. The modules delivered later bring their own
checks: the forwarding sysctl and `nf_tables` with [SPEC-02](SPEC-02-forward-policy.md), the
metrics bind address with [SPEC-08](SPEC-08-observability.md).

> **REQ-API-051** — `/v1/health` MUST report success only after every startup check passes.

> **REQ-API-078** — `GetVersion` MUST report the agent version, the commit it was built from,
> the process start time and its uptime.

A single endpoint covers both liveness and readiness because the agent runs under systemd
rather than an orchestrator that distinguishes them. Splitting it later adds a path without
changing this one, so the simpler form carries no cost to reverse.

## 9. Shutdown

> **REQ-API-073** — On `SIGTERM` the agent MUST stop accepting new requests and complete the
> requests in flight before exiting.

> **REQ-API-074** — Shutdown MUST NOT alter the kernel state of any interface it manages.

`REQ-API-074` is the property that makes an upgrade safe, and it is the same one the whole
design rests on: the data plane runs independently of the agent, so stopping the agent is not
stopping the tunnel. Under ADR-0013 the interfaces belong to their `wg-quick@` units, which the
agent's own shutdown leaves running. A shutdown that tore interfaces down would turn every package
upgrade into an outage.

## 10. Removed requirements

Removed in v2.0 by [ADR-0014](../10-decisions/ADR-0014-rest-api-described-by-openapi.md), which
makes the API REST alone, and [ADR-0013](../10-decisions/ADR-0013-drive-wg-and-wg-quick.md),
which applies a write synchronously:

~~**REQ-API-002**~~ — REST mapping declared with `google.api.http` annotations. No `.proto`
contract remains; `REQ-API-063` states the mapping.

~~**REQ-API-010**~~ — `ListPeerStatus` reporting every peer in one call. The operation is gone;
`ListPeers` returns each peer with its status.

~~**REQ-API-021**~~ — A failed application answered by `DEGRADED` under HTTP `202`. A failed
application is restored and reported under `REQ-APL-008`.

~~**REQ-API-040**~~ — Errors as `google.rpc.Status` carrying `ErrorInfo`. Replaced by
`REQ-API-082`.

~~**REQ-API-079**~~ — Structured error payloads in `google.rpc.Status.details`. No payload in v1
needs one; a problem document takes extension members when one does.

~~**REQ-API-061**~~ — CI blocking compatibility-breaking changes within a version. Removed in v2.0:
the agent is an internal tool with no published client, and `REQ-API-003` governs a breaking
change by hand.

## 11. Open questions

None.
