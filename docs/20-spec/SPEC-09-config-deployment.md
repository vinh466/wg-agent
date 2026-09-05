---
id: SPEC-09
title: Configuration, packaging and deployment
prefix: CFG
status: Accepted
version: 1.6
owner: Vinh Nguyen
created: 2026-08-03
updated: 2026-09-06
depends_on: [SPEC-05]
adrs: [ADR-0002, ADR-0009]
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

> **REQ-CFG-037** — The agent MUST read its configuration from a YAML file whose path
> defaults to `/etc/wg-agent/config.yaml`.

> **REQ-CFG-039** — A `--config` argument MUST override that path.

> **REQ-CFG-040** — An absent configuration file MUST leave every key at its default rather
> than failing to start.

> **REQ-CFG-001** — Every configuration key MUST be overridable by an environment variable
> following the pattern `WG_AGENT_<PATH>`.

> **REQ-CFG-041** — `<PATH>` MUST be the dotted key path uppercased, with every character
> outside `A-Z` and `0-9` replaced by an underscore.

`REQ-CFG-041` makes the pattern of `REQ-CFG-001` decidable: `server.http.address` becomes
`WG_AGENT_SERVER_HTTP_ADDRESS`. Without the transformation two implementations could disagree
about a variable an operator has already set, which is the kind of difference that surfaces only
in production.

`REQ-CFG-040` matters because of `REQ-CFG-037`: a node reaching the store through the CLI has
no configuration file yet, and refusing to start without one would make the first token
impossible to issue.

> **REQ-CFG-002** — The agent MUST refuse to start on encountering an unrecognized
> configuration key rather than ignoring it silently.

> **REQ-CFG-004** — The systemd unit MUST load `/etc/default/wg-agent` through
> `EnvironmentFile`.

> **REQ-CFG-005** — Loading `/etc/default/wg-agent` MUST tolerate the file being absent.

`REQ-CFG-004` gives the deployment the shape an operator expects from a Debian service: the
YAML file holds structure, and `/etc/default/wg-agent` holds the per-host overrides that
`REQ-CFG-001` already exposes as `WG_AGENT_<PATH>` variables. The port below picks 9585 to sit
beside the metrics listener on 9586 rather than contend for 8080.

`node.endpoint` is empty by default because no value the agent could choose would be right. The
address a client reaches a node at depends on NAT and on which of several addresses is routable
from where the client sits, so `REQ-KEY-038` refuses to guess and asks the caller instead.

```yaml
node:
  endpoint: ""                       # host:port clients reach this node at — REQ-KEY-037

server:
  unix_socket: /run/wg-agent/wg-agent.sock
  socket_mode: "0660"
  socket_group: wg-agent
  http:
    enabled: false                   # the install script turns this on — REQ-CFG-031
    address: "127.0.0.1:9585"        # loopback only — REQ-SEC-070

security:
  allow_server_generated_keys: true
  token_file: /etc/wg-agent/tokens.yaml   # mode 0600 — REQ-SEC-074

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
  address: "127.0.0.1:9586"        # loopback only — REQ-SEC-083
  per_peer: true

audit:
  enabled: true
  path: /var/log/wg-agent/audit.jsonl

log:
  level: info
  format: json
```

### 2.1. Token file

> **REQ-CFG-003** — The token file MUST map each token value to exactly one role and one
> label.

```yaml
tokens:
  - token: <opaque string>
    role: admin
    label: vpn-controller
  - token: <opaque string>
    role: reader
    label: monitoring
```

`label` supplies the principal recorded in the audit log under
[SPEC-08](SPEC-08-observability.md). The token value itself is sensitive data under
`REQ-SEC-076` and never appears in a record.

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
EnvironmentFile=-/etc/default/wg-agent
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
for `/proc/sys` is inconsistent across systemd versions, so the combination is verified rather
than assumed.

> **REQ-CFG-013** — The test suite MUST verify the `REQ-CFG-011` combination against every
> systemd version shipped by the distributions in section 3.

The verification runs the packaged unit under a container whose PID 1 is the target
distribution's own systemd, asserting that the agent writes
`net.ipv4.conf.<iface>.forwarding` while the rest of `/proc/sys` stays read-only. A container
reproduces the systemd version and the mount-namespace behavior that decide the outcome, which
makes it a sufficient gate; the writes land in the container's own network namespace and
cannot disturb the host.

## 5. Packaging

> **REQ-CFG-020** — The binary MUST be built statically with `CGO_ENABLED=0` for `amd64` and
> `arm64`.

> **REQ-CFG-021** — The `.deb` package MUST include the systemd unit, a `sysusers.d` entry
> creating the account, a `tmpfiles.d` entry creating the runtime directory, a logrotate
> configuration for the audit log, and a sample configuration file.

> **REQ-CFG-038** — The `.deb` package MUST ship an `/etc/default/wg-agent` file containing
> only commented examples.

> **REQ-CFG-022** — The `postinst` script MUST NOT enable the HTTP listener.

The post-installation default is the unix socket alone. Enabling the HTTP listener is an
explicit operator action, which is what the install script in section 6 performs. Installing
the package on its own leaves the agent reachable only over the socket.

### 5.1. Conffiles

> **REQ-CFG-023** — The package MUST declare `/etc/wg-agent/config.yaml` and
> `/etc/default/wg-agent` as conffiles.

> **REQ-CFG-024** — The package MUST NOT declare the token file as a conffile.

Without `REQ-CFG-023` an upgrade overwrites operator edits. `REQ-CFG-024` is the counterpart:
the token file is generated rather than authored, so a conffile prompt on every upgrade would
be noise, and dpkg comparing its contents would be meaningless.

### 5.2. Maintainer scripts

> **REQ-CFG-025** — `prerm` MUST stop the service before any file is removed.

> **REQ-CFG-026** — `postrm purge` MUST remove the configuration, the token file and the
> store.

> **REQ-CFG-027** — Package removal MUST NOT delete any WireGuard link, at any removal level.

> **REQ-CFG-028** — `postrm purge` MUST print the names of the WireGuard links left behind.

`REQ-CFG-027` follows the rule the agent applies to itself: `REQ-RCN-030` forbids touching a
link it did not create and `REQ-RCN-035` forbids removing an orphan on its own. Removing a
control plane is not a reason to drop live tunnels. `REQ-CFG-028` keeps that from becoming a
silent leak — an operator learns what remains as it becomes theirs to handle.
Reasoning in [ADR-0010](../10-decisions/ADR-0010-install-script-over-released-deb.md).

## 6. Install script

> **REQ-CFG-029** — The repository MUST publish an install script supporting the `install`,
> `update` and `uninstall` subcommands.

> **REQ-CFG-030** — The install script MUST verify the checksum of a downloaded artifact
> before installing it.

> **REQ-CFG-031** — On `install`, the script MUST write a configuration enabling the HTTP
> listener on a loopback address.

> **REQ-CFG-032** — On `install`, the script MUST generate one `admin` token when no token
> file exists.

> **REQ-CFG-033** — On completion, the script MUST print the listener address together with
> any token generated during that run.

> **REQ-CFG-034** — On `uninstall`, the script MUST leave WireGuard links in place by default.

> **REQ-CFG-035** — The script MUST accept `uninstall --remove-links`, which deletes the
> WireGuard links the agent manages.

> **REQ-CFG-036** — The release pipeline MUST publish a `.deb` and its checksum for `amd64`
> and `arm64`.

The script fetches the package matching the host architecture, verifies it against
`REQ-CFG-030`, and hands it to the package manager. `update` repeats that sequence, so
`apt upgrade` alone does not reach the agent — the trade accepted in
[ADR-0010](../10-decisions/ADR-0010-install-script-over-released-deb.md).

Token issuance sits with the script rather than with `postinst` so that generating a value and
displaying it are one action, which is what `REQ-CLI-011` and `REQ-CLI-012` in
[SPEC-12](SPEC-12-cli.md) require: a token is shown once, as it is created, and never again.
`REQ-CFG-033` therefore prints a token only on a run that created one. Re-running
`install` on a node that already has a token reports the address alone and leaves the
credential untouched, and `wg-agent token regen` is the way to replace a lost one.

A package installed on its own, without the script, has no token and no HTTP listener under
`REQ-CFG-022`. That agent is reachable over the unix socket, which grants `admin` through
`REQ-SEC-077`, so it is fully usable — the script adds the loopback API rather than enabling
basic operation.
