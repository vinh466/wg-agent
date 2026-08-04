---
id: ADR-0002
title: Netlink and wgctrl instead of wg-quick
status: Accepted
owner: Vinh Nguyen
created: 2026-08-03
updated: 2026-08-04
supersedes: []
superseded_by: null
affects: [SPEC-01, SPEC-02, SPEC-03]
---

# ADR-0002: Netlink and wgctrl instead of wg-quick

## Context

Two ways exist to drive WireGuard from Go: shelling out to `wg` and `wg-quick`, or
talking to the kernel through netlink and `wgctrl-go`.

One misconception needs clearing first: **`wgctrl-go` cannot create a network interface.**
It configures a WireGuard device — private key, listen port, fwmark, peer list. Creating
and deleting links, assigning addresses, setting MTU and adding routes all belong to
netlink.

[ADR-0001](ADR-0001-declarative-model.md) selected a declarative model, which requires
diffing and repairing individual fields.

## Alternatives considered

### A — Use wg-quick
The agent writes `/etc/wireguard/wgN.conf` and invokes `wg-quick up` / `down`.

- For: handles many concerns out of the box; widely proven; little initial code
- Against, and this is decisive:
  - **Only `up` and `down` exist — no granular operation.** Adding one peer means
    tearing the interface down and rebuilding it, **disconnecting every established
    peer**. For a hub serving hundreds of peers this is fatal
  - Reconciliation requires diffing; wg-quick cannot diff
  - Errors arrive as an exit code and a stderr string, indistinguishable between
    "port in use" and "module not loaded"
  - `PostUp` and `PostDown` accept arbitrary shell, which becomes a remote code execution
    surface once exposed through an API — see [ADR-0007](ADR-0007-no-shell-hooks.md)
  - Runtime dependencies: `bash`, `wg`, `ip`, `resolvconf`, `iptables` must be present
  - No locking, so concurrent requests race
  - Statistics still require wgctrl, so the result is a hybrid anyway

### B — Native netlink and wgctrl
- For: per-peer operations without disruption; typed errno errors; no external binaries;
  far faster with no process forking; testable in network namespaces
- Against: the parts wg-quick provides must be implemented

## Decision

Adopt **alternative B**.

The point that makes this straightforward: **the hard parts of wg-quick are the parts the
agent does not need.**

| wg-quick does | Agent needs it? | Cost to implement |
|---|---|---|
| Create link, assign address, MTU, up/down | Yes | ~30 lines of netlink |
| Load device and peer configuration | Yes | wgctrl, already available |
| Routes for AllowedIPs | Yes | ~15 lines of netlink |
| fwmark and policy routing for `0.0.0.0/0` | No | Full-tunnel **client** logic |
| DNS through resolvconf | No | **Client** logic |
| PostUp and PostDown hooks | No | Prohibited on security grounds |

The two most complex parts of wg-quick — policy routing and DNS — are client-side
concerns, not server-agent concerns. What remains is roughly 150 lines of netlink.

For NAT, use the `google/nftables` library rather than invoking `nft`, preserving the
no-exec property.

## Consequences

### Positive
- Adding and removing peers leaves other peers untouched, a prerequisite for serving many
  peers
- Reconciliation can diff at field granularity
- A static binary with `CGO_ENABLED=0` and no host dependencies beyond the kernel

### Negative — the price paid
- The netlink layer must be implemented and tested
- The nftables API must be learned instead of writing familiar command strings
- Users lose any ability to inject arbitrary behavior through hooks

### Follow-on work
- Invariant: **no `exec.Command` in production paths.** Test helpers only
- The wg-quick `.conf` **format** is retained as the client export format — the agent
  *generates* that file without *executing* it

## Conditions for revisiting

A requirement to run where the kernel module cannot be loaded (restricted containers)
would call for a userspace backend such as `boringtun`. That is a second backend behind
the same interface, **not** a reason to return to wg-quick.
