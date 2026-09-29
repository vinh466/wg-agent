---
id: ADR-0012
title: Managed netlink in .NET, carrying ADR-0002 forward
status: Accepted
owner: Vinh Nguyen
created: 2026-09-29
updated: 2026-09-29
supersedes: [ADR-0002]
superseded_by: null
affects: [SPEC-01, SPEC-02, SPEC-03, SPEC-06, SPEC-09]
---

# ADR-0012: Managed netlink in .NET, carrying ADR-0002 forward

## Context

[ADR-0002](ADR-0002-netlink-over-wg-quick.md) decided to drive WireGuard through netlink
rather than by shelling out to `wg` and `wg-quick`. Its reasoning holds, but every line of
it is written in Go terms — `wgctrl-go`, `vishvananda/netlink`, `exec.Command` — because a
Go implementation was assumed.

The implementation language is now C# on .NET 10. That move does not reopen the decision;
it reopens two questions ADR-0002 could not answer, because the answers are stack-specific:

- Can managed .NET reach the two netlink families without a child process, given that no
  maintained .NET WireGuard or netlink library exists?
- Does the no-child-process invariant of `REQ-SEC-041` survive, or does the language force
  a shell to `wg` and `ip` the way ADR-0002's alternative A would have?

The kernel interface is unchanged: generic netlink for the device and its peers, rtnetlink
for the link, its addresses and its routes. `wgctrl` and `vishvananda/netlink` were only
the Go names for those two families.

## Alternatives considered

### A — Shell out to `wg` and `wg-quick` from .NET
The .NET WireGuard ecosystem is thinner than Go's, which makes invoking the command-line
tools tempting.

- For: little initial code; the tools are present on most nodes
- Against, and this is decisive:
  - Every objection in ADR-0002 alternative A still applies — no granular peer operation,
    no diff, exit-code errors, runtime dependencies on `bash`, `wg`, `ip`
  - `wg set … private-key <path>` requires the key on the filesystem, where argv and
    `/proc` expose it; netlink passes the key over the socket
  - It breaks `REQ-SEC-041` outright

### B — Managed netlink over libc sockets
Open `AF_NETLINK` sockets and speak both families from managed code.

- For: keeps every property ADR-0002 chose netlink for; no child process
- Against: `new Socket((AddressFamily)16, …)` is refused, because .NET translates the
  address-family enum and `16` is not `AF_NETLINK` on its table, so the socket must be
  created through libc

## Decision

Adopt **B**, which carries the decision of ADR-0002 forward unchanged. This ADR supersedes
ADR-0002 as the current statement of it; the decision itself is not reversed.

The stack-specific questions are answered by measurement rather than assumption:

- A spike created a WireGuard link through rtnetlink, resolved the `wireguard` family
  through `CTRL_CMD_GETFAMILY`, ran `WG_CMD_GET_DEVICE`, and read a device back identical to
  what `wg show` reported — device key, listen port, peer, nested allowed-ips, keepalive.
- The only native surface is three libc calls: `socket`, `bind`, `close`. The file
  descriptor is then wrapped in the managed `Socket`, so the rest — asynchronous reads and writes included — is
  managed. No child process runs, so `REQ-SEC-041` holds.
- NativeAOT produces a single binary of roughly 1.3–1.5 MB against glibc whose only dynamic
  dependencies are libc and the loader, and that binary reaches the kernel. No runtime is
  installed on the node.

One gap is specific to the language and does not exist in Go: no .NET release through 10
exposes X25519 — it is absent from `ECCurve.NamedCurves`, the OID is rejected, and the
PKCS#8 importer refuses it. `BouncyCastle.Cryptography`, pure managed, fills it; its derived
public key agrees with the kernel, and the AOT trimmer keeps only the curve. This is the one
managed dependency the kernel edge carries, isolated in `WgAgent.Platform`.

For NAT, an nftables library rather than invoking `nft` preserves the no-child-process
property, exactly as ADR-0002 required; that work is deferred under B-04.

## Consequences

### Positive
- Adding and removing peers leaves other peers untouched, the prerequisite for serving many
- Reconciliation diffs at field granularity
- A single self-contained binary with no runtime and no host dependency beyond the kernel

### Negative — the price paid
- The netlink layer is hand-written over raw sockets, and the message framing is the
  project's to maintain
- One managed crypto dependency, for a curve the platform ought to provide
- Cross-compiling the NativeAOT binary to arm64 is harder than Go's `GOARCH=arm64`, and a
  glibc build must be produced per libc; v1 targets `linux-x64` on glibc only

### Follow-on work
- `WgAgent.Platform.Linux` is the adapter: `WG_CMD_SET_DEVICE` with peer chunking across
  messages, the rtnetlink link, address and route operations, and the `RTNLGRP_LINK`
  subscription of `REQ-RCN-021`

## Conditions for revisiting

Two signals, one inherited and one new. A requirement to run where the kernel module cannot
be loaded calls for a userspace backend such as `boringtun` behind the same ports — a second
adapter, not a return to `wg-quick`, as ADR-0002 already held. And should a maintained,
AOT-compatible managed netlink library appear, the hand-written socket layer behind `IDevice`
and `ILink` could be replaced without disturbing anything above the port.
