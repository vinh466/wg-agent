---
id: ADR-0008
title: The agent does not own the host firewall
status: Accepted
owner: Vinh Nguyen
created: 2026-08-03
updated: 2026-08-04
supersedes: []
superseded_by: null
affects: [SPEC-02, SPEC-11]
---

# ADR-0008: The agent does not own the host firewall

## Context

The agent needs netfilter rules to implement
[ADR-0006](ADR-0006-three-axis-forward-policy.md) and NAT. The question is how much of the
host firewall it manages.

On a real node something else almost always manages netfilter already — `ufw`,
`firewalld`, Docker, or the operations team's own scripts.

## Alternatives considered

### A — The agent owns the whole firewall
It manages the global FORWARD policy and removes conflicting rules.

- For: `ALLOW` would genuinely mean traffic passes
- Against: direct conflict with `ufw`, `firewalld` and Docker; the agent destroys other
  parties' network configuration; unacceptable

### B — The agent owns one dedicated table and adds only DENY rules
It creates and manages `table inet wg_agent` and touches nothing else.

- For: coexists safely with everything; destroys no existing configuration
- Against: the agent cannot guarantee `ALLOW`

## Decision

Adopt **alternative B**.

The resulting semantics are asymmetric, arising from how nftables evaluates base chains on
the same hook: a `drop` verdict in any chain terminates evaluation and discards the packet,
while `accept` merely ends that chain and **continues** to the next.

| Value | Agent action | Guarantee |
|---|---|---|
| `DENY` | Adds a `drop` rule | Blocking is certain |
| `ALLOW` | Adds no rule | Only that *the agent does not block* |

`ALLOW` therefore **MUST** be read as "the agent does not block", never as "traffic is
guaranteed to pass".

This is the direct and unavoidable price of not seizing the host firewall. It is accepted
deliberately, because the alternative is destroying someone else's configuration on their
own machine.

## Consequences

### Positive
- Installing the agent on a node running `ufw` or Docker causes no incident
- Ownership is unambiguous, which simplifies debugging
- Removing the agent means deleting one table

### Negative — the price paid
- `ALLOW` does not guarantee connectivity; an operator can open policy and still see no
  traffic
- This is the **hardest symptom to diagnose** in the whole system, because the cause lies
  outside the agent's scope

### Follow-on work
- SPEC-11 must implement the `foreign_forward_chains` check, listing other tables hooked
  into FORWARD so the culprit is named when `ufw` is blocking. Without that check the price
  above becomes undiagnosable
- User documentation states the `ALLOW` semantics prominently

## Conditions for revisiting

None. Seizing the host firewall is unacceptable behavior for an agent regardless of the
convenience gained.
