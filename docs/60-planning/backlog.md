---
updated: 2026-09-30
---

# Backlog

Requirements outside phases P1 to P3 of the [roadmap](roadmap.md). Nothing here is withdrawn:
every ID below stays live and normative in its module. Deferral is a statement about build
order, not about correctness. Phase P4 is chosen from this file once P1 to P3 run on a real node.

## Why this file exists

[ADR-0013](../10-decisions/ADR-0013-drive-wg-and-wg-quick.md) narrowed the near-term product to a
wrapper over `wg` and `wg-quick` that replaces an operator's manual work. Against that target this
file defers **168 of the 330 live requirements**, leaving 162 in phases P1 to P3. Counting them
is what keeps the plan honest: a phase that looks small only because nobody counted is the
failure this file exists to prevent. Counts are as of the front-matter date.

Several entries were written for a control plane that drove the kernel directly. Each says what
has to be re-read against ADR-0013 when it returns, so a returning entry is re-specified rather
than implemented as it stands.

## How to read an entry

| Field | Meaning |
|---|---|
| **Defers** | The requirement IDs moved out of phases P1 to P3 |
| **Returns when** | The condition that puts the entry back on the critical path |
| **On return** | What must be re-read or re-decided first |

## Summary

| Entry | Area | Deferred |
|---|---|---|
| B-01 | Metrics and audit | 10 |
| B-02 | Backup, restore, upgrade and migration | 15 |
| B-03 | Install script and release pipeline | 8 |
| B-04 | Forward policy and NAT | 33 |
| B-05 | API scaffolding for several writers | 16 |
| B-06 | Diagnostics and the node overview | 16 |
| B-07 | Orphan bookkeeping | 4 |
| B-08 | Roles, several principals, transport security | 4 |
| B-09 | Continuous reconcile and drift correction | 14 |
| B-10 | Adoption and foreign interfaces | 36 |
| B-11 | Key and client-configuration extras | 10 |
| B-12 | Resource fields beyond the wrapper | 2 |
| | **Total** | **168** |

---

## B-01 — Metrics and audit

**Defers:** `REQ-OBS-001` to `REQ-OBS-004`, `REQ-OBS-012`, `REQ-OBS-020` to `REQ-OBS-023`,
`REQ-SEC-083`. Ten requirements.

**Returns when:** a dashboard consumes the node, or a second principal exists.

The structured logs of `REQ-OBS-010` and `REQ-OBS-011` stay in P3. The audit log attributes
operations among principals, and one token is one principal.

**On return:** `REQ-OBS-004` counts drift, which exists only once B-09 does.

---

## B-02 — Backup, restore, upgrade and migration

**Defers:** all of [SPEC-10](../20-spec/SPEC-10-lifecycle.md) — `REQ-LIF-001`, `REQ-LIF-010` to
`REQ-LIF-012`, `REQ-LIF-020` to `REQ-LIF-023`, `REQ-LIF-030`, `REQ-LIF-031`, `REQ-LIF-040`,
`REQ-LIF-050` to `REQ-LIF-053`. Fifteen requirements.

**Returns when:** `OQ-06`, `OQ-07` and `OQ-08` settle, which is also what moves SPEC-10 from
`Draft` to `Accepted`.

**On return:** under ADR-0013 the interface files in `/etc/wireguard/` are part of what a backup
restores. The audit's section 4.6 lists the gaps SPEC-10 carries.

---

## B-03 — Install script and release pipeline

**Defers:** `REQ-CFG-029` to `REQ-CFG-036`. Eight requirements.

**Returns when:** the agent is installed by someone other than its author, or the repository is
published with releases to download.

**On return:** `REQ-CFG-045` makes the package generate the token, so the script's own token
requirements, `REQ-CFG-032` and `REQ-CFG-033`, are re-read against it; `REQ-CFG-036` names
`arm64`, which the platform floor excludes.

---

## B-04 — Forward policy and NAT

**Defers:** all of [SPEC-02](../20-spec/SPEC-02-forward-policy.md) — `REQ-FWD-001` to
`REQ-FWD-005`, `REQ-FWD-010` to `REQ-FWD-017`, `REQ-FWD-020` to `REQ-FWD-025`, `REQ-FWD-030` to
`REQ-FWD-032`, `REQ-FWD-040` to `REQ-FWD-042` — with the validation that reads it, `REQ-VAL-021`
to `REQ-VAL-023`, `REQ-VAL-034` and `REQ-VAL-035`, and the sysctl carve-out, `REQ-CFG-011` to
`REQ-CFG-013`. Thirty-three requirements.

**Returns when:** peers of one interface must be isolated from each other, or the agent must
manage egress and NAT rather than the host.

Until then forwarding and NAT are the host's, or an interface's own `PostUp` and `PostDown` set
through the CLI — section 5 of [SPEC-13](../20-spec/SPEC-13-applying-changes.md) and
[ADR-0017](../10-decisions/ADR-0017-operator-hooks-through-the-cli.md).

**On return:** the mechanism is open. libnftables with an owned, persistent table is F-01 and
D-16 of the [audit](spec-audit.md); it needs `NFT_TABLE_F_PERSIST`, which Debian 13 carries and
Ubuntu 24.04 carries only with its HWE kernel, and Ubuntu 24.04's libnftables 1.0.9 cannot express
it. The audit's D-01 and D-07 decide the sysctl rule and the treatment of foreign WireGuard
interfaces.

---

## B-05 — API scaffolding for several writers

**Defers:** `REQ-API-011` to `REQ-API-015` (pagination), `REQ-API-030` to `REQ-API-035`,
`REQ-API-062`, `REQ-API-072` and `REQ-API-077` (revisions, `ETag` and batch writes),
`REQ-API-061` (the compatibility gate in CI), `REQ-RES-031`. Sixteen requirements.

**Returns when:** a second writer exists, or a client is published to anyone.

With one writer an omitted revision is an unconditional overwrite, which is the behaviour a
single operator wants; pagination guards a ceiling one node does not reach.

**On return:** the audit's M-12 — a batch cannot be atomic in the kernel, only in the store.

---

## B-06 — Diagnostics and the node overview

**Defers:** `REQ-DIA-001` to `REQ-DIA-005`, `REQ-DIA-010`, `REQ-DIA-011`, `REQ-DIA-020` to
`REQ-DIA-025`, `REQ-DIA-030`, `REQ-DIA-031`, `REQ-DIA-052`. Sixteen requirements.

**Returns when:** an operator needs a node's health without logging in to it.

**On return:** the check list reads nftables and sysctl state that exists only with B-04, and
`REQ-DIA-010` must stop contradicting `REQ-FWD-040` (audit M-08).

---

## B-07 — Orphan bookkeeping

**Defers:** `REQ-RCN-033` to `REQ-RCN-035`, `REQ-RCN-037`. Four requirements.

**Returns when:** a failed delete must survive a restart as something other than an error.

Under ADR-0013 a delete that fails is restored and reported (`REQ-APL-008`), so nothing is left
half-deleted for a record to remember.

---

## B-08 — Roles, several principals, transport security

**Defers:** `REQ-SEC-020`, `REQ-SEC-021`, `REQ-SEC-075`, `REQ-SEC-079`. Four requirements.

**Returns when:** a second caller needs narrower rights than full control, or the listener must
be reached across a network the operator does not control.

The second trigger brings TLS, which has no requirement yet: the measurements in
[ADR-0015](../10-decisions/ADR-0015-network-listener-with-a-shared-secret.md) are where it starts.

---

## B-09 — Continuous reconcile and drift correction

**Defers:** `REQ-RCN-010` to `REQ-RCN-013`, `REQ-RCN-051` (field ownership), `REQ-RCN-020` to
`REQ-RCN-022`, `REQ-RCN-024` (triggers and algorithm), `REQ-RCN-040`, `REQ-RCN-041` (degraded
state and backoff), `REQ-RES-032`, `REQ-CLI-008`, `REQ-CLI-009`. Fourteen requirements.

**Returns when:** hand edits or other tools changing the agent's interfaces cost the operator
real time.

**On return:** `REQ-RCN-022` is written as netlink steps. Under ADR-0013 reconcile compares the
store with the rendered file and with `wg show`, and repairs through the same synchronisation and
restart as [SPEC-13](../20-spec/SPEC-13-applying-changes.md); the algorithm is re-specified, not
implemented as it stands. The audit's D-04, D-05 and D-13 are its questions.

---

## B-10 — Adoption and foreign interfaces

**Defers:** `REQ-RCN-031`, `REQ-RCN-036`, `REQ-RCN-060` to `REQ-RCN-067`, `REQ-RCN-069` to
`REQ-RCN-074` (adoption and release), `REQ-RES-017`, `REQ-RES-018` (ownership and a fresh
identifier), `REQ-API-067`, `REQ-API-068`, `REQ-DIA-040` to `REQ-DIA-051` (the readiness report),
`REQ-CLI-004` to `REQ-CLI-007` (`doctor` and `adopt`). Thirty-six requirements.

**Returns when:** interfaces configured by hand must come under management without being copied
into a document first.

The agent manages only what it created, by the operator's decision of 2026-09-29. An interface
kept by hand moves once, through the JSON document of `REQ-CLI-025`, which the operator fills from
the old file; for an internal tool that one-time move is all the migration needed.

**On return:** under ADR-0013 adopting means taking over a file in `/etc/wireguard/` that
`REQ-APL-002` forbids the agent to read; adoption is the explicit exception, and its requirements
are re-specified around the file rather than the kernel dump.

---

## B-11 — Key and client-configuration extras

**Defers:** `REQ-KEY-003`, `REQ-KEY-004` (`RotateInterfaceKey`), `REQ-KEY-015` (a switch for
server-side generation), `REQ-KEY-030` to `REQ-KEY-033` (client routing modes),
`REQ-KEY-039` (QR codes), `REQ-KEY-040`, `REQ-KEY-041` (`GenerateKeyPair`). Ten requirements.

**Returns when:** the operator asks for one. These are the most likely candidates for P4.

`UpdateInterface` with a new `private_key` already rotates a key; the dedicated operation adds
generation and the warning. `AUTO` routing reads `forward_policy` and waits for B-04.

---

## B-12 — Resource fields beyond the wrapper

**Defers:** `REQ-RES-015`, `REQ-RES-026` (`instance_id` and counter-reset detection), and the
fields `fwmark` and `manage_routes`, which carry no requirement of their own. Two requirements.

**Returns when:** a platform does traffic accounting from the counters, or routing needs a
firewall mark or a routing table other than the main one.

**On return:** counters are per peer in the kernel, so a peer removed and added again resets them
while the interface identifier stays (audit M-30).

## Candidates for P4

Chosen after P1 to P3 run on a real node, from what that use shows is missing. The likely ones:
B-11 in whole or in part, drift detection from B-09, and a node overview from B-06. B-04 returns
the moment peers must be isolated from each other.

## What is not in this file

Work without a requirement: rebuilding `packaging/systemd/` from SPEC-09 in P3, and the open
question of [SPEC-13](../20-spec/SPEC-13-applying-changes.md) — whether a hand edit to a file the
agent created should block the next write.
