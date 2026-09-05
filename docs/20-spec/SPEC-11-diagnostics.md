---
id: SPEC-11
title: Diagnostics
prefix: DIA
status: Accepted
version: 1.6
owner: Vinh Nguyen
created: 2026-08-03
updated: 2026-09-06
depends_on: [SPEC-02, SPEC-03]
adrs: [ADR-0008, ADR-0011]
milestone: M1
---

# SPEC-11: Diagnostics

## 1. Scope

Reports that answer questions from observable data instead of requiring an operator to log into
the node: `GetOverview` for *is the agent working*, `DiagnoseInterface` for *why is traffic not
passing on this interface*, and the adoption readiness report of section 5 for *what would
happen if this existing interface were adopted*.

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

> **REQ-DIA-052** — Each component MUST be reported as a `name`, a `result`, a `message` and a
> list of name-value details.

The `Reported` column above says what belongs in a component's details, not what its message
shape is. `REQ-DIA-052` fixes one envelope for all nine rather than thirty typed fields across
nine messages, which is what lets a component gain a detail without that being a change to the
contract under `REQ-API-061`. Typed payloads are a candidate for a later version, not a thing
this one has to guess at.

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
> `UNKNOWN` — plus `observed` and `expected` as lists of strings, and `hint_code` and `hint` as
> strings.

`observed` is a list because `REQ-DIA-031` requires a peer-scoped check to name every public key
concerned, and a single string would need a separator no requirement defines. A check with one
observation carries a list of one.

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

## 5. Adoption readiness

A report describing what adopting a `FOREIGN` interface would produce, and what stands in the
way, under [ADR-0011](../10-decisions/ADR-0011-operator-initiated-adoption.md). It runs before
anything is written, which makes it the preview the reconcile path does not otherwise offer.

> **REQ-DIA-040** — The agent MUST provide an adoption readiness report naming every `FOREIGN`
> interface, the spec fields adoption would read from the kernel, and its findings.

> **REQ-DIA-041** — Each finding MUST carry the fields required by `REQ-DIA-002`.

> **REQ-DIA-042** — The report MUST record a `FAIL` result for a condition under which
> adoption would produce an interface the agent cannot manage.

> **REQ-DIA-043** — The report MUST record a `FAIL` result when a `wg-quick` unit for the
> interface is enabled.

> **REQ-DIA-044** — The report MUST record a `WARN` result for a `wg-quick` directive with no
> equivalent in the resource model, naming the directive.

> **REQ-DIA-045** — The report MUST NOT take a spec field value from a `wg-quick`
> configuration file.

> **REQ-DIA-046** — The report MUST record a `WARN` result, rather than a `FAIL` result, for a
> `wg-quick` configuration file it cannot parse.

> **REQ-DIA-047** — The report MUST NOT include a private key or a preshared key.

> **REQ-DIA-048** — The report MUST record a `WARN` result for a peer whose endpoint the kernel
> holds, naming the peer.

> **REQ-DIA-049** — The report MUST determine `wg-quick` unit enablement from the presence of a
> symlink to `wg-quick@<interface>.service` under a systemd target's `.wants` directory.

> **REQ-DIA-050** — The report MUST record an `UNKNOWN` result when it cannot determine unit
> enablement.

> **REQ-DIA-051** — The `hint_code` of a readiness finding MUST be one of the values below.

| `hint_code` | Raised for |
|---|---|
| `WG_QUICK_UNIT_ENABLED` | An enabled `wg-quick` unit — `REQ-DIA-043` |
| `WG_QUICK_UNIT_UNDETERMINED` | Enablement could not be determined — `REQ-DIA-050` |
| `WG_QUICK_HOOK_DIRECTIVE` | `PreUp`, `PostUp`, `PreDown` or `PostDown` — `REQ-DIA-044` |
| `WG_QUICK_UNOWNED_DIRECTIVE` | Another directive outside the resource model — `REQ-DIA-044` |
| `WG_QUICK_CONFIG_UNPARSEABLE` | The configuration file could not be read — `REQ-DIA-046` |
| `PEER_ENDPOINT_DISCARDED` | A peer endpoint `REQ-RCN-063` does not store — `REQ-DIA-048` |
| `INTERFACE_NO_ADDRESS` | A link with no address — `REQ-VAL-016` |
| `INTERFACE_IPV6_ADDRESS` | An address of the IPv6 family — `REQ-VAL-020` |
| `PEER_IPV6_ALLOWED_IPS` | An IPv6 entry in a peer's allowed IPs — `REQ-VAL-020` |
| `PEER_NO_ALLOWED_IPS` | A peer with no allowed IPs — `REQ-VAL-017` |
| `LISTEN_PORT_COLLISION` | A port another host interface holds — `REQ-VAL-013` |
| `ADDRESS_COLLISION` | A subnet another host interface carries — `REQ-VAL-014` |
| `LINK_NOT_WIREGUARD` | A link the resource model does not describe — `REQ-DIA-042` |
| `ADOPTION_READY` | Nothing blocks or warns |

`REQ-DIA-030` calls the set closed so that `hint` can be reworded without that being a contract
change. The set above covers section 5; the codes for the checks of section 4.1 arrive with
those checks, which are deferred under `B-06` in the [backlog](../60-planning/backlog.md).
Adding a value to the enum later is additive and does not break a generated client, so the set
grows without a package version bump.

`REQ-DIA-049` fixes a filesystem predicate because `REQ-SEC-041` forbids the agent from
executing a child process, which rules out asking `systemctl`. Enablement is a symlink, so the
question is answerable by reading directories. `REQ-DIA-050` keeps an unreadable directory from
presenting as a pass: an undetermined answer is reported as undetermined, and `REQ-RCN-064`
blocks only on `FAIL`, so adoption is not refused for a question the report could not ask.

`REQ-DIA-048` is what gives an operator the one notice that `REQ-RCN-063` discards a peer
endpoint. Without it an implementation that stays silent would be conformant.

Reusing `REQ-DIA-002` gives every finding the same shape as a diagnostic check result,
including the `hint_code` and `hint` that `REQ-DIA-003` and `REQ-DIA-030` already govern, so
remediation text has one home and one closed identifier set. `REQ-DIA-005` forbids diagnostics
from altering state, and this report is diagnostics, so it inherits that guarantee.

`REQ-DIA-047` mirrors `REQ-DIA-024`. Adoption reads the interface private key and every
preshared key from the kernel under `REQ-RCN-061` and `REQ-RCN-062`, so a report naming the
fields it would store would otherwise disclose exactly what `REQ-RES-013`, `REQ-RES-022`,
`REQ-KEY-002` and `REQ-SEC-050` forbid in any output.

`REQ-DIA-043` names the enabled unit and nothing else. A `wg-quick` configuration file survives
`systemctl disable` and describes an interface nobody is starting, so treating the file's
presence as contention would refuse adoption on precisely the nodes it exists to serve.

### 5.1. Findings

| Finding | Result | Reason |
|---|---|---|
| An enabled `wg-quick` unit for this interface | `FAIL` | Both would manage the link, and the winner after a reboot is a race |
| The spec adoption would produce fails validation | `FAIL` | `REQ-VAL-001` rejects it with its own reason code, so `REQ-RCN-064` defers to it |
| A peer carries no `allowed_ips` | `FAIL` | `REQ-VAL-017` rejects the resulting spec |
| The link carries no address | `FAIL` | `REQ-VAL-016` rejects the resulting spec |
| `PostUp` or `PostDown` present | `WARN` | [ADR-0007](../10-decisions/ADR-0007-no-shell-hooks.md) forbids reproducing them, so disabling `wg-quick` loses their effect at the next boot |
| `SaveConfig` enabled | `WARN` | The file stops being updated once the agent manages the interface |
| `DNS` present | `WARN` | A client-side concern the agent does not own |
| `Table` present | `WARN` | It maps to `manage_routes`, which `REQ-RCN-066` takes from the request rather than from the file |
| A peer holds an endpoint | `WARN` | `REQ-DIA-048` — `REQ-RCN-063` discards it |
| The configuration file cannot be parsed | `WARN` | `REQ-DIA-046` — field values come from the kernel regardless |
| Unit enablement could not be determined | `UNKNOWN` | `REQ-DIA-050` |

The validation row references [SPEC-07](SPEC-07-validation.md) rather than restating its rules,
which is what keeps the port, address and IPv6 predicates in the one module that owns them.

The `PostUp` warning is the one that costs an operator a working node. Rules installed by a hook
survive adoption because nothing removes them, and disappear at the next boot because nothing
recreates them. The gap between those two moments is where the report earns its place.

### 5.2. Surface

The report has no RPC of its own. `REQ-CLI-004` requires `doctor` to run on a node where the
agent has never started, so the command computes the report locally. An API caller reaches the
same findings two ways: `REQ-API-068` returns the spec an adoption would store without writing
it, and `REQ-API-067` carries the findings inside the `ADOPTION_BLOCKED` failure of
`REQ-RCN-064`.

## 6. Implementation cost

The agent already reads every input these checks need in order to reconcile. This module
mainly re-presents that data in a form useful to a human, making the cost low relative to the
operational value.

That is the basis for scheduling it in M1 rather than M4: it is needed once the topology
patterns in [SPEC-02](SPEC-02-forward-policy.md) are first tested.

## 7. Open questions

None.
