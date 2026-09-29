---
id: packaging
title: Packaging assets
status: partial
owner: Vinh Nguyen
created: 2026-09-07
updated: 2026-09-29
---

# Packaging

What is here, and what is not.

## Present

| File | Requirement |
|---|---|
| `systemd/wg-agent.service` | `REQ-CFG-010`, `REQ-CFG-004`, `REQ-CFG-005`, `REQ-CFG-011` |
| `systemd/wg-agent.sysusers` | The account the unit runs as, part of `REQ-CFG-021` |
| `systemd/wg-agent.tmpfiles` | The runtime and state directories, part of `REQ-CFG-021` |

No agent binary exists while the implementation is rebuilt from the specification. Once it
does, install by hand until the package exists:

```bash
sudo install -m 0755 wg-agent /usr/bin/wg-agent
sudo install -m 0644 packaging/systemd/wg-agent.sysusers /usr/lib/sysusers.d/wg-agent.conf
sudo install -m 0644 packaging/systemd/wg-agent.tmpfiles /usr/lib/tmpfiles.d/wg-agent.conf
sudo install -m 0644 packaging/systemd/wg-agent.service /etc/systemd/system/wg-agent.service
sudo systemd-sysusers && sudo systemd-tmpfiles --create
sudo systemctl daemon-reload && sudo systemctl enable --now wg-agent
```

The agent refuses to start rather than run half-configured. `systemctl status wg-agent`
carries the reason code of whichever check of `REQ-API-050` failed, so a failure names its own
cause. The one to expect first is `SYSCTL_WRITE_DENIED`, which means the `ReadWritePaths`
carve-out of `REQ-CFG-011` did not take effect on this systemd version — `REQ-CFG-012` permits
`ProtectKernelTunables=no` as the fallback, and that is a hardening trade to make knowingly.

## Not present

Deferred under `B-03` in [the backlog](../docs/60-planning/backlog.md): the `.deb` itself
(`REQ-CFG-021`, `REQ-CFG-023` to `REQ-CFG-028`), `/etc/default/wg-agent` as a shipped file
(`REQ-CFG-038`), the install script (`REQ-CFG-029` to `REQ-CFG-035`), the release pipeline
(`REQ-CFG-036`), and the logrotate configuration, whose audit log is deferred under `B-01`.

`REQ-CFG-013` requires the `REQ-CFG-011` combination to be verified against every systemd
version the tested distributions ship. That needs `systemd` as PID 1, which Docker does not
provide, so it stays with `B-03`.

A check that the unit file carries the directives `REQ-CFG-010`, `REQ-CFG-011` and `REQ-CFG-005`
name is part of the rebuild's test suite. Until it exists, nothing asserts that an edit to
`wg-agent.service` kept them.
