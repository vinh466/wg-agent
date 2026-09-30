# Product positioning

## What wg-agent is

**A node-level control plane agent for WireGuard on Linux.**

It turns the WireGuard state of **one machine** into an API-addressable resource under a
declarative model. Callers describe desired state; the agent is responsible for bringing
the kernel to that state and keeping it there.

The critical boundary: **the agent manages one node.** Orchestration across nodes belongs
to the platform layer above it.

**The first release is narrower than the product this page describes.** It is a wrapper over
`wg` and `wg-quick` that replaces an operator's manual work on a node —
[ADR-0013](../10-decisions/ADR-0013-drive-wg-and-wg-quick.md) — reached from a CLI on the node
and a REST API over the operator's private network. The platform-facing properties below grow
from it through the [backlog](../60-planning/backlog.md).

```
┌─────────────────────────────────────────────┐
│  Platform / SaaS / Panel / Terraform / K8s  │  ← Org, user, billing, RBAC, IPAM
└──────────────────────┬──────────────────────┘
                       │ REST over the operator's private network
                       │ (one bearer token)
        ┌──────────────┼──────────────┐
        ▼              ▼              ▼
   ┌─────────┐    ┌─────────┐    ┌─────────┐
   │wg-agent │    │wg-agent │    │wg-agent │   ← 1 agent per node
   │ node-a  │    │ node-b  │    │ node-c  │
   └─────────┘    └─────────┘    └─────────┘
```

The API listener serves plain HTTP on an address the operator chooses, and the operator places
it on a private network —
[ADR-0015](../10-decisions/ADR-0015-network-listener-with-a-shared-secret.md). TLS and mutual TLS
are deferred until a node must be reached across a network the operator does not control.

## Intended consumers

Not end users. The consumers are **machines**:

- VPN platforms and VPN SaaS products
- Control panels, hosting, PaaS
- Terraform providers
- Kubernetes operators
- Internal automation

That constraint shapes the whole API: idempotent writes, full state reads for drift
detection, stable identifiers, and errors distinguishable by code rather than by message
string.

## In the first release

- Interfaces the agent creates: create, delete, enable and disable, addresses, MTU, listen port
- Peers: create, update, delete, list — a supplied public key, or a key pair the agent generates
- Routes for allowed IPs, installed by `wg-quick`
- Keys generated through `wg`; an interface key supplied or generated
- A client `.conf` returned when a peer is created with a generated key pair
- Runtime state read from `wg show`
- Durable desired state, with interfaces restored at boot by their `wg-quick@` units
- A CLI on the node and a REST API behind one token
- A `.deb` package

## Later — the backlog

Forward policy and NAT; drift correction; adoption of interfaces configured by hand;
diagnostics and a node overview; metrics and an audit log; backup and restore; QR codes, a key
rotation operation and client routing modes; the installation script; roles, several tokens and
TLS. Each has its requirements already written; the [backlog](../60-planning/backlog.md) says
when each returns.

**IPv4 only.** See [ADR-0005](../10-decisions/ADR-0005-ipv4-only-in-v1.md).

## Out of scope

This is the most important section of this page. Most open-source WireGuard projects die
of scope creep — they start as an agent and end as a half-finished SaaS that nobody can
reuse.

| Not done here | Belongs to |
|---|---|
| VPN user authentication | Platform |
| User, organization, tenant management | Platform |
| Billing, quota | Platform |
| UI, dashboard | Platform |
| RBAC, IAM, business ACL | Platform |
| IPAM — allocating peer addresses | Platform. The agent only **validates** |
| Multi-node orchestration, mesh topology | Platform |
| Business database | Platform |
| `PostUp` / `PostDown` shell hooks | Never — [ADR-0007](../10-decisions/ADR-0007-no-shell-hooks.md) |
| Owning the host firewall | Never — [ADR-0008](../10-decisions/ADR-0008-no-host-firewall-ownership.md) |
| Policy routing for full-tunnel clients | Client-side concern |
| Host DNS management | Client-side concern |
| IPv6 | Deferred — [ADR-0005](../10-decisions/ADR-0005-ipv4-only-in-v1.md) |
| Mutual TLS and node enrollment | Deferred — [ADR-0015](../10-decisions/ADR-0015-network-listener-with-a-shared-secret.md) carries one token over the operator's private network |
| Per-principal request rate limiting | Deferred until the API has production traffic to size a limit against |
| A signed APT repository | Deferred — [ADR-0010](../10-decisions/ADR-0010-install-script-over-released-deb.md) ships an install script over a released `.deb` |
| Zero-downtime interface key rotation | Deferred. `REQ-KEY-004` warns that rotation disconnects peers |
| Userspace WireGuard (boringtun) | Deferred |
| Network namespaces | Deferred |
| Overlapping subnets across interfaces | Requires network namespaces |

## Differentiation

`wgrest`, `wg-api`, Netmaker, Netbird and Firezone already exist. The differentiator is
**not** "a REST API for WireGuard" — that has been built several times.

The differentiator is:

> **Declarative desired state over the tools operators already trust — `wg-quick` files and
> units — with an API that refuses what WireGuard would silently break, and a path from there to
> reconciliation and first-class Terraform and Kubernetes support.**

Plus one property the architecture provides for free: **the data plane runs independently
of the agent.** When the agent crashes or is being upgraded, the kernel keeps forwarding
traffic. An agent outage is not a VPN outage.

## Read next

- [Architecture](architecture.md) — layers and library boundaries
- [Connectivity model](../40-concepts/connectivity-model.md) — the hardest concept
- [Decision index](../10-decisions/README.md) — why the system has this shape
