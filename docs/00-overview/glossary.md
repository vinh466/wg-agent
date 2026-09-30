# Glossary

Terms used consistently across documentation and code. Where a concept has several names
in common usage, the "Avoid" column lists the rejected ones.

| Term | Meaning | Avoid |
|---|---|---|
| **Agent** | The wg-agent process on one node | daemon, server |
| **Node** | A Linux machine running the agent | host (reserved for the OS underneath) |
| **Platform** | The layer calling the agent API: the operator's scripts or orchestrator | client, caller (when describing the architectural role) |
| **Caller** | The party invoking a specific RPC | — |
| **Interface** | A WireGuard network interface, e.g. `wg0` | tunnel, device |
| **Peer** | A WireGuard counterpart on an interface, identified by public key | client, user |
| **Client** | An end-user device running WireGuard | — |
| **Desired state** | State declared by the platform and stored durably by the agent | config |
| **Observed state** | Actual state read from the kernel | actual state |
| **Drift** | A difference between desired and observed in an **agent-owned** field | — |
| **Reconcile** | Bringing observed state into agreement with desired state | sync |
| **agent-owned** | A field the agent enforces on every reconcile | — |
| **kernel-owned** | A field the kernel updates at runtime; reconcile does not overwrite it | — |
| **Data plane** | The packet path: kernel encryption and forwarding | — |
| **Control plane** | The control path: the agent and its API | — |
| **Uplink** | The physical egress interface, e.g. `eth0` | WAN, external interface |
| **Intra-interface** | Traffic between peers on the **same** interface | peer-to-peer |
| **Inter-interface** | Traffic between two different WireGuard interfaces | cross-interface |
| **External** | Traffic from a WireGuard interface to the uplink | internet, egress |
| **AllowedIPs** (capitalized) | The **client**-side field, in a `.conf` file | — |
| **allowed_ips** (lowercase) | The **server**-side field, in `PeerSpec` | — |
| **BYOK** | Bring Your Own Key — the client generates the keypair and sends only the public key | — |
| **Cryptokey routing** | WireGuard's mapping of addresses to peers via AllowedIPs | — |
| **Roaming** | A client changes network address and the kernel learns the new endpoint | — |
| **REQ ID** | The stable identifier of a normative requirement, e.g. `REQ-FWD-012` | — |

## Easily confused

### `AllowedIPs` versus `allowed_ips`

Two different things, and the most common source of confusion in this system.

| | Client side (`AllowedIPs`) | Server side (`allowed_ips`) |
|---|---|---|
| Location | The `.conf` file on the client machine | `PeerSpec` in the agent |
| Determines | Which traffic the client sends into the tunnel | Which peer receives packets for a given address |
| Controlled by | The client, freely editable | The agent |
| A security boundary? | No | Blocks source spoofing only; does not isolate peers |

Full treatment in the [connectivity model](../40-concepts/connectivity-model.md).

### Drift versus difference

Not every difference is drift. A difference in a kernel-owned field — such as `endpoint`
after a client roams — is normal behavior, not drift. See `REQ-RCN-012`.
