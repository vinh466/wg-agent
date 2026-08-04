---
id: SPEC-09
title: Configuration, packaging and deployment
prefix: CFG
status: Accepted
version: 1.1
owner: Vinh Nguyen
created: 2026-08-03
updated: 2026-08-04
depends_on: [SPEC-05]
adrs: [ADR-0002]
milestone: M2
---

# SPEC-09: Configuration, packaging and deployment

## 1. Scope

Configuration keys, system requirements, the systemd unit, packaging.

**Not in this module:**
- Default values of resource fields → [SPEC-01](SPEC-01-resource-model.md), [SPEC-02](SPEC-02-forward-policy.md)
- Security semantics of the options → [SPEC-05](SPEC-05-security.md)
- Upgrade and migration → [SPEC-10](SPEC-10-lifecycle.md)

## 2. Configuration file

> **REQ-CFG-001** — Every configuration key MUST be overridable by an environment variable
> following the pattern `WG_AGENT_<PATH>`.

> **REQ-CFG-002** — The agent MUST refuse to start on encountering an unrecognized
> configuration key rather than ignoring it silently.

```yaml
server:
  unix_socket: /run/wg-agent/wg-agent.sock
  socket_mode: "0660"
  socket_group: wg-agent
  tcp:
    enabled: false
    address: "127.0.0.1:8443"
    tls:
      cert_file: /etc/wg-agent/tls/server.crt
      key_file:  /etc/wg-agent/tls/server.key
      client_ca_file: /etc/wg-agent/tls/client-ca.crt
      min_version: "1.3"

security:
  allow_server_generated_keys: true
  allowed_client_identities: []      # empty = any certificate signed by the trusted CA
  roles:
    "spiffe://corp/svc/vpn-controller": admin
    "spiffe://corp/svc/monitoring":     reader

state:
  path: /var/lib/wg-agent/state.db

reconcile:
  interval: 30s
  apply_timeout: 10s
  backoff_min: 1s
  backoff_max: 60s

runtime:
  peer_online_threshold: 180s

# Defaults applied to new interfaces when the caller omits them.
# These values are the flat-LAN-per-group pattern — see SPEC-02.
defaults:
  forward_policy:
    intra_interface: ALLOW
    inter_interface: DENY
    external: DENY
  mtu: 1420

metrics:
  enabled: true
  address: "127.0.0.1:9586"
  per_peer: true

audit:
  enabled: true
  path: /var/log/wg-agent/audit.jsonl

log:
  level: info
  format: json
```

## 3. System requirements

The real constraint is the kernel, not the distribution.

| Requirement | Value |
|---|---|
| Kernel | 5.6 or later for in-tree WireGuard, or older with `wireguard-dkms` |
| Capability | `CAP_NET_ADMIN` |
| nftables | Required when NAT is enabled or any axis is set to `DENY` |
| Tested distributions | Debian 11/12/13, Ubuntu 20.04/22.04/24.04 |

## 4. systemd unit

> **REQ-CFG-010** — The systemd unit MUST run the agent under a dedicated account with
> `AmbientCapabilities=CAP_NET_ADMIN`.

```ini
[Service]
User=wg-agent
Group=wg-agent
AmbientCapabilities=CAP_NET_ADMIN
CapabilityBoundingSet=CAP_NET_ADMIN
NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=yes
PrivateTmp=yes
PrivateDevices=yes
ProtectKernelTunables=yes
ProtectControlGroups=yes
RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6 AF_NETLINK
RestrictNamespaces=yes
LockPersonality=yes
MemoryDenyWriteExecute=yes
SystemCallArchitectures=native
ReadWritePaths=/var/lib/wg-agent /run/wg-agent /proc/sys/net/ipv4/conf
```

### 4.1. The ProtectKernelTunables trade-off

`ProtectKernelTunables=yes` mounts `/proc/sys` read-only, while the agent must write
`net.ipv4.conf.<iface>.forwarding` per `REQ-FWD-020`. With the default
`intra_interface = ALLOW`, that write is the ordinary path rather than an exception.

> **REQ-CFG-011** — The package SHOULD combine `ProtectKernelTunables=yes` with
> `ReadWritePaths=/proc/sys/net/ipv4/conf`, opening only the required subtree.

> **REQ-CFG-012** — Where that combination does not work on a given systemd version, the
> package MAY fall back to `ProtectKernelTunables=no`.

Documentation states plainly that the fallback is a hardening trade-off. Carve-out behavior
for `/proc/sys` is inconsistent across systemd versions and requires verification on every
target distribution during M2 — a real risk rather than a formality. See
[open questions](../60-planning/open-questions.md), OQ-05.

## 5. Packaging

> **REQ-CFG-020** — The binary MUST be built statically with `CGO_ENABLED=0` for `amd64` and
> `arm64`.

> **REQ-CFG-021** — The `.deb` package MUST include the systemd unit, a `sysusers.d` entry
> creating the account, a `tmpfiles.d` entry creating the runtime directory, a logrotate
> configuration for the audit log, and a sample configuration file.

> **REQ-CFG-022** — The `postinst` script MUST NOT enable the TCP listener.

The post-installation default is the unix socket.
