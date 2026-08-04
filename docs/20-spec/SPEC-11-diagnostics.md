---
id: SPEC-11
title: Diagnostics
prefix: DIA
status: Draft
version: 0.2
owner: Vinh Nguyen
created: 2026-08-03
updated: 2026-08-04
depends_on: [SPEC-02, SPEC-03]
adrs: [ADR-0008]
milestone: M1
---

# SPEC-11: Diagnostics

> **Status: Draft.** This module originates from the end-to-end review and is **not
> approved**.

## 1. Scope

The `DiagnoseInterface` RPC, which answers *why is traffic not passing* from observable data
instead of requiring an operator to log into the node.

**Not in this module:**
- Continuous metrics and logs → [SPEC-08](SPEC-08-observability.md)
- Validation at write time → [SPEC-07](SPEC-07-validation.md)

The distinction from SPEC-07: validation inspects the **spec** when it is written, while
diagnostics inspects **the node** when asked.

## 2. Purpose

After [ADR-0006](../10-decisions/ADR-0006-three-axis-forward-policy.md), seven independent
conditions must all hold for two peers to communicate:

1. The link exists and is UP
2. Forwarding sysctl is enabled on the interfaces involved
3. The agent's nftables rules do not block
4. Routes exist
5. Server-side `allowed_ips` is correct
6. Client-side `AllowedIPs` is wide enough
7. The host firewall does not block

Condition 7 lies **outside the agent's scope** per
[ADR-0008](../10-decisions/ADR-0008-no-host-firewall-ownership.md) and is the hardest symptom
in the system to diagnose. Without a diagnostic mechanism an operator must log in and read
`nft list ruleset` — precisely what the agent exists to avoid.

## 3. Requirements

> **REQ-DIA-001** — The agent MUST provide a `DiagnoseInterface` RPC returning a list of
> check results.

> **REQ-DIA-002** — Each result MUST contain `name`, `result` — one of `PASS`, `WARN`, `FAIL`,
> `UNKNOWN` — plus `observed`, `expected` and `hint`.

> **REQ-DIA-003** — `hint` MUST describe a concrete corrective action rather than restating
> the symptom.

> **REQ-DIA-004** — `DiagnoseInterface` MUST be read-only and accessible to the `reader` role.

> **REQ-DIA-005** — Diagnostics MUST NOT trigger reconciliation or alter state in any way.

## 4. Check list

| Name | Verifies | Data source |
|---|---|---|
| `link_exists` | The link exists and is of type wireguard | netlink |
| `oper_state` | UP or DOWN against `spec.enabled` | netlink |
| `addresses` | Addresses match the spec | netlink |
| `mtu` | MTU matches the spec | netlink |
| `port_bound` | The kernel holds the expected UDP port | wgctrl, `/proc/net/udp` |
| `sysctl_forwarding` | Forwarding matches `forward_policy` | `/proc/sys` |
| `uplink_forwarding` | Uplink forwarding when `external = ALLOW` | `/proc/sys` |
| `nft_table` | `table inet wg_agent` exists | nftables |
| `nft_rules` | Rules match the current `forward_policy` | nftables |
| `nat_masquerade` | A masquerade rule exists when `nat.enabled` | nftables |
| `routes` | Routes exist for the union of `allowed_ips` | netlink |
| `peers_no_handshake` | Peers that have never completed a handshake | wgctrl |
| `allowed_ips_conflicts` | Actual duplicates or overlaps | wgctrl |
| **`foreign_forward_chains`** | **Other tables hooked into FORWARD** | nftables |

> **REQ-DIA-010** — The agent MUST implement the `foreign_forward_chains` check, listing every
> base chain hooked into FORWARD that does not belong to `table inet wg_agent`.

This check is mandatory rather than optional. It is the only answer to the price accepted in
[ADR-0008](../10-decisions/ADR-0008-no-host-firewall-ownership.md): `ALLOW` does not
guarantee traffic passes. Without the check that price becomes undiagnosable.

> **REQ-DIA-011** — When a foreign FORWARD chain is present and any axis is set to `ALLOW`,
> the agent MUST return `WARN` with a `hint` stating that agent policy permits the traffic
> while another host firewall may be blocking it.

## 5. Implementation cost

The agent already reads every input these checks need in order to reconcile. This module
mainly re-presents that data in a form useful to a human, making the cost low relative to the
operational value.

That is the basis for scheduling it in M1 rather than M4: it is needed once the topology
patterns in [SPEC-02](SPEC-02-forward-policy.md) are first tested.

## 6. Open questions

- Whether per-peer diagnostics are worth adding alongside per-interface. See OQ-09.
- Whether `hint` should be a free-form string or an identifier the platform localizes. See
  OQ-10.
