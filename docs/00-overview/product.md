# Product positioning

## What wg-agent is

**A node-level control plane agent for WireGuard on Linux.**

It turns the WireGuard state of **one machine** into an API-addressable resource under a
declarative model. Callers describe desired state; the agent is responsible for bringing
the kernel to that state and keeping it there.

The critical boundary: **the agent manages one node.** Orchestration across nodes belongs
to the platform layer above it.

```
┌─────────────────────────────────────────────┐
│  Platform / SaaS / Panel / Terraform / K8s  │  ← Org, user, billing, RBAC, IPAM
└──────────────────────┬──────────────────────┘
                       │ gRPC / REST over a local transport
                       │ (unix socket or loopback HTTP)
        ┌──────────────┼──────────────┐
        ▼              ▼              ▼
   ┌─────────┐    ┌─────────┐    ┌─────────┐
   │wg-agent │    │wg-agent │    │wg-agent │   ← 1 agent per node
   │ node-a  │    │ node-b  │    │ node-c  │
   └─────────┘    └─────────┘    └─────────┘
```

Both agent listeners are local to the node under
[ADR-0009](../10-decisions/ADR-0009-local-only-listeners.md). A platform on another host
supplies its own hop — an SSH tunnel, or a component co-located with the agent. Direct
cross-host management over mTLS is deferred.

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

## In scope

- Interface lifecycle: create, delete, up/down, addresses, MTU, listen port, fwmark
- Peer lifecycle: create, update, delete, list
- Routes on the interface matching AllowedIPs
- Key generation and rotation, public key import
- Runtime state read from the kernel
- Persistent desired state with automatic reconciliation
- Client `.conf` generation and QR codes
- Forward policy: peer-to-peer, interface-to-interface, egress
- NAT through nftables
- Metrics, health, audit log, per-interface diagnostics and a node overview endpoint
- Installation, update and removal through a script over a released `.deb`

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
| Remote management over TCP with mTLS, and node enrollment | Deferred — [ADR-0009](../10-decisions/ADR-0009-local-only-listeners.md) |
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

> **Declarative reconciliation, durable desired state, an API that runs least-privilege and
> local by default, and first-class Terraform and Kubernetes support.**

Plus one property the architecture provides for free: **the data plane runs independently
of the agent.** When the agent crashes or is being upgraded, the kernel keeps forwarding
traffic. An agent outage is not a VPN outage.

## Read next

- [Architecture](architecture.md) — layers and library boundaries
- [Connectivity model](../40-concepts/connectivity-model.md) — the hardest concept
- [Decision index](../10-decisions/README.md) — why the system has this shape
