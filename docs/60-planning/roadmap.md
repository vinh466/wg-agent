---
updated: 2026-08-04
---

# Roadmap

## Milestones

| Milestone | Content | Specs | Exit criteria |
|---|---|---|---|
| **M0 — Foundation** | Repo layout, `.proto`, `buf` toolchain, netlink and wgctrl adapters, in-memory CRUD | SPEC-01, SPEC-04 | Tests in a netns create an interface, add a peer, read statistics |
| **M1 — Declarative** | bbolt store, reconcile engine, revisions, validation, error model, **diagnostics** | SPEC-03, SPEC-06, SPEC-07, SPEC-11 | Reboot self-heals. Manually deleting a link rebuilds it. Roaming survives |
| **M2 — Security** | Unix socket, mTLS, roles, systemd hardening, `.deb` package | SPEC-05, SPEC-09 | Install from `.deb`, run non-root with `CAP_NET_ADMIN`, mTLS working |
| **M3 — Operations** | Metrics, audit log, health, backup and restore, log rotation | SPEC-08, SPEC-10 | Dashboard shows handshake, traffic and drift. Restore onto a new node succeeds |
| **M4 — Networking and UX** | `ForwardPolicySpec`, nftables, per-interface sysctl, NAT, `ClientRouting.AUTO`, QR, batch, watch | SPEC-02 | All four topology patterns verified by netns tests |
| **M5 — Ecosystem** | Terraform provider, Go client SDK, published OpenAPI, user documentation | — | `terraform apply` manages peers and detects drift |

## Changes from the original plan

**SPEC-11 (diagnostics) moved from M4 to M1.** Once `ForwardPolicySpec` exists, seven
independent conditions must hold for two peers to communicate. Diagnostic tooling is needed as
soon as topology patterns are first tested rather than at M4.

**SPEC-10 (lifecycle) added at M3.** It came out of the end-to-end review: backup, restore,
upgrade, audit log rotation and scale targets had no home in the earlier plan.

## Milestone dependencies

```
M0 ──► M1 ──► M2 ──► M3
        │              │
        └──► M4 ───────┴──► M5
```

- M4 needs the store and reconcile engine from M1 but not the security work from M2
- M5 needs a stable API (M0) and complete policy support (M4)
- M3 needs both M1 and M2

## State as of the front-matter date

Implementation has not started. The project is in its specification phase.

| Item | State |
|---|---|
| Documentation architecture | Done |
| Contributor and agent rules | Done |
| Automated documentation checks | Done |
| ADR-0001 through ADR-0008 | Accepted |
| SPEC-01 through SPEC-09 | Accepted |
| SPEC-10, SPEC-11 | Draft — unapproved |
| Milestone-blocking open questions | 5 — see [open questions](open-questions.md) |
| Code | Not started |

## Next actions

1. Approve SPEC-10 and SPEC-11, moving them to `Accepted`
2. Settle OQ-01 (certificate issuance) and OQ-03 (rate limiting), which need an owner decision
3. Scaffold the repository per the [architecture](../00-overview/architecture.md) and begin M0
