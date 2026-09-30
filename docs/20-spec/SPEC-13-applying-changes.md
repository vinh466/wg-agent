---
id: SPEC-13
title: Applying a change through wg and wg-quick
prefix: APL
status: Accepted
version: 1.0
owner: Vinh Nguyen
created: 2026-09-30
updated: 2026-09-30
depends_on: [SPEC-01, SPEC-03]
adrs: [ADR-0007, ADR-0013, ADR-0017]
milestone: P1
---

# SPEC-13: Applying a change through wg and wg-quick

## 1. Scope

How a change the agent has accepted reaches WireGuard: the files the agent writes, the systemd
units it drives, when a change synchronises the running device and when it restarts it, and what
a failure leaves behind. [ADR-0013](../10-decisions/ADR-0013-drive-wg-and-wg-quick.md) fixes the
mechanism; this module states what it must achieve.

**Not in this module:**
- What the store holds, and the lock every writer takes → [SPEC-03](SPEC-03-state-reconcile.md)
- Which programs the agent may start, and how → [SPEC-05](SPEC-05-security.md)
- The request and response of a write → [SPEC-04](SPEC-04-api-conventions.md)
- Field meanings and defaults → [SPEC-01](SPEC-01-resource-model.md)

## 2. Files and units

> **REQ-APL-001** — Each interface the agent creates MUST be defined by the file
> `/etc/wireguard/<name>.conf` and run by the systemd unit `wg-quick@<name>`.

> **REQ-APL-002** — The agent MUST NOT read, write or delete a file under `/etc/wireguard/` that
> it did not create.

The directory is not a choice: `wg-quick@` reads its configuration there, and on Ubuntu 26.04
AppArmor confines `wg` and `wg-quick` to it. Files the operator keeps beside the agent's own are
the operator's, as the links of `REQ-RCN-030` are.

> **REQ-APL-009** — A configuration file the agent writes MUST be owned by root with mode `0600`,
> and replaced atomically.

The file holds the interface's private key and every preshared key, as the store does under
`REQ-RCN-004`. An atomic replacement — a temporary file in the same directory, renamed over the
old one — is what lets `wg-quick` never read half a file, and what leaves the previous version
intact for `REQ-APL-008` to restore.

> **REQ-APL-003** — A configuration file the agent renders MUST carry only the keys
> `PrivateKey`, `ListenPort`, `Address`, `MTU`, `PostUp` and `PostDown` under `[Interface]`, and
> `PublicKey`, `PresharedKey`, `AllowedIPs`, `Endpoint` and `PersistentKeepalive` under `[Peer]`.

The closed list is what keeps [ADR-0007](../10-decisions/ADR-0007-no-shell-hooks.md) true of a file
the agent writes. `PostUp` and `PostDown` carry only the commands the operator set through the CLI
(`REQ-CLI-024`), which no API caller can read or change —
[ADR-0017](../10-decisions/ADR-0017-operator-hooks-through-the-cli.md). `PreUp` and `PreDown` stay
out; `SaveConfig` would let `wg-quick` rewrite a file the agent owns; `DNS` and `Table` belong to
client concerns and to fields deferred with [SPEC-02](SPEC-02-forward-policy.md). Labels stay in
the store.

Every interface file carries `ListenPort`, taken from the spec or from the default of
[SPEC-01](SPEC-01-resource-model.md): a file without it lets the kernel choose a new port each
time `wg-quick` brings the interface up.

> **REQ-APL-004** — An interface's unit MUST be enabled and active exactly when the interface's
> `enabled` is true.

systemd starts an enabled unit at boot whether or not the agent runs, which is what restores an
interface after a reboot — the work a reconcile loop would otherwise do at startup. A disabled
unit leaves the file in place, so enabling the interface again needs only the unit.

## 3. Applying a change

> **REQ-APL-005** — A change to a peer MUST leave the sessions of the interface's other peers
> undisturbed, unless `REQ-APL-006` applies.

`wg syncconf`, fed the rendered configuration without the keys only `wg-quick` understands,
applies the difference between that configuration and the running device and nothing else.
Measured under ADR-0013, a peer added and then removed while another peer pinged every 100 ms
cost no packet, and the other peer kept its handshake and its learned endpoint. An `Endpoint` a
file carries is written again at each synchronisation, so a peer configured with one returns to
it — the case of a site-to-site peer, whose address is static.

> **REQ-APL-006** — On an enabled interface, a change to its `addresses` or `mtu`, or the
> addition, change or removal of a peer holding an `allowed_ips` entry outside the interface's
> subnets, MUST restart the interface's unit.

`wg syncconf` configures WireGuard alone. Addresses, the MTU and the routes for allowed IPs are
set by `wg-quick` when the interface comes up, and a peer routed outside the interface's subnets
has a route only after `up`, and keeps it until `down`. A change to `listen_port` or `private_key`
synchronises without a restart, although a new private key ends every session by its nature. On
a disabled interface a change rewrites the file alone; the unit applies it when enabled.

> **REQ-APL-007** — The response to a write that restarted an interface's unit MUST state that
> the interface's sessions were interrupted.

## 4. Failure

> **REQ-APL-008** — When applying a change fails, the agent MUST restore the interface's previous
> configuration file and running state, leave the store unchanged, and return the failure with
> reason `APPLY_FAILED`.

A write is applied before the response under `REQ-API-020`, so the caller learns the outcome in
the response and no resource is left half-applied for a later pass to finish. The store is written
last, once the change has taken effect, so it never describes a configuration that failed. The
restoration is itself a synchronisation or a restart; should it fail as well, the same response
says so.

## 5. Limits accepted

The agent corrects no drift, and each write replaces the whole file. A hand edit to a file the
agent created, or a `wg set` against one of its interfaces, persists until the agent next writes
that interface, and is then lost — by the operator's decision of 2026-09-30, the agent neither
detects nor preserves it. A change the agent should keep belongs in the spec.
Continuous reconciliation is specified in [SPEC-03](SPEC-03-state-reconcile.md) sections 3 to 5
and delivered later.

Forwarding between peers and NAT towards other networks are properties of the host. The agent
manages neither until [SPEC-02](SPEC-02-forward-policy.md) is delivered. The operator sets them
once on the host, or for one interface through its `post_up` and `post_down` commands — the way a
hand-kept `wg-quick` file does it.

## 6. Open questions

None.
