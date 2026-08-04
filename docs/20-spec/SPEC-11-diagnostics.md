---
id: SPEC-11
title: Diagnostics
prefix: DIA
status: Accepted
version: 1.0
owner: Vinh Nguyen
created: 2026-08-03
updated: 2026-08-05
depends_on: [SPEC-02, SPEC-03]
adrs: [ADR-0008]
milestone: M1
---

# SPEC-11: Diagnostics

## 1. Scope

Two read-only RPCs that answer questions from observable data instead of requiring an operator
to log into the node: `GetOverview` for *is the agent working*, and `DiagnoseInterface` for
*why is traffic not passing on this interface*.

**Not in this module:**
- Continuous metrics and logs → [SPEC-08](SPEC-08-observability.md)
- Validation at write time → [SPEC-07](SPEC-07-validation.md)
- Liveness reporting → `REQ-API-051` in [SPEC-04](SPEC-04-api-conventions.md)

The distinction from SPEC-07: validation inspects the **spec** when it is written, while
diagnostics inspects **the node** when asked. The distinction from the health endpoint: health
answers a single yes or no for a probe, while `GetOverview` reports every component
separately so a human can see which one failed.

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

## 3. Node overview

`GetOverview` is the endpoint an operator opens first: one call that shows every moving part
of the agent and which of them is unhealthy. It is also the data source for a dashboard,
which is why it reports figures rather than prose.

> **REQ-DIA-020** — The agent MUST provide a `GetOverview` RPC reporting the state of every
> component listed below.

| Component | Reported |
|---|---|
| `agent` | Version, commit, start time, uptime |
| `listeners` | Unix socket path and mode; HTTP address; whether each is serving |
| `store` | Path, schema version, size on disk, writability |
| `kernel` | WireGuard availability, `CAP_NET_ADMIN`, netlink reachability |
| `nftables` | Availability, and whether `table inet wg_agent` is present |
| `sysctl` | Writability of `net.ipv4.conf`, per `REQ-FWD-025` |
| `reconcile` | Last run, duration, result, consecutive failure count |
| `interfaces` | Counts by `ownership`, and per interface the `oper_state`, peer total and peers online |
| `drift` | Fields found drifting on the most recent passes |

> **REQ-DIA-021** — Each component MUST carry a result of `PASS`, `WARN`, `FAIL` or
> `UNKNOWN`, matching the vocabulary of `REQ-DIA-002`.

> **REQ-DIA-022** — `GetOverview` MUST be read-only and accessible to the `reader` role.

> **REQ-DIA-023** — `GetOverview` MUST NOT trigger reconciliation or alter state in any way.

> **REQ-DIA-024** — `GetOverview` MUST NOT include a private key, a preshared key or a token
> value.

> **REQ-DIA-025** — `GetOverview` MUST answer from data the agent already holds, without
> waiting on a reconcile pass.

`REQ-DIA-025` keeps the endpoint usable exactly when it matters most. An agent whose store is
unwritable or whose netlink calls hang is the case an operator most needs to inspect, and an
overview that blocked on those subsystems would time out instead of naming them.

The `ORPHANED` count from `REQ-RES-017` surfaces here as well, since `REQ-RCN-035` leaves
those links for an operator to remove and this endpoint is where that backlog becomes visible.

## 4. Interface diagnostics

> **REQ-DIA-001** — The agent MUST provide a `DiagnoseInterface` RPC returning a list of
> check results.

> **REQ-DIA-002** — Each result MUST contain `name`, `result` — one of `PASS`, `WARN`, `FAIL`,
> `UNKNOWN` — plus `observed`, `expected`, `hint_code` and `hint`.

> **REQ-DIA-003** — `hint` MUST describe a concrete corrective action rather than restating
> the symptom.

> **REQ-DIA-030** — `hint_code` MUST be a stable identifier drawn from a closed set, and
> `hint` its English rendering.

> **REQ-DIA-031** — A check reporting a peer-specific finding MUST list the public keys
> concerned in `observed`.

`REQ-DIA-030` lets a platform localize or branch on the identifier while an operator reading
the raw response still gets a sentence. Changing the wording of `hint` is then not a contract
change, which is what keeps the text improvable.

`REQ-DIA-031` is what removes the need for a per-peer variant of this RPC. The two checks with
peer-level findings — `peers_no_handshake` and `allowed_ips_conflicts` — name the offending
keys, so an interface-scoped call already answers *which peer*. A second RPC would duplicate
every other check to add nothing.

> **REQ-DIA-004** — `DiagnoseInterface` MUST be read-only and accessible to the `reader` role.

> **REQ-DIA-005** — Diagnostics MUST NOT trigger reconciliation or alter state in any way.

### 4.1. Check list

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

None.
