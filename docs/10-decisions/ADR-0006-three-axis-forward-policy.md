---
id: ADR-0006
title: Three-axis forward policy defaulting to a flat LAN per group
status: Accepted
owner: Vinh Nguyen
created: 2026-08-03
updated: 2026-08-04
supersedes: []
superseded_by: null
affects: [SPEC-02, SPEC-07]
---

# ADR-0006: Three-axis forward policy

## Context

The original design had a single `enable_ip_forward` switch. That switch is all-or-nothing:
turning it on to give peers internet access simultaneously opens peer-to-peer and
interface-to-interface traffic, which the operator never asked for.

One technical point is decisive: **narrowing server-side `allowed_ips` does not isolate
peers.** Peer A with `allowed_ips = 10.10.0.5/32` can still send a packet with
`src=10.10.0.5 dst=10.10.0.6` — the server accepts it because the source matches, then
routes it to B. Real isolation requires a rule in the FORWARD chain.

## Alternatives considered

### A — Keep a single `enable_ip_forward` switch
- For: simple
- Against: cannot express intent; opens more than required; cannot isolate peers

### B — Allow arbitrary firewall rules to be declared
- For: maximum flexibility
- Against: turns the agent into a general firewall management tool; conflicts with the
  boundary in [ADR-0008](ADR-0008-no-host-firewall-ownership.md); easy to misconfigure

### C — Three named axes
`intra_interface`, `inter_interface`, `external` — one per real traffic direction.

- For: expresses intent directly; maps one-to-one onto nftables rules; testable
- Against: three fields instead of one

## Decision

Adopt **alternative C**, defaulting to a **flat LAN per group**:

| Axis | Default |
|---|---|
| `intra_interface` | `ALLOW` |
| `inter_interface` | `DENY` |
| `external` | `DENY` |

That default matches the natural expectation when an operator creates one interface and
adds several peers: members are expected to see each other. The other two axes stay
`DENY`, so nothing leaks beyond the interface.

This is **not** deny-all. The trade-off is accepted deliberately: absolute isolation
requires declaring `intra_interface: DENY` explicitly.

### Related consequence: minimal sysctl scope

The kernel decides forwarding from the `forwarding` sysctl of the **input** interface, not
from a global variable. Therefore:

- `intra_interface` and `inter_interface` need forwarding enabled only on the **WireGuard
  interfaces the agent created** — resources the agent fully owns
- Only `external` requires forwarding on the uplink, which lies outside the agent's
  ownership and therefore demands an explicit opt-in

This substantially reduces the agent's footprint on the host compared with the original
design.

## Consequences

### Positive
- Peer isolation becomes a boolean field rather than a shell script
- Peer-to-peer and interface-to-interface traffic never touch global host configuration
- Four common operational patterns are expressible with three fields

### Negative — the price paid
- `ALLOW` only means *the agent does not block*, never *traffic is guaranteed to pass* —
  a direct consequence of [ADR-0008](ADR-0008-no-host-firewall-ownership.md)
- Policy is stateful and directional, so a one-sided declaration produces asymmetric
  behavior — intended, but surprising
- Opening `inter_interface` on the server is insufficient: client-side `AllowedIPs` must
  also widen, and the agent cannot enforce that

### Follow-on work
- `ClientRouting.AUTO` derives client-side `AllowedIPs` from policy so operators neither
  compute it nor compute it wrongly
- Validation warns on one-sided declarations and on `external = ALLOW` without NAT

## Conditions for revisiting

A requirement for ACLs finer than interface granularity — peer A may reach B but not C —
is **business ACL** and belongs to the platform layer, implemented by narrowing
`allowed_ips`. These three axes must not grow into a firewall language.
