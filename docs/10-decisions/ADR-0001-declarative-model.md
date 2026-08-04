---
id: ADR-0001
title: Declarative model with a reconcile loop
status: Accepted
owner: Vinh Nguyen
created: 2026-08-03
updated: 2026-08-04
supersedes: []
superseded_by: null
affects: [SPEC-01, SPEC-03, SPEC-04]
---

# ADR-0001: Declarative model with a reconcile loop

## Context

WireGuard state in the kernel is ephemeral. A reboot destroys every interface, peer and
key.

The intended consumers are machines: Terraform providers, Kubernetes operators, VPN
platforms. Those consumers need drift detection, full state reads for comparison, and
idempotent operations.

The original concept described the agent as a thin mapping from API calls to syscalls,
with no persistence layer.

## Alternatives considered

### A — Imperative RPC shim
Each API call performs one kernel operation. No state is stored.

- For: minimal code, no synchronization to reason about
- Against: a reboot loses everything; the layer above must implement recovery; drift is
  undetectable; the result does not meet the definition of a control plane

### B — Declarative with reconciliation
Callers declare desired state. The agent persists it locally and runs a loop that brings
the kernel into agreement.

- For: survives reboots; detects and repairs drift; matches the Terraform and operator
  model; self-heals after manual intervention
- Against: requires a local store, concurrency handling, and versioning for optimistic
  concurrency

### C — Imperative in v1, declarative in v2
- For: a working build sooner
- Against: the API surface almost certainly breaks during the transition; the store
  touches every write path, so it is not something that can be added later

## Decision

Adopt **alternative B**.

Alternative A fails the definition of a control plane set out in
[product positioning](../00-overview/product.md). Alternative C claims to save effort but
requires rewriting the write path, paying the cost twice.

Writes remain **synchronous** — applied to the kernel before returning — with the
reconcile loop acting as a safety net rather than the primary execution path
(`REQ-API-020`). Terraform providers need the outcome within the call.

## Consequences

### Positive
- Reboots, crashes and manual interface deletion all self-repair
- Drift becomes measurable and alertable through `wg_agent_reconcile_drift_total`
- `terraform plan` reflects reality because full state is readable

### Negative — the price paid
- A storage layer appears, requiring backup, schema migration and corruption handling
- Interface private keys must be persisted to disk to survive a reboot
- Fields must be classified as agent-owned or kernel-owned; without that classification
  reconciliation breaks runtime behavior such as roaming (`REQ-RCN-013`)

### Follow-on work
- SPEC-03: store design, reconcile algorithm, field ownership classification
- SPEC-10: backup, restore, schema migration during upgrade

## Conditions for revisiting

None. This decision defines the product — without it wg-agent is an API wrapper rather
than a control plane.
