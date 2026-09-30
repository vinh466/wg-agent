---
id: ADR-0017
title: Operator hooks through the CLI, carrying ADR-0013 forward
status: Accepted
owner: Vinh Nguyen
created: 2026-09-30
updated: 2026-09-30
supersedes: [ADR-0013]
superseded_by: null
affects: [SPEC-01, SPEC-04, SPEC-05, SPEC-07, SPEC-12, SPEC-13]
---

# ADR-0017: Operator hooks through the CLI, carrying ADR-0013 forward

## Context

[ADR-0013](ADR-0013-drive-wg-and-wg-quick.md) drives WireGuard through `wg` and `wg-quick`, and
forbids a rendered file to carry `PreUp`, `PostUp`, `PreDown` or `PostDown`, so that
[ADR-0007](ADR-0007-no-shell-hooks.md) holds. The consequence is that forwarding and NAT fall to
the host, set outside the agent.

The operator configures NAT by hand today through `PostUp` and `PostDown`, and asked for the same
through the agent's CLI alone. The facts that decide the question:

- A hook runs as root, every time `wg-quick` brings the interface up or down.
- The API token crosses the operator's private network in clear under
  [ADR-0015](ADR-0015-network-listener-with-a-shared-secret.md), so a hook field in the API would
  give root to whoever reads that network.
- The CLI runs as root on the node. Whoever runs it can already write any file and start any
  program there.

## Alternatives considered

### A — Hooks through neither, as ADR-0013
- For: nothing but validated values ever reaches a rendered file
- Against: NAT and forwarding set apart from the interface, unlike the operator's practice

### B — Structured NAT fields the agent turns into fixed hooks
- For: NAT through the API as well, with no free text anywhere
- Against: an nftables layer of the agent's own, and a slice of SPEC-02 brought forward

### C — Free-form hooks through the API
- Against, and decisive: remote root for anyone reading the private network

### D — Free-form hooks through the CLI alone
- For: the operator's practice, unchanged; no power the CLI's root user lacks
- Against: the agent renders commands it did not write, and cannot check what they do

## Decision

Adopt **D**. This ADR carries ADR-0013 forward and supersedes it as the current statement: every
decision of ADR-0013 stands, except that a rendered file may carry `PostUp` and `PostDown` lines
the operator set through the CLI.

- The commands are fields of the interface, set by `interface create` and `interface update`.
- The API neither returns them nor changes them, and an API write keeps the ones the CLI set.
  ADR-0007 — no shell hooks exposed through the API — therefore holds unchanged.
- A command containing a line break is refused, since it could add keys of its own to the file.
- `PreUp` and `PreDown` stay excluded; the operator's case needs only the two.
- The hooks run in the `wg-quick@` unit, as root, outside the agent's sandbox; the agent itself
  still starts no program but `wg` and `systemctl`.

## Consequences

### Positive
- An interface moved from a hand-kept file keeps its NAT and forwarding rules as they were
- Forwarding and NAT can live with the interface they serve, removed with it at `PostDown`

### Negative — the price paid
- A mistaken hook breaks the interface's `up`, and the agent reports that only as the failure of
  the unit
- A rendered file carries text no requirement validates beyond its shape

### Follow-on work
- `REQ-APL-003` widened by `PostUp` and `PostDown`; fields and requirements in SPEC-01, SPEC-04,
  SPEC-07 and SPEC-12

## Conditions for revisiting

- NAT or forwarding needed through the API — alternative B, or SPEC-02 itself, from backlog B-04.
- The CLI reachable by an account other than root.
- A hook that must run before the interface exists — `PreUp`.
