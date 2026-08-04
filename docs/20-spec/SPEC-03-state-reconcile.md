---
id: SPEC-03
title: Desired state and reconcile
prefix: RCN
status: Accepted
version: 1.1
owner: Vinh Nguyen
created: 2026-08-03
updated: 2026-08-04
depends_on: [SPEC-01, SPEC-02]
adrs: [ADR-0001]
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

> **REQ-RCN-002** — The store MUST hold only `spec` values and `instance_id`.

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
11. Write status and observed_generation
```

> **REQ-RCN-023** — The agent MUST use incremental peer updates rather than whole-list
> replacement, except during `BatchUpdatePeers` with `replace_all = true` and during a full
> reconcile.

## 6. Unmanaged interfaces

> **REQ-RCN-030** — A WireGuard interface present on the host but absent from desired state
> MUST NOT be deleted or modified by the agent.

> **REQ-RCN-031** — The agent MUST report such an interface in `ListInterfaces` with
> `status.managed = false`.

Deleting resources created by another party is unacceptable behavior for an agent.

## 7. Error handling

> **REQ-RCN-040** — When application fails, the agent MUST retain the stored desired state
> and mark the resource `DEGRADED` with a `reason`.

> **REQ-RCN-041** — The agent MUST retry using exponential backoff with jitter between
> `backoff_min` and `backoff_max`.

> **REQ-RCN-042** — The agent MUST serialize all operations on a single interface behind a
> per-interface lock.

## 8. Open questions

- Full-reconcile duration at 10,000 peers, which determines the default
  `reconcile.interval`. See [open questions](../60-planning/open-questions.md), OQ-04.
