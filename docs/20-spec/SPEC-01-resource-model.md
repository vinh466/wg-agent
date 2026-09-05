---
id: SPEC-01
title: Resource model
prefix: RES
status: Accepted
version: 1.7
owner: Vinh Nguyen
created: 2026-08-03
updated: 2026-09-06
depends_on: []
adrs: [ADR-0001, ADR-0005, ADR-0011]
milestone: M0
---

# SPEC-01: Resource model

## 1. Scope

Defines the `Interface` and `Peer` resources: identity, fields, types, defaults, and the
boundary between `spec` and `status`.

**Not in this module:**
- Validation rules → [SPEC-07](SPEC-07-validation.md)
- Field ownership during reconcile → [SPEC-03](SPEC-03-state-reconcile.md)
- `ForwardPolicySpec` and `NatSpec` → [SPEC-02](SPEC-02-forward-policy.md)
- RPC shapes → [SPEC-04](SPEC-04-api-conventions.md)

## 2. General principles

> **REQ-RES-001** — Every resource MUST separate `spec`, the desired state written by
> callers, from `status`, which callers never write.

`status` draws on the kernel and on the agent's own records. The distinction that matters is
authorship, not origin: a caller writes `spec` and reads `status`. `instance_id`, `created_at`,
`ownership` and `revision` are all agent records rather than kernel readings, and they belong to
`status` for that reason.

> **REQ-RES-002** — `Interface` and `Peer` MUST be separate collections.

> **REQ-RES-030** — `InterfaceSpec` MUST NOT contain a peer list.

Rationale: separate collections let Terraform or an operator manage individual peers
without contesting ownership with the interface spec. Atomic replacement of a whole peer
set uses `BatchUpdatePeers` (`REQ-API-034`).

> **REQ-RES-003** — Every write operation MUST be idempotent.

> **REQ-RES-004** — Any IPv6 address or CIDR in a spec MUST be rejected explicitly, per
> `REQ-VAL-020`.

## 3. Interface

### 3.1. Identity

> **REQ-RES-010** — An interface is identified by `name`, which MUST be immutable after
> creation.

> **REQ-RES-011** — `name` MUST match `^[a-zA-Z][a-zA-Z0-9_-]{0,14}$`.

The 15-character limit derives from Linux `IFNAMSIZ = 16`, including the NUL terminator.

### 3.2. InterfaceSpec

| Field | Type | Required | Default | Notes |
|---|---|---|---|---|
| `name` | string | Yes | — | Immutable |
| `private_key` | string (base64) | No | generated | Never readable afterwards |
| `listen_port` | uint32 | No | `0` | `0` lets the kernel choose |
| `addresses` | []string (CIDR) | Yes | — | IPv4 only |
| `mtu` | uint32 | No | `1420` | |
| `fwmark` | uint32 | No | `0` | `0` disables |
| `manage_routes` | bool | No | `true` | `false` matches `Table=off` in wg-quick |
| `forward_policy` | `ForwardPolicySpec` | No | see [SPEC-02](SPEC-02-forward-policy.md) | |
| `nat` | `NatSpec` | No | disabled | |
| `enabled` | bool | No | `true` | `false` keeps the link present but DOWN |
| `labels` | map<string,string> | No | `{}` | Free-form metadata |

> **REQ-RES-012** — When `private_key` is omitted at creation, the agent MUST generate a
> new key.

> **REQ-RES-013** — The agent MUST NOT include `private_key` in any response.

> **REQ-RES-014** — The agent MUST NOT interpret the contents of `labels`.

`labels` is reserved for the platform layer.

### 3.3. InterfaceStatus

| Field | Source |
|---|---|
| `public_key` | Derived from the private key |
| `listen_port` | The port the kernel has bound |
| `instance_id` | UUID assigned when the agent begins managing the link |
| `created_at` | When the agent began managing the link |
| `revision` | Opaque, changes when the spec changes — see `REQ-RES-031` |
| `oper_state` | `UP` \| `DOWN` \| `ABSENT` |
| `ownership` | `MANAGED` \| `FOREIGN` \| `ORPHANED` — see `REQ-RES-017` |
| `peer_count` | Peer count in the kernel |
| `condition` | `READY` \| `PROGRESSING` \| `DEGRADED`, with `reason` and `message` |
| `warnings` | Validation findings, per `REQ-VAL-002` |
| `masquerade_out_interface` | The uplink derived under `REQ-FWD-031`, empty when NAT is off |
| `last_reconcile_at` | Timestamp |

> **REQ-RES-015** — The agent MUST generate a fresh `instance_id` on every successful link
> creation.

> **REQ-RES-018** — The agent MUST assign a fresh `instance_id` to an interface it adopts.

Adoption is not creation, so `REQ-RES-015` does not reach it. An adopted interface still needs
the identifier, because `REQ-RES-026` reads it to tell a counter reset from a running total.

A known imprecision follows, and is accepted rather than solved. Adoption leaves the link
running, so its counters do not reset, while the identifier changes. On a first adoption no
caller holds an earlier sample, so nothing is misread. On a re-adoption after `REQ-RCN-069` a
caller holding an earlier sample sees a changed identifier and infers a reset that did not
happen, overstating the delta once. Retaining the previous identifier is not available: release
removes the interface from the store under `REQ-RCN-071`, so the agent has nothing to retain.

> **REQ-RES-031** — `revision` MUST be an opaque string that a caller compares and returns
> without interpreting.

> **REQ-RES-032** — `status.condition` MUST carry exactly one of `READY`, `PROGRESSING` or
> `DEGRADED`, with a `reason` and a `message`.

> **REQ-RES-033** — `status.warnings` MUST be a list, each entry carrying the reason code and
> message of one validation finding.

`condition` and `warnings` answer different questions, which is why they are two fields rather
than one list. `condition` is the lifecycle state, and the three values are mutually exclusive —
`REQ-API-021` and `REQ-RCN-040` both name `DEGRADED` as one value, not one entry among several.
`warnings` is the output of `REQ-VAL-002`, and a resource can be `READY` while carrying several
of them.

`REQ-RES-031` says opaque because `REQ-API-033` carries the value in an `ETag`, where a client
must not parse it. The agent is free to change how it derives one without that being a contract
change.

> **REQ-RES-017** — `status.ownership` MUST take one of the three values below.

| Value | Meaning |
|---|---|
| `MANAGED` | Present in desired state; the agent enforces the spec |
| `FOREIGN` | A WireGuard link absent from desired state that no deletion record names; left untouched under `REQ-RCN-030` |
| `ORPHANED` | Named by a deletion record whose link survived; awaiting manual cleanup — see `REQ-RCN-034` |

A `FOREIGN` or `ORPHANED` interface has no entry in desired state, so `ListInterfaces` returns
it with `status` populated and `spec` absent. `REQ-RES-001` separates the two precisely so
that this case has a representation.

The three values are decided by desired state and the deletion record, both of which the store
holds. Creation history is not among them, because the agent has no durable memory of it — the
point `REQ-RCN-033` and `REQ-RCN-037` already turn on — and because reconcile step 1 recreates
a link the agent did not originally create.

`condition` and `revision` together already answer whether a caller's write reached the
kernel, because `REQ-API-020` applies a write before responding and `REQ-API-030` changes
`revision` only when the spec changes. A separate `observed_generation` would restate that
with a second identifier and no additional information.

> **REQ-RES-016** — `status.listen_port` MUST report the port the kernel has bound,
> including when `spec.listen_port` is `0`.

> **REQ-RES-019** — The agent MUST derive `oper_state` from the link's administrative flag
> rather than from the operational state the kernel reports.

A WireGuard link reports its operational state as unknown even while it is administratively up,
because it has no carrier to report on. Reading that value would never yield `UP`, so the
administrative flag is the only source that answers the question `oper_state` asks.

## 4. Peer

### 4.1. Identity

> **REQ-RES-020** — A peer is identified by the pair `(interface_name, public_key)`, both
> of which MUST be immutable.

Changing a public key is treated as deleting one peer and creating another. The public key
is the natural key because the kernel itself uses it as the identifier.

> **REQ-RES-021** — In REST paths, `public_key` MUST be encoded as unpadded base64url.

> **REQ-RES-027** — Everywhere other than a REST path, a key field MUST be encoded as standard
> base64 with padding.

The two encodings differ because a path segment cannot carry `/` or `+`. Standard base64 is what
`wg` prints and what a client configuration file contains, so a body that used the path encoding
would not match anything an operator can copy.

### 4.2. PeerSpec

| Field | Type | Required | Default | Notes |
|---|---|---|---|---|
| `interface_name` | string | Yes | — | Immutable |
| `public_key` | string (base64) | Yes | — | Immutable |
| `preshared_key` | string (base64) | No | — | Write-only |
| `allowed_ips` | []string (CIDR) | Yes | — | Cryptokey routing, IPv4 only |
| `endpoint` | string | No | — | `host:port`. A **kernel-owned** field — see `REQ-RCN-013` |
| `persistent_keepalive` | uint32 | No | `0` | Seconds. `0` disables |
| `labels` | map<string,string> | No | `{}` | |

> **REQ-RES-022** — The agent MUST NOT include `preshared_key` in any response.

### 4.3. PeerStatus

| Field | Notes |
|---|---|
| `last_handshake_at` | Null when no handshake has occurred |
| `handshake_age_seconds` | Null when no handshake has occurred |
| `online` | Normalized heuristic — see `REQ-RES-025` |
| `rx_bytes` / `tx_bytes` | Cumulative counters, reset when the link is recreated |
| `resolved_endpoint` | The endpoint held by the kernel |
| `protocol_version` | From the kernel |
| `revision` | Opaque, changes when the spec changes — see `REQ-RES-031` |
| `condition` | `READY` \| `PROGRESSING` \| `DEGRADED`, with `reason` and `message` |
| `warnings` | Validation findings, per `REQ-VAL-002` |

A peer carries the same three fields as an interface. `REQ-VAL-030`, `REQ-VAL-031` and
`REQ-VAL-033` are peer-scoped warning rules, so without `warnings` here their output would have
nowhere to appear.

> **REQ-RES-023** — When a peer has never completed a handshake, `last_handshake_at` and
> `handshake_age_seconds` MUST be null rather than an epoch-zero value.

> **REQ-RES-024** — The agent MUST read `status` directly from the kernel on every call
> rather than accumulating or storing traffic figures.

> **REQ-RES-025** — The agent MUST compute `online` as
> `last_handshake_at != null AND (now - last_handshake_at) < peer_online_threshold`,
> defaulting to 180 seconds.

The 180-second threshold is three times `rekey-after-time` (120 s), tolerating one missed
handshake cycle. Raw `handshake_age_seconds` is always exposed alongside so the platform
can apply its own rule.

## 5. Counter reset detection

Kernel `rx_bytes` and `tx_bytes` accumulate from link creation. Recreating a link — after a
reboot, or when reconcile rebuilds a deleted interface — resets them to zero.

> **REQ-RES-026** — The agent MUST expose `instance_id` so the platform can detect counter
> resets: an unchanged `instance_id` implies `delta = new - old`, while a changed one means
> `delta = new`.

The agent does not compute the delta because traffic accounting is business data owned by
the platform.
