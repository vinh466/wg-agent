---
id: ADR-0013
title: Drive WireGuard through wg and wg-quick in v1
status: Proposed
owner: Vinh Nguyen
created: 2026-09-29
updated: 2026-09-29
supersedes: [ADR-0012]
superseded_by: null
affects: [SPEC-01, SPEC-02, SPEC-03, SPEC-05, SPEC-06, SPEC-09, SPEC-12]
---

# ADR-0013: Drive WireGuard through wg and wg-quick in v1

## Context

The operator runs WireGuard on each node by hand: one `wg-quick` configuration per interface,
edited and reloaded manually. The need v1 answers is narrower than the specification grew to
cover — a CLI and an API on the node that do what the operator does by hand, so that it stops
being manual. Forward policy, NAT, adoption, diagnostics, audit, metrics and backup stay
specified and move to the backlog.

[ADR-0012](ADR-0012-managed-netlink-in-dotnet.md) rebuilds through netlink the part of
`wg-quick` the agent needs, carrying forward the objection
[ADR-0002](ADR-0002-netlink-over-wg-quick.md) raised against driving the tools: that adding
one peer means tearing the interface down. The objection does not hold. Measured on Debian 13
with wireguard-tools 1.0.20210914, two network namespaces joined by a veth pair, a client
pinging the server every 100 ms:

| Observation | Result |
|---|---|
| `wg syncconf` adding a peer, the configuration fed on stdin | 60 of 60 pings answered; the existing peer's handshake and learned endpoint unchanged |
| `wg syncconf` removing that peer | applied; the other peer untouched |
| Routes for a new peer's allowed IPs outside the interface's subnet | not installed — `wg-quick` adds routes at `up` only |
| `wg-quick` started by a non-root account | refused: it re-executes itself through `sudo` |
| `wg` started by a non-root account holding `CAP_NET_ADMIN` | works |
| AppArmor on Ubuntu 26.04 | profiles for `wg` and `wg-quick` confine both to `/etc/wireguard/`; Debian 13 and Ubuntu 24.04 ship none |

## Alternatives considered

### A — Managed netlink, as ADR-0012
- For: field-level precision; typed errors; no child process
- Against: rebuilds what the tools already do, with the reconcile machinery around it — the
  largest build for the least gain an operator sees in v1

### B — Drive `wg` and `wg-quick`
- For: the files and units the operator already uses; systemd restores interfaces at boot
  without the agent; the smallest build
- Against: child processes; text output to parse; root; an interface-level change restarts
  the interface

### C — `wg-quick` for the lifecycle, netlink for peers
- For: peer changes without a child process
- Against: two mechanisms writing one device, each able to undo the other

## Decision

Adopt **B**.

- An interface the agent creates is a file `/etc/wireguard/<name>.conf` and the systemd unit
  `wg-quick@<name>`. The agent renders the file, then enables and starts the unit; systemd
  brings the interface up at boot whether or not the agent runs. The directory is fixed by
  `wg-quick@` and, on Ubuntu 26.04, by AppArmor.
- The agent manages only the interfaces it created, recorded in its own store. It never reads,
  edits or deletes any other file in `/etc/wireguard/`.
- A peer change is applied with `wg syncconf <name> /dev/stdin`, fed a configuration the agent
  renders without the keys only `wg-quick` understands. Other peers keep their sessions.
- A change `wg syncconf` cannot apply — an address, the MTU — restarts the unit, and the
  response says that the interface's sessions were dropped.
- State is read from `wg show <name> dump`, whose tab-separated form exists for scripts. Keys
  come from `wg genkey`, `wg pubkey` and `wg genpsk`. A secret travels over stdin or stdout,
  never in argv.
- The binaries are a fixed set — `wg` and `systemctl` — started with fixed argv and never
  through a shell. No rendered file carries `PreUp`, `PostUp`, `PreDown`, `PostDown`, `DNS` or
  `SaveConfig`, so [ADR-0007](ADR-0007-no-shell-hooks.md) holds.
- The agent runs as root: `/etc/wireguard/` is root's `0700` directory, and starting a unit
  needs root or a polkit grant. The agent's systemd unit confines it instead — capability
  bounding set `CAP_NET_ADMIN`, the filesystem read-only outside `/etc/wireguard/` and the
  agent's own directories.

## Consequences

### Positive
- v1 does what the operator does by hand, through the same files and units; an interface keeps
  working if the agent is stopped or removed
- No netlink layer and no crypto dependency — BouncyCastle leaves with ADR-0012
- Restore after reboot belongs to systemd rather than to a reconcile pass

### Negative — the price paid
- `REQ-SEC-041` is amended from "no child process" to a fixed set of binaries with fixed argv
- The agent runs as root, which `REQ-SEC-030` and `REQ-SEC-062` forbid; both are amended, and
  the unit's confinement carries the weight the account did
- Drift is not corrected: a hand edit to an agent file, or a `wg set`, persists until the agent
  next writes that interface
- An address or MTU change drops the sessions on that interface
- A peer whose allowed IPs leave the interface's subnets gets no route from `wg syncconf`; the
  specification decides whether such a peer restarts the unit or is refused
- Errors arrive as an exit status and text rather than an errno

### Follow-on work
- Amend `REQ-SEC-030`, `REQ-SEC-041` and `REQ-SEC-062`; replace the reconcile of SPEC-03 with
  apply-on-write; move SPEC-02, SPEC-08, SPEC-10, SPEC-11 and adoption to the backlog; cut
  SPEC-12 to interface and peer commands
- Mark ADR-0012 superseded when this ADR is accepted

## Conditions for revisiting

- A requirement the tools cannot express returning from the backlog — forward policy, NAT,
  drift correction. Managed netlink under ADR-0012, and libnftables under F-01 of the
  [specification audit](../60-planning/spec-audit.md), are the recorded routes.
- Restarts on interface changes becoming unacceptable for a deployment.
- `wireguard-tools` changing the form of `wg show dump`, or AppArmor policy moving the
  configuration directory.
