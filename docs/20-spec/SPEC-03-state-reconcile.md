---
id: SPEC-03
title: Desired state and reconcile
prefix: RCN
status: Accepted
version: 1.9
owner: Vinh Nguyen
created: 2026-08-03
updated: 2026-09-06
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

> **REQ-RCN-002** — The store MUST hold only `spec` values, `instance_id`, `created_at`,
> `revision`, the deletion records defined in section 6 and the adoption records defined in
> section 6.3.

`revision` is stored rather than derived. Deriving it from the spec — a hash, say — would make a
spec changed from A to B and back to A reproduce its earlier value, and `REQ-API-031` would then
accept a write from a caller holding a stale read. A stored value that only ever moves forward
has no such case.

> **REQ-RCN-050** — The store MUST NOT hold `status` values or any traffic counter.

> **REQ-RCN-003** — Every store write MUST occur inside a transaction.

> **REQ-RCN-004** — The store file MUST have mode `0600` and be owned by the account
> running the agent.

> **REQ-RCN-005** — On encountering a schema version newer than it understands, the agent
> MUST refuse to start with a clear message rather than misinterpreting the data.

> **REQ-RCN-006** — The agent MUST hold an exclusive lock on the store for as long as it is
> serving.

> **REQ-RCN-007** — A process writing the store without the agent MUST acquire that lock and
> fail while another process holds it.

The lock is what makes a write outside the agent safe. `REQ-CLI-002` lets several subcommands
reach the store directly, which is the only way to act on a node before the agent has ever
started; without a lock, one of them running beside a live agent would write behind its back
and lose whichever change reconcile wrote next.

An advisory lock on the store file is preferred to asking whether the agent is running. It
guards the resource rather than a proxy for it, so it also serialises two commands against each
other, and the kernel releases it when a holder dies — a socket or a pid file left behind by a
crash answers the question wrongly.

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

Every transition below is a store write. `REQ-RES-017` decides the state from two facts the
store holds — whether desired state describes the link, and whether a deletion record names it —
so no transition depends on remembering which party created the link.

```mermaid
stateDiagram-v2
  [*] --> FOREIGN: a link appears that<br/>desired state does not describe
  [*] --> MANAGED: CreateInterface

  FOREIGN --> MANAGED: AdoptInterface<br/>REQ-RCN-060, REQ-RCN-074
  MANAGED --> FOREIGN: ReleaseInterface<br/>REQ-RCN-069, adoption record required

  MANAGED --> [*]: DeleteInterface, link removed<br/>REQ-RCN-032
  MANAGED --> ORPHANED: DeleteInterface, link survived<br/>REQ-RCN-033, REQ-RCN-034
  ORPHANED --> [*]: operator removes the link,<br/>record clears — REQ-RCN-037

  note right of FOREIGN
    Never deleted or modified
    REQ-RCN-030
  end note
  note right of ORPHANED
    Never deleted or modified either
    REQ-RCN-035
  end note
```

Two properties are worth reading off the diagram. Release returns an interface to `FOREIGN`
rather than to `ORPHANED`, because it writes no deletion record. And `MANAGED` is the only state
with more than one exit, which is why `REQ-RCN-071` clears the adoption record on the transaction
that removes the spec rather than on one named exit.

### 6.1. Foreign interfaces

> **REQ-RCN-030** — A WireGuard interface that desired state does not describe and that no
> deletion record names MUST NOT be deleted or modified by the agent.

> **REQ-RCN-031** — The agent MUST report such an interface in `ListInterfaces` with
> `status.ownership = FOREIGN`.

Deleting resources created by another party is unacceptable behavior for an agent. Both
qualifiers are facts the store holds, which is what makes the rule decidable after a restart:
the agent cannot know whether it created a link, only whether desired state describes it and
whether a deletion record names it. `REQ-RCN-035` protects an orphan under its own rule, so the
two cases stay distinct. A link nobody has asked for stays untouchable.

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

> **REQ-RCN-074** — Adoption MUST reject a request naming an interface whose `status.ownership`
> is not `FOREIGN` with `INTERFACE_NOT_FOREIGN`.

> **REQ-RCN-061** — Adoption MUST populate the interface spec from kernel state, storing the
> existing private key and every address the link carries rather than a subset.

> **REQ-RCN-062** — Adoption MUST store every peer present in the kernel as part of the
> adopted interface's desired state.

> **REQ-RCN-063** — Adoption MUST NOT store a peer endpoint, which stays kernel-owned under
> `REQ-RCN-051`.

> **REQ-RCN-066** — Adoption MUST source `forward_policy`, `nat` and `manage_routes` from the
> adoption request, rejecting a request that omits any of them with `ADOPTION_FIELD_REQUIRED`.

> **REQ-RCN-067** — Adoption MUST reject a request naming a link absent from the host with
> `INTERFACE_NOT_FOUND`.

> **REQ-RCN-064** — When the report of `REQ-DIA-040` carries a `FAIL` finding for the named
> interface that `REQ-VAL-001` does not itself reject, adoption MUST fail with
> `ADOPTION_BLOCKED` and leave the store unchanged.

> **REQ-RCN-065** — Adoption MUST write the interface, its peers and its adoption record in a
> single transaction.

> **REQ-RCN-069** — The agent MUST support removing an adopted interface from desired state
> while leaving its link in the kernel.

> **REQ-RCN-070** — Adoption MUST retain an adoption record naming the interface and holding
> the forwarding sysctl value that `REQ-FWD-024` records.

> **REQ-RCN-071** — The agent MUST clear an interface's adoption record in the same
> transaction that removes its spec from the store.

> **REQ-RCN-073** — Releasing an interface MUST restore the forwarding sysctl of `REQ-FWD-024`
> and delete its peers from the store before that transaction commits.

> **REQ-RCN-072** — The agent MUST reject a release request naming an interface that holds no
> adoption record with `INTERFACE_NOT_ADOPTED`.

The kernel supplies the fields it holds; the request supplies the ones it does not:

| Spec field | Source |
|---|---|
| `private_key`, `listen_port`, `fwmark` | WireGuard device dump |
| `addresses`, `mtu`, `enabled` | netlink link and address attributes |
| Peer `public_key`, `preshared_key`, `allowed_ips`, `persistent_keepalive` | The same device dump |
| `forward_policy`, `nat`, `manage_routes` | The adoption request, under `REQ-RCN-066` |
| `instance_id` | Assigned under `REQ-RES-018` |

`REQ-RCN-066` exists because the kernel holds no forward policy and no routing intent, so a
default would be a guess applied to a live node. Taking the defaults of `REQ-FWD-001` and of
`manage_routes` in [SPEC-01](SPEC-01-resource-model.md) would let `REQ-FWD-003` turn an axis
into a drop rule that step 10 of `REQ-RCN-022` installs on traffic that was passing, and would
let step 7 act on routes the previous manager never asked for. Naming them in the request keeps
adoption a decision rather than a side effect, for the same reason `REQ-VAL-015` refuses an
implicit takeover.

`REQ-RCN-061` is what keeps established clients connected: an interface key that survives
adoption leaves every client configuration valid. Generating one instead would disconnect every
peer as onboarding completes, which is the outcome `REQ-KEY-004` warns about for rotation. The
key is stored, never returned — `REQ-RES-013` and `REQ-KEY-002` apply to an adopted interface
exactly as they do to a created one.

`REQ-RCN-063` accepts a known loss. A device dump does not distinguish an endpoint the kernel
learned from a handshake from one an operator configured for a site-to-site peer, so adoption
cannot tell which to keep, and keeping the wrong one would fight `REQ-RCN-051`. An operator who
needs a static endpoint restates it after adoption, where the intent is unambiguous.

`REQ-RCN-069` is the inverse transition. Without it the only exit from `MANAGED` is
`REQ-RCN-032`, which removes the link from the kernel — an outage on the interface adoption
exists to preserve. A released interface needs no ownership rule of its own: it is absent from
desired state and no deletion record names it, so `REQ-RCN-030` and `REQ-RCN-031` report it as
`FOREIGN` without reference to which party created the link.

The adoption record of `REQ-RCN-070` is what makes both directions survive a restart, and it is
what `REQ-RCN-072` tests, so "adopted" is a fact the store holds rather than a history the agent
would have to remember. `REQ-RCN-065` and `REQ-RCN-071` bind the record to the same transactions
as the spec it accompanies, which is what stops a crash between the two writes from leaving a
managed interface no operator can release. `REQ-RCN-071` reaches `REQ-RCN-032` as well as
release, so no exit leaves the record behind.

No separate ownership transition is needed. `REQ-RES-017` defines `MANAGED` as presence in
desired state, so writing the spec is what changes `status.ownership`, and `REQ-RCN-069`
reverses it by the same mechanism.

## 7. Removed requirements

~~**REQ-RCN-068**~~ — Rejection of a peer the kernel holds that `PeerSpec` cannot represent.
Removed in v1.7: no such peer exists. Every field a kernel peer carries — public key, preshared
key, allowed IPs and keepalive — has a place in `PeerSpec`, so the requirement had no reachable
case. The conditions it was reaching for belong elsewhere and are covered: an IPv6 entry is
rejected by `REQ-VAL-020`, an empty `allowed_ips` list by `REQ-VAL-017`, and storing part of a
peer is already impossible under the single transaction of `REQ-RCN-065`.

## 8. Error handling

> **REQ-RCN-040** — When application fails, the agent MUST retain the stored desired state
> and mark the resource `DEGRADED` with reason `RECONCILE_FAILED`.

> **REQ-RCN-041** — The agent MUST retry using exponential backoff with jitter between
> `backoff_min` and `backoff_max`.

> **REQ-RCN-042** — The agent MUST serialize all operations on a single interface behind a
> per-interface lock.

## 9. Open questions

None. The scale targets that set the default `reconcile.interval` are fixed by `REQ-LIF-040`
in [SPEC-10](SPEC-10-lifecycle.md).
