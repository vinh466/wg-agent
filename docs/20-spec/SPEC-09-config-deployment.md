---
id: SPEC-09
title: Configuration, packaging and deployment
prefix: CFG
status: Accepted
version: 2.0
owner: Vinh Nguyen
created: 2026-08-03
updated: 2026-09-30
depends_on: [SPEC-05]
adrs: [ADR-0010, ADR-0013, ADR-0015]
milestone: P1–P3
---

# SPEC-09: Configuration, packaging and deployment

## 1. Scope

Configuration keys, system requirements, the systemd unit, packaging.

**Not in this module:**
- Default values of resource fields → [SPEC-01](SPEC-01-resource-model.md), [SPEC-02](SPEC-02-forward-policy.md)
- Security semantics of the options → [SPEC-05](SPEC-05-security.md)
- Upgrade and migration → [SPEC-10](SPEC-10-lifecycle.md)

## 2. Configuration file

> **REQ-CFG-037** — The agent MUST read its configuration from a file of `KEY=VALUE` lines whose
> path defaults to `/etc/default/wg-agent`.

> **REQ-CFG-039** — A `--config` argument MUST override that path.

> **REQ-CFG-040** — An absent configuration file MUST leave every key at its default rather
> than failing to start.

> **REQ-CFG-001** — Every configuration key MUST be overridable by an environment variable
> following the pattern `WG_AGENT_<PATH>`.

> **REQ-CFG-041** — `<PATH>` MUST be the dotted key path uppercased, with every character
> outside `A-Z` and `0-9` replaced by an underscore.

`REQ-CFG-041` makes the pattern of `REQ-CFG-001` decidable: `listen.address` becomes
`WG_AGENT_LISTEN_ADDRESS`. The file uses the same names, so a line copied from the file into the
environment means the same thing, and one parser serves the agent and the CLI alike. Every key
is a scalar, which is what lets one line per key carry the whole configuration without a
structured format.

`REQ-CFG-040` matters because the CLI reads the same file: a node reaching the store through the
CLI before anything is configured still works on defaults.

> **REQ-CFG-002** — The agent MUST refuse to start on encountering an unrecognized
> configuration key rather than ignoring it silently.

> **REQ-CFG-042** — A command-line flag naming a configuration key MUST take precedence over
> both the file and the environment variable of `REQ-CFG-001`.

`REQ-CFG-042` completes the precedence order, which is otherwise undecidable where all three
supply a value: defaults, then the file, then the environment, then a flag. A flag is the layer
an operator reaches for once, to answer a question about the running node, and one that
silently lost to a file in `/etc` would send them looking for a bug in the agent.

`node.endpoint` is empty by default because no value the agent could choose would be right. The
host a client reaches a node at depends on NAT and on which of several addresses is routable
from where the client sits, so `REQ-KEY-038` refuses to guess and asks the caller instead.

```sh
# /etc/default/wg-agent — every key, at its default.

# Host clients reach this node at; the port is each interface's own — REQ-KEY-037.
#WG_AGENT_NODE_ENDPOINT=

# Loopback unless set; any other address belongs on a private network — REQ-SEC-084.
#WG_AGENT_LISTEN_ADDRESS=127.0.0.1:9585

# One token, mode 0600, owned by root — REQ-SEC-074, REQ-SEC-082.
#WG_AGENT_TOKEN_FILE=/etc/wg-agent/token

#WG_AGENT_STATE_PATH=/var/lib/wg-agent/state.json
#WG_AGENT_APPLY_TIMEOUT=10s
#WG_AGENT_PEER_ONLINE_THRESHOLD=180s
#WG_AGENT_LOG_LEVEL=info
```

### 2.1. Token file

> **REQ-CFG-003** — The token file MUST hold exactly one token.

The file holds the single shared secret of
[ADR-0015](../10-decisions/ADR-0015-network-listener-with-a-shared-secret.md) on one line. The
token value is sensitive data under `REQ-SEC-076` and never appears in a record.

## 3. System requirements

| Requirement | Value |
|---|---|
| Distribution | Debian 13 and later, Ubuntu 24.04 LTS and later — stable and LTS releases |
| Kernel | As the distribution ships it; WireGuard is in tree on every one |
| Packages | `wireguard-tools`, `systemd` |
| Account | root, confined by the unit — `REQ-SEC-086` |

## 4. systemd unit

> **REQ-CFG-043** — The systemd unit MUST make the filesystem read-only except `/etc/wireguard/`
> and the agent's state directory.

```ini
[Service]
ExecStart=/usr/bin/wg-agent serve
CapabilityBoundingSet=CAP_NET_ADMIN
NoNewPrivileges=yes
ProtectSystem=strict
ReadWritePaths=/etc/wireguard
StateDirectory=wg-agent
StateDirectoryMode=0700
ProtectHome=yes
PrivateTmp=yes
PrivateDevices=yes
ProtectKernelTunables=yes
ProtectKernelModules=yes
ProtectControlGroups=yes
RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6 AF_NETLINK
RestrictNamespaces=yes
LockPersonality=yes
MemoryDenyWriteExecute=yes
SystemCallArchitectures=native
```

The agent starts `wg` itself and asks systemd, over its bus, to start and stop `wg-quick@`
units; `wg-quick` then runs in its own unit, outside this sandbox. That is why nothing beyond
`/etc/wireguard/` and the store needs to be writable here, and why `ProtectKernelModules` costs
nothing — the kernel loads WireGuard for the `wg-quick@` unit, not for the agent.

### 4.1. The ProtectKernelTunables trade-off

The agent writes no sysctl until [SPEC-02](SPEC-02-forward-policy.md) is delivered, so the unit
above sets `ProtectKernelTunables=yes` with no carve-out. The requirements below return with the
forwarding sysctl of `REQ-FWD-020`; on systemd 255, 257 and 259 the combination they describe was
measured to work.

> **REQ-CFG-011** — The package SHOULD combine `ProtectKernelTunables=yes` with
> `ReadWritePaths=/proc/sys/net/ipv4/conf`, opening only the required subtree.

> **REQ-CFG-012** — Where that combination does not work on a given systemd version, the
> package MAY fall back to `ProtectKernelTunables=no`.

> **REQ-CFG-013** — The test suite MUST verify the `REQ-CFG-011` combination against every
> systemd version shipped by the distributions in section 3.

The verification runs the packaged unit under a container whose PID 1 is the target
distribution's own systemd, asserting that the agent writes
`net.ipv4.conf.<iface>.forwarding` while the rest of `/proc/sys` stays read-only. A container
reproduces the systemd version and the mount-namespace behavior that decide the outcome, which
makes it a sufficient gate; the writes land in the container's own network namespace and
cannot disturb the host.

## 5. Packaging

> **REQ-CFG-044** — The binary MUST be published with NativeAOT for `linux-x64` against glibc.

> **REQ-CFG-021** — The `.deb` package MUST include the systemd unit and the configuration file,
> and depend on `wireguard-tools` and `libc6 (>= 2.34)`.

The glibc floor is the one the NativeAOT binary was measured to need; every supported
distribution ships a newer one.

> **REQ-CFG-038** — The `.deb` package MUST ship `/etc/default/wg-agent` with every key commented
> out at its default.

> **REQ-CFG-022** — The `postinst` script MUST NOT set a listener address other than loopback.

> **REQ-CFG-045** — The `postinst` script MUST generate the token when the token file is absent.

The agent refuses to start without a token under `REQ-SEC-072`, so the package provides one. An
operator reads it as root, or replaces it with the command of `REQ-CLI-011`, which prints the
value it generates. Opening the listener to a private network is the operator's decision under
ADR-0015, which is why `postinst` leaves the address on loopback.

### 5.1. Conffiles

> **REQ-CFG-023** — The package MUST declare `/etc/default/wg-agent` as a conffile.

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
control plane is not a reason to drop live tunnels. Under ADR-0013 the interfaces the agent
created keep running under their `wg-quick@` units, and their files stay in `/etc/wireguard/` as
the operator's. `REQ-CFG-028` keeps that from becoming a silent leak — an operator learns what
remains as it becomes theirs to handle.
Reasoning in [ADR-0010](../10-decisions/ADR-0010-install-script-over-released-deb.md).

## 6. Install script

The install script and the release pipeline are delivered later; the
[backlog](../60-planning/backlog.md) holds them. Their requirements are re-read against
`REQ-CFG-045` when they return, since the package now generates the token itself.

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

A package installed on its own, without the script, generates its token under `REQ-CFG-045` and
serves on loopback, so it is fully usable — the script adds the download and the update path
rather than enabling basic operation.

## 7. Removed requirements

Removed in v2.0 by [ADR-0013](../10-decisions/ADR-0013-drive-wg-and-wg-quick.md) and the move of
the configuration into one `KEY=VALUE` file:

~~**REQ-CFG-004**~~ — `/etc/default/wg-agent` loaded through `EnvironmentFile`. The agent reads
the file itself under `REQ-CFG-037`; loading it through systemd as well would parse one file
under two sets of rules.

~~**REQ-CFG-005**~~ — Tolerance of an absent `/etc/default/wg-agent` in the unit. `REQ-CFG-040`
covers the agent's own read.

~~**REQ-CFG-010**~~ — A dedicated account with `AmbientCapabilities=CAP_NET_ADMIN`. The agent
runs as root under `REQ-SEC-086`, confined by `REQ-CFG-043`.

~~**REQ-CFG-020**~~ — A static build with `CGO_ENABLED=0` for `amd64` and `arm64`. Go build
flags describe a removed implementation; `REQ-CFG-044` states the build.
