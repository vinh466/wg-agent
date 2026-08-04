---
id: ADR-0007
title: No shell hooks exposed through the API
status: Accepted
owner: Vinh Nguyen
created: 2026-08-03
updated: 2026-08-04
supersedes: []
superseded_by: null
affects: [SPEC-05]
---

# ADR-0007: No shell hooks exposed through the API

## Context

`wg-quick` supports `PreUp`, `PostUp`, `PreDown` and `PostDown` — arbitrary shell around
the interface lifecycle. WireGuard operators are familiar with them and will ask why the
agent has none.

Locating the risk precisely matters, because the answer is not "hooks are bad":

- Over a **unix socket**, the caller is already a local process with root-equivalent
  privilege. A hook grants no escalation
- Over **TCP with mTLS** — the primary deployment model — a client identity is **not**
  root on the node. A hook then converts a leaked credential, or any service holding the
  `admin` role, into **arbitrary code execution on every node**

The problem is therefore not hooks themselves but **who defines their contents**.

## Alternatives considered

### A — Free-form hooks through the API
- For: maximum flexibility for unforeseen needs
- Against: the API becomes a remote code execution endpoint, and no level of
  authentication makes that safe

### B — Named hooks defined by the operator
The operator, already root, declares permitted commands in configuration; callers
reference them by name and cannot pass arbitrary strings. The `sudoers` model.

- For: preserves the privilege boundary — callers cannot introduce new code
- Against: still requires `exec.Command`, breaking the invariant from
  [ADR-0002](ADR-0002-netlink-over-wg-quick.md); adds test surface; becomes an excuse to
  avoid designing proper APIs

### C — No hooks, with typed APIs covering the real needs
- For: no code execution surface; everything testable; the no-exec invariant holds
- Against: no escape hatch for unforeseen needs

## Decision

Adopt **alternative C**.

A survey of real hook usage shows typed APIs already cover most of it:

| Hook used for | Replaced by |
|---|---|
| MASQUERADE | `NatSpec.masquerade_out_interface` |
| FORWARD accept | `ForwardPolicySpec` |
| `sysctl ip_forward` | Per-interface sysctl (`REQ-FWD-020`) |
| Peer isolation | `ForwardPolicySpec.intra_interface: DENY` |
| Client-side DNS | Out of scope — a client concern |
| Kill switch | Out of scope — a client concern |
| Up/down webhooks | Synchronous API responses plus `WatchPeerStatus` |

What remains is `tc`/qdisc rate limiting, explicitly deferred, plus genuinely unforeseen
needs.

The decisive argument for the unforeseen case: **wg-agent need not be the only thing
touching the node.** Its consumers are platforms, not end users. A platform requiring
bespoke rules runs its own configuration management alongside. The agent manages
WireGuard and need not become a general systems administration tool.

## Consequences

### Positive
- No code execution surface on the API, even with a leaked credential
- Every network behavior is typed and testable
- The no-`exec.Command` invariant survives

### Negative — the price paid
- Unforeseen needs wait for a typed API, or are handled by tooling outside the agent
- Operators familiar with wg-quick will notice the gap and need an explanation

### Follow-on work
- SPEC-05 states the prohibition **with its reason**, never as dogma
- Each new request is first evaluated for coverage by a typed API

## Conditions for revisiting

If genuine needs accumulate that typed APIs cannot cover, consider **alternative B**,
never alternative A. The boundary to preserve: a caller over the API **MUST NOT** be able
to introduce new code onto the node.
