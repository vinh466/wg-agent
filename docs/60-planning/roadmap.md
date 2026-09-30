---
updated: 2026-10-01
---

# Roadmap

## What the first release is

A wrapper over `wg` and `wg-quick` that replaces an operator's manual work on a node —
[ADR-0013](../10-decisions/ADR-0013-drive-wg-and-wg-quick.md). It creates interfaces, adds and
removes peers, hands a client its configuration and reads status, from a CLI on the node and from
a REST API reached over the operator's private network —
[ADR-0014](../10-decisions/ADR-0014-rest-api-described-by-openapi.md) and
[ADR-0015](../10-decisions/ADR-0015-network-listener-with-a-shared-secret.md). It manages only
the interfaces it created, and it is an internal tool: support for anyone but its operator is not
planned.

It runs on Debian 13 and later and Ubuntu 24.04 LTS and later, on `linux-x64` — D-10 of the
[specification audit](spec-audit.md). Everything else waits in the [backlog](backlog.md), and
phase P4 is chosen from it once P1 to P3 are in use.

## Phases

| Phase | Content | Modules | Exit criteria |
|---|---|---|---|
| **P1 — Core and CLI** | The store and its lock; interfaces and peers through files in `/etc/wireguard/`, `wg-quick@` units and `wg syncconf`; status from `wg show`; validation; key generation; the client configuration built when a peer is created; the configuration file; the CLI | SPEC-01, SPEC-03, SPEC-06, SPEC-07, SPEC-13; SPEC-05 sections 7 and 8; SPEC-09 section 2; SPEC-12 except `serve` and `token` | In a container: create an interface, add a peer with a generated key pair and connect a client with the printed file; add a second peer while the first pings without loss; route a peer outside the subnet and see the unit restart with the route installed; delete the interface and leave no file behind |
| **P2 — REST API** | `api/openapi.yaml`, written first; `serve`; the HTTP listener, one token, problem documents for errors; health and version; `token rotate` | SPEC-04; SPEC-05 sections 2 to 4; SPEC-09 section 2.1; SPEC-12 `serve` and `token` | Every operation of SPEC-04 answers over HTTP with the token and refuses without it; the contract test passes; the CLI and the API change one node side by side |
| **P3 — Packaging and operation** | The `.deb` with its confined unit, the token generated at install, the conffile and maintainer scripts; structured logs; a test tier under each distribution's own systemd; the operator guide | SPEC-09 sections 3 to 5; SPEC-05 section 6; SPEC-08 section 3 | Installed on Debian 13 and Ubuntu 24.04, the agent's interfaces survive a reboot and the package's removal; the unit's confinement holds under each distribution's systemd |
| **P4 — From use** | Chosen from the backlog after P1 to P3 run on a real node | — | — |

P1 carries 127 requirements, P2 43 and P3 16: 186 of the 341 live ones. The other 155 are in the
backlog, entry by entry. P1 and P2 grew by 24 while they were built: each was a doubt the code met
and the specification settled first, as the module workflow requires.

Inside a phase the order is the module workflow of `CLAUDE.md`: read the module, resolve doubt as
a spec change, write a failing test per requirement, write the code, keep nothing unspecified, run
the checks. Tooling is built at the start of P1 — the task runner, and the container tier the
first integration test needs.

## State as of the front-matter date

| Item | State |
|---|---|
| Documentation architecture, rules, checks | Done — docs, traceability and mermaid checks |
| Decisions | ADR-0001 to ADR-0018 accepted, except those superseded: ADR-0002, ADR-0012 and ADR-0013 by their successors, ADR-0003 by ADR-0014, ADR-0009 by ADR-0015, ADR-0010 by ADR-0018 |
| Specification | 341 live requirements — 186 in P1 to P3, 155 deferred; 52 struck |
| SPEC-01, SPEC-03, SPEC-04, SPEC-06, SPEC-07, SPEC-12, SPEC-13 | `Implemented` — every requirement outside the backlog has a test |
| SPEC-10 | `Draft` — its three open decisions wait with B-02 |
| API contract | `api/openapi.yaml` is the source of truth; `docs/30-api` is rendered from it |
| systemd unit | Rebuilt from SPEC-09 in P3 |
| Specification audit | Reassessed after the wrapper cut — section 8 of the [audit](spec-audit.md) |
| Implementation | P1 and P2 delivered: the store and its lock, apply, the shared service, the configuration file, the CLI, and the REST API with one bearer token and `serve`. The unit and integration tiers pass; the exit criteria of P1 and P2 run in the integration tier, and the whole binary publishes NativeAOT without warning |

## How the plan got here

The plan before ADR-0013 sized a control plane for machine consumers in milestones M0 to M5:
netlink, a reconcile loop, forward policy and NAT, adoption, diagnostics, metrics, backup, and a
Terraform provider. An implementation in Go reached a working slice of it, and a partial port to
C# followed the move to .NET. Both were removed so the code is rebuilt from the specification
alone, because a port reproduces decisions the specification never made.

The specification audit of September 2026 found the gaps that rebuilding would have met, and the
operator's own need turned out narrower than the plan: the manual `wg-quick` work on each node.
ADR-0013 to ADR-0015 narrowed the first release to it. The milestone table of the earlier plan
remains in the history of this file.

## Next actions

1. Start P3: the `.deb` and its confined systemd unit, the token generated at install, the
   conffile and maintainer scripts (SPEC-09 sections 3 to 5), structured logs (SPEC-08 section 3),
   and the packaging test tier under each distribution's own systemd
2. Implement P3 module by module, following the module workflow in `CLAUDE.md`
