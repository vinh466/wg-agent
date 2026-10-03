---
id: SPEC-02
title: Forward policy and NAT
prefix: FWD
status: Accepted
version: 1.4
owner: Vinh Nguyen
created: 2026-08-03
updated: 2026-09-05
depends_on: [SPEC-01]
adrs: [ADR-0006, ADR-0008, ADR-0011]
milestone: Backlog B-04
---

# SPEC-02: Forward policy and NAT

## 1. Scope

Specifies `ForwardPolicySpec`, `NatSpec`, and the nftables rules and sysctl values the
agent manages.

**Not in this module:**
- Conceptual explanation and worked examples → [connectivity model](../40-concepts/connectivity-model.md)
- Choosing a topology → [topology patterns](../50-guides/topology-patterns.md)
- Related validation rules → [SPEC-07](SPEC-07-validation.md)
- Which requirements land in which milestone → [roadmap](../60-planning/roadmap.md)

This module is delivered in two parts. The `ALLOW`/`DENY` axes and the forwarding sysctl are
required by the default `ForwardPolicySpec`, so they arrive with the first release; NAT,
`ALLOW_LIST` and uplink forwarding follow later. The roadmap holds the split, because a
schedule is not a normative statement.

## 2. ForwardPolicySpec

| Field | Type | Default |
|---|---|---|
| `intra_interface` | `ALLOW` \| `DENY` | `ALLOW` |
| `inter_interface` | `ALLOW` \| `DENY` \| `ALLOW_LIST` | `DENY` |
| `allowed_peer_interfaces` | []string | `[]` |
| `external` | `ALLOW` \| `DENY` | `DENY` |

> **REQ-FWD-001** — The default `ForwardPolicySpec` MUST be `intra_interface: ALLOW`,
> `inter_interface: DENY`, `external: DENY`.

> **REQ-FWD-002** — `allowed_peer_interfaces` MUST take effect only when
> `inter_interface = ALLOW_LIST`.

The default corresponds to the flat-LAN-per-group pattern described in
[topology patterns](../50-guides/topology-patterns.md).

## 3. ALLOW and DENY semantics

The agent owns only `table inet wg_agent` and does not touch the host firewall
([ADR-0008](../10-decisions/ADR-0008-no-host-firewall-ownership.md)). In nftables a `drop`
verdict in any base chain terminates evaluation and discards the packet, while `accept`
only ends that chain and continues to the next one on the same hook.

> **REQ-FWD-003** — When an axis is set to `DENY`, the agent MUST add the corresponding
> `drop` rule.

> **REQ-FWD-004** — When an axis is set to `ALLOW`, the agent MUST NOT add any `accept`
> rule for that axis.

> **REQ-FWD-005** — User documentation MUST state that `ALLOW` means the agent does not
> block rather than that traffic is guaranteed to pass.

`REQ-FWD-004` follows from `REQ-FWD-005`: adding an `accept` rule would suggest the agent
guarantees connectivity, which it cannot.

## 4. nftables rules

> **REQ-FWD-010** — The agent MUST create and manage only `table inet wg_agent`.

> **REQ-FWD-040** — The agent MUST NOT read, modify or delete any other nftables table.

> **REQ-FWD-011** — The agent's forward chain MUST begin with
> `ct state established,related accept`.

> **REQ-FWD-012** — When `intra_interface = DENY`, the agent MUST drop packets whose input
> and output interface are both that interface.

> **REQ-FWD-013** — When `inter_interface = DENY`, the agent MUST drop packets leaving that
> interface towards any other WireGuard interface it manages.

> **REQ-FWD-014** — When `inter_interface = ALLOW_LIST`, the agent MUST drop packets towards
> every managed WireGuard interface absent from `allowed_peer_interfaces`.

> **REQ-FWD-015** — When `external = DENY`, the agent MUST drop packets leaving that
> interface towards a non-WireGuard interface.

> **REQ-FWD-016** — On interface removal, the agent MUST delete every rule belonging to that
> interface.

> **REQ-FWD-041** — When no managed interface remains, the agent MUST delete
> `table inet wg_agent`.

### 4.1. Asymmetry

Because of `REQ-FWD-011`, policy applies only to **new** connections.

> **REQ-FWD-017** — The agent MUST NOT synchronize policy between two interfaces
> automatically.

When `wg0` permits `wg1` but `wg1` does not permit `wg0`, `wg0` can initiate connections
and replies return normally, while `wg1` cannot initiate. This is intended behavior;
validation warns about it (`REQ-VAL-023`) because it usually indicates a forgotten
declaration.

## 5. Forwarding sysctl

The kernel decides forwarding from the `forwarding` sysctl of the **input** interface rather
than from a global variable.

| Flow | Outbound needs | Return needs | Touches uplink? |
|---|---|---|---|
| Peer to peer on `wg0` | `conf.wg0.forwarding=1` | `conf.wg0.forwarding=1` | No |
| `wg0` to `wg1` | `conf.wg0.forwarding=1` | `conf.wg1.forwarding=1` | No |
| Peer to egress | `conf.wg0.forwarding=1` | `conf.<uplink>.forwarding=1` | **Yes** |

> **REQ-FWD-020** — When `intra_interface = ALLOW` or `inter_interface != DENY`, the agent
> MUST set `net.ipv4.conf.<iface>.forwarding = 1` on the corresponding WireGuard interface.

> **REQ-FWD-021** — The agent MUST NOT write the global `net.ipv4.ip_forward`.

> **REQ-FWD-022** — The agent MUST NOT change the forwarding sysctl of an interface that
> desired state does not describe, except as required by `REQ-FWD-023` or `REQ-FWD-024`.

> **REQ-FWD-023** — When `external = ALLOW`, the agent MUST reject the spec with
> `FORWARD_POLICY_NEEDS_UPLINK` unless `nat.enable_uplink_forwarding` is true.

> **REQ-FWD-024** — The agent MUST record the forwarding sysctl of each WireGuard interface it
> adopts and restore that value when the interface is released.

`REQ-FWD-022` tests desired-state membership rather than creation, because an interface adopted
under `REQ-RCN-060` is managed without having been created by the agent. `REQ-RCN-030` is the
single definition of that boundary; restating the creation test here would leave two answers to
one question. The uplink is never in desired state, so it stays outside.

`REQ-FWD-024` covers adoption alone, and release alone, because those are the only cases where
a restore can be performed or observed. The per-interface sysctl node exists only while the
link does, so for an interface the agent created and then deleted there is nothing left to
restore. Release is the one exit that leaves the link running, and it is also a moment at which
`REQ-FWD-022` would otherwise forbid the write — hence the exception it now carries. The value
is held in the adoption record of `REQ-RCN-070`.

> **REQ-FWD-042** — The agent MUST NOT restore the forwarding sysctl on the uplink.

Uplink values are left alone because other components on the host may depend on them.

> **REQ-FWD-025** — The agent MUST verify sysctl writability during startup rather than
> during reconcile, failing with `SYSCTL_WRITE_DENIED`.

## 6. NatSpec

| Field | Type | Default |
|---|---|---|
| `enabled` | bool | `false` |
| `masquerade_out_interface` | string | derived from the default route |
| `enable_uplink_forwarding` | bool | `false` |

> **REQ-FWD-030** — When `nat.enabled` is true, the agent MUST add a masquerade rule for
> traffic leaving that interface via `masquerade_out_interface`.

> **REQ-FWD-031** — When `masquerade_out_interface` is empty, the agent MUST derive it from
> the default route and record the derived value in `status`.

> **REQ-FWD-032** — Enabling `enable_uplink_forwarding` MUST emit a `WARN` log entry and an
> audit record.

## 7. Open questions

- Whether the agent should re-derive `masquerade_out_interface` when the default route
  changes. See [open questions](../60-planning/open-questions.md), OQ-11.
