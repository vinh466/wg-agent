# Product positioning

## What wg-agent is

**An internal tool that manages the WireGuard interfaces of one Linux node.**

It replaces the manual work of keeping `wg-quick` files by hand: it creates interfaces, adds and
removes peers, hands a client its configuration and reports status, and it refuses the
configurations WireGuard would silently break. It drives `wg` and `wg-quick` rather than the
kernel directly — [ADR-0013](../10-decisions/ADR-0013-drive-wg-and-wg-quick.md), carried forward
by [ADR-0017](../10-decisions/ADR-0017-operator-hooks-through-the-cli.md).

The critical boundary: **the agent manages one node.** Coordinating several nodes belongs to the
operator's own scripts or orchestrator above it.

By the operator's statement of 2026-09-30 it is an internal tool. Support for parties other than
its operator is not a goal, and the parts of the plan that served them were struck rather than
deferred — the [backlog](../60-planning/backlog.md) lists them under *Not planned*.

```
┌─────────────────────────────────────────────┐
│   The operator's scripts or orchestrator    │  ← which addresses, which nodes, which users
└──────────────────────┬──────────────────────┘
                       │ REST over the operator's private network
                       │ (one bearer token)
        ┌──────────────┼──────────────┐
        ▼              ▼              ▼
   ┌─────────┐    ┌─────────┐    ┌─────────┐
   │wg-agent │    │wg-agent │    │wg-agent │   ← 1 agent per node, and its CLI on the node
   │ node-a  │    │ node-b  │    │ node-c  │
   └─────────┘    └─────────┘    └─────────┘
```

The API listener serves plain HTTP on an address the operator chooses, and the operator places
it on a private network —
[ADR-0015](../10-decisions/ADR-0015-network-listener-with-a-shared-secret.md). TLS waits until a
node must be reached across a network the operator does not control.

## Who uses it

The operator, twice over: at a node's shell through the CLI, and from their own scripts or
orchestrator through the API. Both reach the same core, and both are written against codes rather
than messages: idempotent writes, stable identifiers, and errors distinguishable by reason code.

## In the first release

- Interfaces the agent creates: create, delete, enable and disable, addresses, MTU, listen port
- Peers: create, update, delete, list — a supplied public key, or a key pair the agent generates
- Routes for allowed IPs, installed by `wg-quick`
- Keys generated through `wg`; an interface key supplied or generated
- A client `.conf` returned when a peer is created with a generated key pair, and the next free
  address of the interface's subnet given to such a peer when asked for none
- `PostUp` and `PostDown` commands, set through the CLI alone
- An interface and its peers created from one JSON document — how an interface kept by hand moves
  under the agent, once
- Runtime state read from `wg show`
- Durable desired state, with interfaces restored at boot by their `wg-quick@` units
- A CLI on the node and a REST API behind one token
- A `.deb`, and an install script that installs, updates and removes it from the GitHub release

## Later — the backlog

Forward policy and NAT managed by the agent; drift correction; adoption of interfaces configured
by hand; diagnostics and a node overview; metrics and an audit log; backup and restore; QR codes,
a key rotation operation and client routing modes; TLS. Each has its requirements already
written; the [backlog](../60-planning/backlog.md) says when each returns.

**IPv4 only.** See [ADR-0005](../10-decisions/ADR-0005-ipv4-only-in-v1.md).

## Out of scope

| Not done here | Belongs to |
|---|---|
| Users, organizations, billing, a UI | The operator's own systems |
| Deciding which peer gets which address | The operator. The agent **validates**, and gives a generated peer the next free address of its interface's subnet — `REQ-KEY-047` |
| Several nodes, mesh topology | The operator's scripts or orchestrator |
| `PostUp` / `PostDown` through the API | Never — [ADR-0007](../10-decisions/ADR-0007-no-shell-hooks.md); the operator sets them through the CLI — [ADR-0017](../10-decisions/ADR-0017-operator-hooks-through-the-cli.md) |
| Owning the host firewall | Never — [ADR-0008](../10-decisions/ADR-0008-no-host-firewall-ownership.md) |
| Policy routing for full-tunnel clients | Client-side concern |
| Host DNS management | Client-side concern |
| IPv6 | Deferred — [ADR-0005](../10-decisions/ADR-0005-ipv4-only-in-v1.md) |
| Roles, several tokens, mutual TLS, node enrollment | Not planned — one operator, one token |
| An APT repository with `apt upgrade` | Not planned — [ADR-0019](../10-decisions/ADR-0019-install-script-over-released-deb.md); an install script and a released `.deb` deliver install, update and uninstall |
| Published clients, a compatibility gate in CI | Not planned — no client outside the operator's own |
| Zero-downtime interface key rotation | Deferred. `REQ-KEY-004` warns that rotation disconnects peers |
| Userspace WireGuard (boringtun), network namespaces | Deferred |
| Overlapping subnets across interfaces | Requires network namespaces |

## What it gives the operator

Against editing `wg-quick` files by hand:

- **One command per change, from anywhere on the private network.** No shell on the node for a
  peer, and no restart for one either — `wg syncconf` leaves every other session untouched.
- **Refusal before breakage.** Host bits, a default route, a port in use, a peer carrying the
  node's own key — the mistakes WireGuard accepts and then silently mishandles are refused with a
  reason code.
- **A complete client file, once.** Generated with the peer, carrying the right endpoint and port.

And one property the design provides for free: **the data plane runs independently of the
agent.** The interfaces belong to their `wg-quick@` units, so when the agent stops or is
upgraded, traffic keeps flowing. An agent outage is not a VPN outage.

## Read next

- [Architecture](architecture.md) — layers and the programs behind each operation
- [Roadmap](../60-planning/roadmap.md) — the phases of the first release
- [Decision index](../10-decisions/README.md) — why the system has this shape
