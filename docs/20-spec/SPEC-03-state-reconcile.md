---
id: SPEC-03
title: Desired state and reconcile
prefix: RCN
status: Accepted
version: 1.3
owner: Vinh Nguyen
created: 2026-08-03
updated: 2026-09-05
depends_on: [SPEC-01, SPEC-02]
adrs: [ADR-0001, ADR-0011]
milestone: M1
---

# SPEC-03: Desired state and reconcile

## 1. Scope

Durable storage of desired state, the reconcile algorithm, and **field ownership** — which
fields the agent enforces and which the kernel owns.

**Not in this module:**
- Backup, restore, upgrade migration → [SPEC-10](SPEC-10-lifecycle.md)
- Specific nftables rules → [SPEC-02](SPEC-02-forward-policy.md)
- Drift metrics → [SPEC-08](SPEC-08-observability.md)

## 2. Store

> **REQ-RCN-001** — The agent MUST persist desired state to disk and restore it on startup.

> **REQ-RCN-002** — The store MUST hold only `spec` values, `instance_id` and the deletion
> records defined in section 6.

> **REQ-RCN-050** — The store MUST NOT hold `status` values or any traffic counter.

> **REQ-RCN-003** — Every store write MUST occur inside a transaction.

> **REQ-RCN-004** — The store file MUST have mode `0600` and be owned by the account
> running the agent.

> **REQ-RCN-005** — On encountering a schema version newer than it understands, the agent
> MUST refuse to start with a clear message rather than misinterpreting the data.

Implementation: bbolt — pure Go, no cgo, single file, transactional.

## 3. Field ownership

This is the most defect-prone part of the declarative model. Without an explicit
classification, reconciliation destroys runtime behavior established by the kernel.

> **REQ-RCN-010** — Every spec field MUST be classified as either agent-owned or
> kernel-owned.

| Class | Meaning | Reconcile behavior |
|---|---|---|
| **agent-owned** | The agent is the source of truth | Enforced on **every** reconcile; a difference is drift |
| **kernel-owned** | The kernel updates it at runtime | Written only when the **spec changes**; a difference is not drift |

| Field | Class |
|---|---|
| `private_key`, `listen_port`, `fwmark` | agent-owned |
| `addresses`, `mtu`, `enabled` | agent-owned |
| `allowed_ips`, `preshared_key`, `persistent_keepalive` | agent-owned |
| `forward_policy`, `nat` | agent-owned |
| **`endpoint`** | **kernel-owned** |

> **REQ-RCN-011** — The agent MUST enforce every agent-owned field on each reconcile pass.

> **REQ-RCN-012** — The agent MUST NOT treat a difference in a kernel-owned field as drift.

> **REQ-RCN-013** — The agent MUST apply `spec.endpoint` only at peer creation or when that
> field changes in the spec.

> **REQ-RCN-051** — Reconcile MUST NOT overwrite an endpoint the kernel has learned.

`REQ-RCN-013` and `REQ-RCN-051` exist to preserve **roaming**. WireGuard learns a peer's
endpoint from a valid handshake, which is what lets a client move from WiFi to cellular
without losing the tunnel. Treating an endpoint difference as drift and rewriting it every
cycle would disconnect roaming clients repeatedly, with a symptom that is very hard to
trace.

## 4. Reconcile triggers

> **REQ-RCN-020** — The agent MUST trigger reconciliation on each of the events below.

| Source | Scope |
|---|---|
| After every API write | The affected interface |
| Agent startup | All interfaces |
| Periodic timer, default 30 s | All interfaces |
| Netlink event reporting a link deleted or brought down | The affected interface |
| Explicit `Reconcile` RPC | As requested |

> **REQ-RCN-021** — The agent MUST subscribe to netlink events to detect externally deleted
> links rather than relying on the periodic timer alone.

## 5. Algorithm

> **REQ-RCN-022** — For each interface in desired state, the agent MUST perform the steps
> below in order.

```
 1. Link present?             → no: create it
 2. Link of type wireguard?   → no: mark DEGRADED and leave it untouched
 3. Device config matches?    → no: reconfigure (delta)
 4. Peer set matches?         → diff by public key:
                                 present in kernel only → remove
                                 missing                → add
                                 agent-owned difference → update (delta)
 5. Addresses match?          → add and remove per diff
 6. MTU matches?              → set
 7. manage_routes enabled?    → sync routes with the union of allowed_ips
 8. spec.enabled?             → bring up or down
 9. Forwarding sysctl matches?→ set per SPEC-02 section 5
10. nftables rules match?     → sync table inet wg_agent
11. Write status and conditions
```

> **REQ-RCN-036** — Each full reconcile pass MUST classify every WireGuard link absent from
> desired state as `FOREIGN` or `ORPHANED`, per section 6.

> **REQ-RCN-023** — The agent MUST use incremental peer updates rather than whole-list
> replacement, except during `BatchUpdatePeers` with `replace_all = true` and during a full
> reconcile.

## 6. Interfaces outside desired state

A WireGuard link on the host that desired state does not describe falls into one of two cases,
and conflating them costs an operator real time: a `FOREIGN` link belongs to somebody else and
must never be touched, while an `ORPHANED` link is the agent's own leftover and wants cleaning
up.

### 6.1. Foreign interfaces

> **REQ-RCN-030** — A WireGuard interface present on the host that the agent never created
> and that desired state does not describe MUST NOT be deleted or modified by the agent.

> **REQ-RCN-031** — The agent MUST report such an interface in `ListInterfaces` with
> `status.ownership = FOREIGN`.

Deleting resources created by another party is unacceptable behavior for an agent. The
qualifier matches the title of this section: an interface enters desired state only through
adoption under section 6.3, which is an explicit operator action. A link nobody has asked for
stays untouchable.

### 6.2. Deletion and orphans

> **REQ-RCN-032** — `DeleteInterface` MUST remove the link from the kernel and the spec from
> the store.

> **REQ-RCN-038** — Deleting an interface MUST delete its peers from the store within the same
> transaction.

> **REQ-RCN-033** — When link removal does not complete, the agent MUST retain a deletion
> record naming that interface.

> **REQ-RCN-034** — An interface holding a deletion record whose link still exists MUST be
> reported with `status.ownership = ORPHANED`.

> **REQ-RCN-035** — The agent MUST NOT delete or modify an orphaned link on its own.

> **REQ-RCN-037** — The agent MUST clear the deletion record once the link is absent.

The deletion record is what distinguishes an orphan from a foreign link across a restart:
without it the agent has no memory that the link was ever its own. Automatic cleanup of
orphans is deliberately absent — an operator removes the link, and the record clears itself on
the next pass under `REQ-RCN-037`. Reclaiming orphans automatically is a candidate
enhancement, not v1 behavior.

### 6.3. Adoption

Adoption is the only path from `FOREIGN` to `MANAGED`. It reads an existing link and its peers
into desired state without disturbing the traffic already flowing through it, per
[ADR-0011](../10-decisions/ADR-0011-operator-initiated-adoption.md).

> **REQ-RCN-060** — The agent MUST bring an existing link under management only in response to
> an explicit adoption request naming that interface.

> **REQ-RCN-061** — Adoption MUST populate the interface spec from kernel state, storing the
> existing private key rather than generating one.

> **REQ-RCN-062** — Adoption MUST store every peer present in the kernel as part of the
> adopted interface's desired state.

> **REQ-RCN-063** — Adoption MUST NOT store a peer endpoint, which stays kernel-owned under
> `REQ-RCN-051`.

> **REQ-RCN-064** — Adoption MUST leave the store unchanged when the readiness report of
> `REQ-DIA-040` reports a blocking finding.

> **REQ-RCN-065** — Adoption MUST write the interface and its peers in a single transaction.

Every field originates in the kernel, so nothing is reconstructed:

| Spec field | Source |
|---|---|
| `private_key`, `listen_port`, `fwmark` | WireGuard device dump |
| `addresses`, `mtu`, `enabled` | netlink link and address attributes |
| Peer `public_key`, `preshared_key`, `allowed_ips`, `persistent_keepalive` | The same device dump |

`REQ-RCN-061` is what keeps established clients connected: an interface key that survives
adoption leaves every client configuration valid. Generating one instead would disconnect every
peer as onboarding completes, which is the outcome `REQ-KEY-004` warns about for rotation.

No separate ownership transition is needed. `REQ-RES-017` defines `MANAGED` as presence in
desired state, so writing the spec is what changes `status.ownership`.

A correctly adopted interface makes the next reconcile pass a no-op: steps 3 through 8 of
`REQ-RCN-022` compare desired state against the kernel state it was just read from. An adoption
that would not converge silently is therefore an adoption that was incomplete.

## 7. Error handling

> **REQ-RCN-040** — When application fails, the agent MUST retain the stored desired state
> and mark the resource `DEGRADED` with a `reason`.

> **REQ-RCN-041** — The agent MUST retry using exponential backoff with jitter between
> `backoff_min` and `backoff_max`.

> **REQ-RCN-042** — The agent MUST serialize all operations on a single interface behind a
> per-interface lock.

## 8. Open questions

None. The scale targets that set the default `reconcile.interval` are fixed by `REQ-LIF-040`
in [SPEC-10](SPEC-10-lifecycle.md).
