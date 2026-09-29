---
updated: 2026-09-29
---

# Roadmap

## What MVP means

MVP is **M0 through M2**. The bar is four operator-visible capabilities:

1. Install, update and uninstall on Debian or Ubuntu with one script from the public repository
2. Per-host overrides through `/etc/default/wg-agent`, the way any Debian service behaves
3. A working API on a loopback address, with a token issued during installation
4. `GET /v1/overview` reporting every agent component, usable for a dashboard or a quick debug

M3 onwards is beyond MVP. Metrics, audit log, backup and restore, NAT, and the Terraform
provider all wait.

## Milestones

| Milestone | Content | Specs | Exit criteria |
|---|---|---|---|
| **M0 — Foundation** | Repo layout, `.proto` contract, platform ports and the netlink adapter, in-memory CRUD | SPEC-01, SPEC-04 | Tests in a netns create an interface, add a peer, read statistics |
| **M1 — Declarative** | file-backed store, reconcile engine, revisions, validation, error model, diagnostics, node overview | SPEC-03, SPEC-06, SPEC-07, SPEC-11 | Reboot self-heals. Manually deleting a link rebuilds it. Roaming survives. `GET /v1/overview` names a broken component |
| **M2 — MVP** | Unix socket, loopback HTTP with tokens, roles, systemd hardening, `.deb`, install script, CLI, forward policy axes | SPEC-05, SPEC-09, SPEC-12, SPEC-02 in part | One script installs, prints a token, and the API answers on loopback. Uninstall leaves links alone. `intra`/`inter`/`external` DENY actually blocks |
| **M3 — Operations** | Metrics, audit log, backup and restore, log rotation | SPEC-08, SPEC-10 | Dashboard shows handshake, traffic and drift. Restore onto a new node succeeds |
| **M4 — Networking and UX** | NAT, `ALLOW_LIST`, uplink forwarding, `ClientRouting.AUTO`, QR, batch, watch | SPEC-02 remainder | All four topology patterns verified by netns tests |
| **M5 — Ecosystem** | Terraform provider, a client SDK, published OpenAPI, user documentation | — | `terraform apply` manages peers and detects drift |

## The SPEC-02 split

The default `ForwardPolicySpec` sets `inter_interface: DENY` and `external: DENY`, and a
`DENY` axis needs nftables. A release whose defaults claim isolation it does not enforce would
be lying to its operators, so the axes ship with MVP and the rest follows at M4.

| Lands at M2 | Deferred to M4 |
|---|---|
| `REQ-FWD-001` to `REQ-FWD-005` — the axes and their semantics | `REQ-FWD-014` — `ALLOW_LIST` |
| `REQ-FWD-010` to `REQ-FWD-013`, `REQ-FWD-015` to `REQ-FWD-017` — nftables rules | `REQ-FWD-023` — uplink forwarding |
| `REQ-FWD-040`, `REQ-FWD-041` — table ownership and teardown | `REQ-FWD-030` to `REQ-FWD-032` — NAT and masquerade |
| `REQ-FWD-020` to `REQ-FWD-022`, `REQ-FWD-024`, `REQ-FWD-025`, `REQ-FWD-042` — forwarding sysctl | `REQ-KEY-031` `ALLOW_LIST` branch — `ClientRouting.AUTO` |

`external = ALLOW` stays unreachable until M4 without extra work: `REQ-FWD-023` rejects it
unless `nat.enable_uplink_forwarding` is set, and NAT does not exist yet. The MVP therefore
supports `external: DENY` only, and the existing validation says so rather than failing
obscurely.

## Changes from the original plan

**SPEC-11 (diagnostics) moved from M4 to M1**, and gained the node overview. Once
`ForwardPolicySpec` exists, seven independent conditions must hold for two peers to
communicate. Diagnostic tooling is needed as soon as topology patterns are first tested.

**SPEC-10 (lifecycle) added at M3.** It came out of the end-to-end review: backup, restore,
upgrade, audit log rotation and scale targets had no home in the earlier plan.

**mTLS dropped from M2.** [ADR-0009](../10-decisions/ADR-0009-local-only-listeners.md)
confines v1 to a unix socket plus a loopback HTTP listener with static tokens. That removes
certificate issuance, node enrollment and certificate rotation — the largest block of work in
M2 — and pushes remote management to a later version. M5 gains a dependency: the Terraform
provider needs a tunnel or a co-located component to reach a node.

**SPEC-12 (CLI) added at M2.** Token issuance, export and import all needed an operator-facing
command surface that no module owned.

**Part of SPEC-02 pulled from M4 into M2**, per the split above.

**Health and readiness merged.** `/v1/healthz` and `/v1/readyz` became a single `/v1/health`,
with `/v1/overview` carrying the component detail that a probe never needed.

## Milestone dependencies

```
M0 ──► M1 ──► M2 ──► M3 ──► M4 ──► M5
```

- M2 needs the store and reconcile engine from M1, since the install script's exit criteria
  cover a running agent
- M4 extends the forward policy work started in M2
- M5 needs a stable API from M0 and complete policy support from M4

**M0 to M2 are build order, not shipping boundaries.** Nothing is released before M2. That
matters because several M1 modules reference SPEC-02, which lands at M2: reconcile steps 9 and
10 in `REQ-RCN-022`, the policy validation rules `REQ-VAL-021` to `REQ-VAL-023` and
`REQ-VAL-034` to `REQ-VAL-035`, and the nftables and sysctl checks in SPEC-11. Those pieces
stay inert through M1 and become live in M2. Reading the milestone numbers as independent
releases is what makes that look like a dependency inversion.

## State as of the front-matter date

The specification is complete for M0 through M2. The implementation is at zero, deliberately.

An implementation in Go reached a working vertical slice, and a partial port of it to C#
followed the move to .NET. Both were removed, and the code is rebuilt from the specification
alone, because a port reproduces decisions the specification never made. Two were found on
inspection: the forwarding condition of `REQ-FWD-020` implemented differently from its text,
with the comment that justified the difference lost in the port, and a reconcile behaviour
for a missing private key that no requirement asks for. A port carries such decisions
without marking them, so removing the code was cheaper than proving the absence of more.

| Item | State |
|---|---|
| Documentation architecture, rules, checks | Done — docs, traceability and mermaid checks |
| ADR-0001 through ADR-0012 | Accepted; ADR-0002 superseded by ADR-0012 |
| SPEC-01 through SPEC-09, SPEC-11, SPEC-12 | Accepted |
| SPEC-10 | Draft — three M3 decisions unsettled |
| Open questions blocking the MVP | 0 — see [open questions](open-questions.md) |
| `.proto` contract | Committed — source of truth under ADR-0003 |
| systemd unit, sysusers and tmpfiles | Committed with the removed implementation — D-15 of the [audit](spec-audit.md) |
| Full specification audit | Read complete — 16 decisions and 30 fixes in the [audit](spec-audit.md), none applied |
| Implementation | None. Rebuilt module by module from the specification |

## Interface adoption

[ADR-0011](../10-decisions/ADR-0011-operator-initiated-adoption.md) added adoption of an
interface the agent did not create. It splits across the existing milestones rather than forming
section 6.3 of [SPEC-03](../20-spec/SPEC-03-state-reconcile.md) needs the store and reconcile
engine, so it lands with M1, while `doctor`, `adopt` and `release` are a command surface and land
with M2 alongside the rest of SPEC-12.

Section 5 of [SPEC-11](../20-spec/SPEC-11-diagnostics.md) sits with M1, matching the milestone
that module already carries. Sections are named rather than requirement ranges because the
ranges moved three times while the design settled.

Adoption is the capability that makes the agent usable on a node where WireGuard already runs,
which is the ordinary case rather than the exception. The MVP bar in the first section predates
it and does not name it.

## Next actions

1. Close the [specification audit](spec-audit.md): decide D-01 to D-16, then apply them and
   the M items as one spec change per module, then revise the backlog and the test guide
   against the result (its section 6)
2. Build tooling for .NET: a task runner and the container tiers of the
   [test guide](../50-guides/running-tests.md)
3. Implement module by module, following the module workflow in `CLAUDE.md`: the platform
   ports and the netlink adapter, the store, validation, reconcile, the service layer, the
   API listeners and tokens, the CLI
4. Settle OQ-06 to OQ-08 before M3 opens, which moves SPEC-10 to `Accepted`
