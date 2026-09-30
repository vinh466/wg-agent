---
id: packaging
title: Packaging assets
status: empty
owner: Vinh Nguyen
created: 2026-09-07
updated: 2026-09-30
---

# Packaging

Nothing is here yet. The `.deb` and its systemd unit are built in phase P3 of the
[roadmap](../docs/60-planning/roadmap.md), from section 4 and section 5 of
[SPEC-09](../docs/20-spec/SPEC-09-config-deployment.md).

The unit, sysusers and tmpfiles files that sat here were written alongside the removed Go
implementation, for a non-root account and a forwarding-sysctl carve-out. Under
[ADR-0013](../docs/10-decisions/ADR-0013-drive-wg-and-wg-quick.md) the agent runs as root,
confined by its unit, and writes no sysctl, so the files were removed rather than kept as a
reference the rebuild is not allowed to use.

What P3 builds, and the requirement each part answers:

| Part | Requirement |
|---|---|
| The unit, root with `CAP_NET_ADMIN` only, filesystem read-only except `/etc/wireguard/` and the state directory | `REQ-SEC-031`, `REQ-SEC-086`, `REQ-CFG-043` |
| `/etc/default/wg-agent`, every key commented at its default, as the one conffile | `REQ-CFG-038`, `REQ-CFG-023` |
| `postinst` generating the token, leaving the listener on loopback | `REQ-CFG-045`, `REQ-CFG-022` |
| Dependencies on `wireguard-tools` and `libc6 (>= 2.34)` | `REQ-CFG-021` |
| Maintainer scripts that never remove a WireGuard link | `REQ-CFG-025` to `REQ-CFG-028` |

The install script and the release pipeline wait under `B-03` in the
[backlog](../docs/60-planning/backlog.md).
