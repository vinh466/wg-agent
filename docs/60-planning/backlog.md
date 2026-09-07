---
updated: 2026-09-05
---

# Backlog

Requirements deferred out of the near-term build. Nothing here is withdrawn: every ID below
stays live and normative in its module, and the [module index](../20-spec/README.md) is
unchanged. Deferral is a statement about build order, not about correctness.

## Why this file exists

The [roadmap](roadmap.md) sizes the work for the product described in
[product.md](../00-overview/product.md) — a component consumed by VPN platforms, Terraform
providers and Kubernetes operators. The near-term target is narrower: a single operator
managing their own node, adopting an interface that already exists, reaching the API locally
behind one token.

Against that narrower target, this file defers **97 of the 310 live requirements**, leaving
213 in the near-term build. Deferring them explicitly is what keeps the count honest — the
alternative is a milestone plan that looks achievable only because nobody counted.

Reading the milestone numbers as the smaller slice does not work: M0 through M2 still carry
280 of 310 requirements, because the machine-facing scaffolding is spread across the MVP
modules rather than concentrated after them. Counts are as of the front-matter date.

## How to read an entry

| Field | Meaning |
|---|---|
| **Defers** | The requirement IDs moved out of the near-term build |
| **Keeps** | Requirements in the same area that stay in, and why |
| **Returns when** | The condition that puts the entry back on the critical path |

## Summary

| Entry | Area | Deferred |
|---|---|---|
| B-01 | Metrics, logs and audit | 10 |
| B-02 | Backup, restore, upgrade and migration | 15 |
| B-03 | Packaging, install script and release pipeline | 20 |
| B-04 | Forward policy and NAT | 22 |
| B-05 | API scaffolding for machine consumers | 14 |
| B-06 | Interface diagnostics | 9 |
| B-07 | Orphan bookkeeping | 4 |
| B-08 | Multi-principal machinery | 3 |
| | **Total** | **97** |

---

## B-01 — Metrics, logs and audit

**Defers:** all of [SPEC-08](../20-spec/SPEC-08-observability.md) — `REQ-OBS-001` to
`REQ-OBS-004`, `REQ-OBS-010`, `REQ-OBS-011`, `REQ-OBS-020` to `REQ-OBS-023`. Ten requirements.

**Returns when:** a second principal exists, or a dashboard consumes the node.

The audit log attributes state-changing operations among principals. One operator holding one
admin token is one principal, so the record answers a question nobody is asking. Prometheus
collectors and the cardinality controls in `REQ-OBS-003` size for thousands of peers.

Already scheduled at M3, so this entry records a reason rather than a change.

---

## B-02 — Backup, restore, upgrade and migration

**Defers:** all of [SPEC-10](../20-spec/SPEC-10-lifecycle.md) — `REQ-LIF-001`, `REQ-LIF-010`
to `REQ-LIF-012`, `REQ-LIF-020` to `REQ-LIF-023`, `REQ-LIF-030`, `REQ-LIF-031`, `REQ-LIF-040`,
`REQ-LIF-050` to `REQ-LIF-053`. Fifteen requirements.

**Returns when:** `OQ-06`, `OQ-07` and `OQ-08` settle, which is also what moves SPEC-10 from
`Draft` to `Accepted`.

One correction worth recording, because the name invites the opposite assumption: `import`
under `REQ-LIF-051` reads a wg-agent export file, and `REQ-LIF-023` refuses a non-empty store.
It does **not** ingest an interface that already exists in the kernel. SPEC-10 is not the home
of interface adoption and never was.

---

## B-03 — Packaging, install script and release pipeline

**Defers:** `REQ-CFG-011` to `REQ-CFG-013` (systemd hardening verified across six
distributions), `REQ-CFG-020` to `REQ-CFG-026`, `REQ-CFG-028` (the `.deb`, its conffiles and
maintainer scripts), `REQ-CFG-029` to `REQ-CFG-036` and `REQ-CFG-038` (install script,
checksum verification, dual-architecture release). Twenty requirements.

**Keeps:** `REQ-CFG-001` and `REQ-CFG-002` (environment overrides, refuse unknown keys),
`REQ-CFG-003` (the token file maps a token to one role and one label), `REQ-CFG-004` and
`REQ-CFG-005` (`/etc/default/wg-agent`, tolerating its absence), `REQ-CFG-010` (a dedicated
account with `AmbientCapabilities=CAP_NET_ADMIN`), and `REQ-CFG-027`.

`REQ-CFG-027` — package removal never deletes a WireGuard link — is the single most important
requirement in this module for a node whose interface predates the agent. It stays in.

**Returns when:** the agent is installed somewhere its author does not have a shell.

`go build` plus one unit file reaches a node the operator already administers. The `.deb` and
the install script solve distribution to strangers.

---

## B-04 — Forward policy and NAT

**Defers:** `REQ-FWD-001` to `REQ-FWD-005`, `REQ-FWD-010` to `REQ-FWD-017`, `REQ-FWD-021`,
`REQ-FWD-023`, `REQ-FWD-030` to `REQ-FWD-032`, `REQ-FWD-040` to
`REQ-FWD-042`. Twenty-one requirements.

**Keeps:** `REQ-FWD-020` (set `forwarding = 1` on the agent's own WireGuard interfaces),
`REQ-FWD-025` (verify the sysctl is writable at startup, returned from this item once
`REQ-FWD-020` was implemented — the check is what turns a hardened unit missing the
`REQ-CFG-011` carve-out from a puzzling `DEGRADED` into a named startup failure) and
`REQ-FWD-022` (never touch the sysctl of an interface desired state does not describe, amended
by [ADR-0011](../10-decisions/ADR-0011-operator-initiated-adoption.md) to test desired-state
membership rather than creation) and `REQ-FWD-024`, which records the pre-adoption value.

**Returns when:** a second interface exists, or peers need egress.

**Deviation recorded:** the reconcile engine performs every step of `REQ-RCN-022` except step
10, the nftables table. Step 9 is performed, because `REQ-FWD-020` is one of the three
requirements this item keeps: without it two peers of one interface are not forwarded to each
other, which is the whole of the flat-LAN-per-group pattern.

The consequence is that an `ALLOW` axis is enforced and a `DENY` axis is not. `inter_interface`
and `external` default to `DENY` and stay unenforced until step 10 lands, which on a
single-interface node with no egress makes the claim vacuous rather than false — the trade this
item already argues for. `nat` is stored and not applied.

An unwritable sysctl is caught at startup by `REQ-FWD-025` and named
`SYSCTL_WRITE_DENIED`, so a unit hardened without the `REQ-CFG-011` carve-out fails at
deployment. Should one slip past the check, reconcile reports the same code as a `DEGRADED`
condition on the affected interface: the tunnel still works, and forwarding between its peers
does not.

The default `ForwardPolicySpec` sets `inter_interface: DENY` and `external: DENY`, and a
`DENY` axis requires nftables. On a node with one interface there is no second interface to
isolate it from. Deferring this trades an unenforced default for not carrying
`google/nftables` on day one — and that trade must be written down rather than assumed, since
the roadmap's own argument for pulling SPEC-02 into M2 is that defaults claiming isolation
they do not enforce mislead operators. A single-interface node makes the claim vacuous rather
than false, which is why the trade is acceptable here and not in a release.

---

## B-05 — API scaffolding for machine consumers

**Defers:** `REQ-API-011` to `REQ-API-015` (pagination), `REQ-API-031` to `REQ-API-033`
(optimistic concurrency), `REQ-API-034`, `REQ-API-035` and `REQ-API-062`
(batch peer updates), `REQ-API-060` and `REQ-API-061` (published clients and the
`buf breaking` compatibility gate). Thirteen requirements.

**Keeps:** `REQ-API-030` and `REQ-API-077`, the revision itself. `REQ-RES-031` is not deferred
and already puts an opaque `revision` in `status`, so deferring the field while keeping the
requirement that it move when the peer set changes would leave two live requirements without a
subject. What is deferred is the enforcement: `REQ-API-032` makes an omitted revision an
unconditional overwrite, which is the behaviour a single writer wants.

**Returns when:** a second writer exists, or a client is published to anyone.

Pagination guards a ceiling the scale target itself puts at ten interfaces and a thousand
peers. Optimistic concurrency arbitrates between concurrent writers; with one writer
`REQ-API-032` already makes an omitted revision an unconditional overwrite, which is the
behavior a single operator wants anyway.

`REQ-API-060` and `REQ-API-061` are deferred **obligations**, not deleted ones. The `.proto`
stays the source of truth under [ADR-0003](../10-decisions/ADR-0003-protobuf-source-of-truth.md);
what is deferred is the promise not to break generated clients that nobody has generated.

---

## B-06 — Interface diagnostics

**Defers:** `REQ-DIA-001`, `REQ-DIA-004`, `REQ-DIA-010`, `REQ-DIA-011`, `REQ-DIA-022` to
`REQ-DIA-025`, `REQ-DIA-031`. Nine requirements.

**Keeps:** `REQ-DIA-020` and `REQ-DIA-021` — the node overview and its status vocabulary,
reduced from nine components to the four that exist in the near-term build. Also
`REQ-DIA-002`, `REQ-DIA-003`, `REQ-DIA-005` and `REQ-DIA-030`, which the adoption readiness
report of `REQ-DIA-040` builds on: `REQ-DIA-041` adopts the finding shape of `REQ-DIA-002`,
`REQ-CLI-005` renders the `hint` of `REQ-DIA-003`, and `REQ-DIA-030` supplies its closed
`hint_code` set. All of section 5 of [SPEC-11](../20-spec/SPEC-11-diagnostics.md) is
near-term work.

**Returns when:** B-04 returns. The fourteen-check `DiagnoseInterface` exists to debug the
seven conditions that must hold for two peers to communicate, and those conditions are the
forward policy this build does not enforce.

One check is pulled forward ahead of the rest: `REQ-DIA-043` detects a contending `wg-quick`
unit, which adoption depends on.

---

## B-07 — Orphan bookkeeping

**Defers:** `REQ-RCN-033` to `REQ-RCN-035` and `REQ-RCN-037` — the deletion record that
survives a restart and distinguishes an orphaned link from a foreign one.

**Keeps:** `REQ-RCN-030` and `REQ-RCN-031` (foreign links reported, never modified) — the
requirements that make coexistence with an existing interface safe, and the ones adoption must
amend rather than ignore. Also `REQ-RCN-038`, which is plain transactional correctness.

**Returns when:** an operator cannot log in to remove a link by hand.

The deletion record exists so a fleet controller need not shell into the node. An operator who
is already there runs `ip link del`.

---

## B-08 — Multi-principal machinery

**Defers:** `REQ-SEC-079` (principal attribution, whose only consumer is the deferred audit
log), `REQ-RES-014` and `REQ-RES-026` (`labels` reserved for the platform layer, and
`instance_id` exposed so a platform can detect counter resets).

**Keeps:** `REQ-SEC-020`, `REQ-SEC-021` and `REQ-SEC-075`. Two roles and one token-to-role
mapping is one annotation and one check; removing it would churn four requirements to save
nothing. `REQ-RES-015` stays, because `instance_id` still has to be generated — only its
platform-facing exposure is deferred.

**Returns when:** a second principal exists. Same trigger as B-01.

---

## Not deferred, and deliberately so

| Kept | Why |
|---|---|
| The declarative model — [ADR-0001](../10-decisions/ADR-0001-declarative-model.md) | Its stated conditions for revisiting are **None**: without it the agent is an API wrapper rather than a control plane. For one operator it is worth more, not less — it is what makes an interface survive a reboot without `wg-quick` |
| `REQ-RCN-013`, `REQ-RCN-051` | `endpoint` stays kernel-owned. This is what stops a roaming client being cut on every reconcile pass |
| `REQ-RCN-021` | Netlink event subscription. Detecting an externally deleted link is the reconcile loop's whole value on a node that other tools also touch |
| `REQ-RCN-041` | Backoff with jitter. A retry loop without it is a busy loop |
| `REQ-SEC-041` | No `exec.Command` in production paths |
| `REQ-CFG-027` | Package removal never deletes a link |

One cost saving that needs no deferral at all: **bbolt is not mandated.** The line naming it in
[SPEC-03](../20-spec/SPEC-03-state-reconcile.md) carries no RFC 2119 keyword, and prose without
a keyword is explanatory. `REQ-RCN-001` to `REQ-RCN-005` are satisfied by a JSON file written
to a temporary path and renamed atomically at mode `0600`.

## What is not in this file

Interface adoption is **near-term work, not deferred work**. It is specified in
[ADR-0011](../10-decisions/ADR-0011-operator-initiated-adoption.md) and in section 6.3 of
[SPEC-03](../20-spec/SPEC-03-state-reconcile.md), section 5 of
[SPEC-11](../20-spec/SPEC-11-diagnostics.md) and section 3 of
[SPEC-12](../20-spec/SPEC-12-cli.md). Naming the sections rather than requirement ranges is
deliberate: three earlier enumerations of those ranges went stale within a day. It is the
reason the near-term target is worth building at all: it is what lets the agent take over a node
that already runs WireGuard instead of demanding one that does not.

Two entries above are load-bearing for it. B-06 keeps `REQ-DIA-020` and `REQ-DIA-021`, which the
readiness report shares a module with, and B-04 keeps `REQ-FWD-022`, which governs the sysctl of
an interface the agent did not create.
