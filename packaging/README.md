---
id: packaging
title: Packaging assets
status: active
owner: Vinh Nguyen
created: 2026-09-07
updated: 2026-10-01
---

# Packaging

The `.deb`, its systemd unit, and the install script of phase P3, built from section 4 and
section 5 of [SPEC-09](../docs/20-spec/SPEC-09-config-deployment.md).

| File | Purpose |
|---|---|
| `systemd/wg-agent.service` | The hardened unit — `REQ-SEC-031`, `REQ-SEC-086`, `REQ-CFG-043` |
| `default/wg-agent` | `/etc/default/wg-agent`, the one conffile — `REQ-CFG-038`, `REQ-CFG-023` |
| `debian/control`, `conffiles` | Package metadata and the conffile list — `REQ-CFG-021` |
| `debian/postinst` | Generates the token, leaves the listener on loopback — `REQ-CFG-045`, `REQ-CFG-022` |
| `debian/prerm`, `postrm` | Stop before removal; purge config/token/store; never a link — `REQ-CFG-025` to `REQ-CFG-028` |
| `build-deb.sh` | Builds the `.deb` from the NativeAOT binary — `REQ-CFG-021`, `REQ-CFG-051` |
| `install.sh` | Install, update, uninstall from the release — `REQ-CFG-052`, `REQ-CFG-053` |

The release pipeline (`.github/workflows/release.yml`) builds the `.deb` and a `SHA256SUMS` on a
version tag and publishes them as a GitHub Release — [ADR-0019](../docs/10-decisions/ADR-0019-install-script-over-released-deb.md).

The unit, sysusers and tmpfiles files that sat here were written alongside the removed Go
implementation, for a non-root account and a forwarding-sysctl carve-out. Under
[ADR-0013](../docs/10-decisions/ADR-0013-drive-wg-and-wg-quick.md) the agent runs as root,
confined by its unit, and writes no sysctl, so the files were removed rather than kept as a
reference the rebuild is not allowed to use.

