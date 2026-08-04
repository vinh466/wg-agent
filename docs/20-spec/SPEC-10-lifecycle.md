---
id: SPEC-10
title: Lifecycle — upgrade, backup, restore
prefix: LIF
status: Draft
version: 0.2
owner: Vinh Nguyen
created: 2026-08-03
updated: 2026-08-05
depends_on: [SPEC-03, SPEC-06, SPEC-09]
adrs: [ADR-0001]
milestone: M3
---

# SPEC-10: Lifecycle — upgrade, backup, restore

> **Status: Draft.** This module originates from the end-to-end review and is **not
> approved**. Implementation waits until the status reaches `Accepted`.

## 1. Scope

Everything that happens to the system over time outside ordinary API operations: agent
restart, version upgrade, downgrade, backup, restore onto a different node, and audit log
size management.

**Not in this module:**
- The reconcile algorithm → [SPEC-03](SPEC-03-state-reconcile.md)
- Audit record contents → [SPEC-08](SPEC-08-observability.md)

## 2. Foundational property: an independent data plane

> **REQ-LIF-001** — Documentation MUST state that the data plane operates independently of
> the agent.

When the agent crashes, is killed, or is being upgraded, the kernel continues encrypting and
forwarding traffic. The agent is only a control plane, so an agent outage is not a VPN
outage.

This follows directly from
[ADR-0002](../10-decisions/ADR-0002-netlink-over-wg-quick.md) and is operationally
significant, not merely a marketing claim.

## 3. Upgrade and downgrade

> **REQ-LIF-010** — An agent upgrade MUST NOT interrupt established tunnels.

> **REQ-LIF-011** — On encountering a store schema newer than it understands, the agent MUST
> refuse to start with a message naming both the store version and the agent version.

This condition arises when a `.deb` package is rolled back after a newer version migrated the
schema. See also `REQ-RCN-005`.

> **REQ-LIF-012** — Schema migration MUST run inside a single transaction.

> **REQ-LIF-050** — Schema migration MUST create a store backup before starting.

### Undecided
- Whether downward migration is supported, or whether recovery relies solely on the
  pre-migration backup. See [open questions](../60-planning/open-questions.md), OQ-08.

## 4. Backup and restore

The store holds **interface private keys**, which enables an important capability:

> Restoring the store onto a new node preserves the server public keys, so **every client
> reconnects with no reconfiguration** once DNS or addressing points at the new node.

This is a strong disaster-recovery property and is therefore specified deliberately rather
than left as an accidental side effect.

> **REQ-LIF-020** — The agent MUST provide a CLI command that exports the complete desired
> state.

> **REQ-LIF-051** — The agent MUST provide a CLI command that imports an exported
> desired state.

> **REQ-LIF-021** — Export MUST NOT be exposed through the network API.

An export contains private keys; exposing it over the API would reintroduce the leak path
that [SPEC-06](SPEC-06-key-management.md) carefully removes.

> **REQ-LIF-022** — An export file MUST be created with mode `0600`.

> **REQ-LIF-052** — Documentation MUST state that an export file is a secret of the highest
> sensitivity.

> **REQ-LIF-023** — Import MUST refuse to proceed when the current store is non-empty, unless
> an explicit override flag is supplied.

### Undecided
- Whether exports support passphrase encryption. See OQ-06.
- Whether partial import of a single interface is required. See OQ-07.

## 5. Audit log rotation

> **REQ-LIF-030** — The installation package MUST include a logrotate configuration for the
> audit log.

> **REQ-LIF-031** — The agent MUST enforce an internal size limit as a last-resort guard when
> logrotate is absent.

At thousands of peers with continuous reconciliation, unbounded audit log growth is a
disk-exhaustion incident waiting to occur.

## 6. Scale targets

> **REQ-LIF-040** — The agent MUST meet the scale targets below.

> **REQ-LIF-053** — The test suite MUST include benchmarks verifying those targets.

| Dimension | Target |
|---|---|
| Interfaces per node | 10 |
| Peers per interface | 250 |
| Peers per node | 1,000 |
| Full reconcile at maximum scale | under 1 s |

The figures carry roughly twice the headroom over the intended deployment of five interfaces
holding about a hundred peers each. Sizing to the deployment rather than to an aspirational
number keeps the benchmark suite cheap enough to run on every change.

At 1,000 peers a node emits per-peer metrics well inside comfortable Prometheus cardinality,
so `metrics.per_peer` stays enabled by default and `REQ-OBS-003` covers deployments that
outgrow this target.
