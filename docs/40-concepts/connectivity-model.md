# Connectivity model

> Explanatory document, not normative. Normative requirements live in
> [SPEC-02](../20-spec/SPEC-02-forward-policy.md).
>
> This is the hardest concept in the system. Read it before configuring multiple interfaces.
>
> Forward policy is delivered after the first release — B-04 in the
> [backlog](../60-planning/backlog.md). Until then forwarding and NAT are the host's, set once for
> every interface: [SPEC-13](../20-spec/SPEC-13-applying-changes.md) section 5.

## Reference topology

One node running wg-agent with three interfaces:

```
                        ┌──────────── wg-agent node ────────────┐
                        │                                       │
  client A ─── wg0 ────►│ 10.10.0.1/24  :51820                  │
  10.10.0.5             │        │                              │
                        │        │  FORWARD  ◄── agent-managed  │──► eth0 ──► internet
  client B ─── wg1 ────►│ 10.20.0.1/24  :51821                  │
  10.20.0.5             │        │                              │
                        │        │                              │
  client C ─── wg2 ────►│ 10.30.0.1/24  :51822                  │
  10.30.0.5             │                                       │
                        └───────────────────────────────────────┘
```

Each interface has its own UDP port, server key and subnet. Separate public addresses are
unnecessary — only the port differs.

## Five traffic flows

| # | Flow | Chain | Governed by |
|---|---|---|---|
| 1 | Peer to server (`10.10.0.1`) | INPUT | **Always passes.** Not forwarding |
| 2 | Peer to peer on `wg0` | FORWARD `wg0→wg0` | `intra_interface` |
| 3 | `wg0` to `wg1` | FORWARD `wg0→wg1` | `inter_interface` |
| 4 | Peer to internet | FORWARD `wg0→eth0` plus NAT | `external` and `nat` |
| 5 | Peer to a LAN behind the server | FORWARD `wg0→eth0` | `external` plus a static host route |

Flow 1 deserves attention: a peer can **always** reach services on the server itself, even
with all three axes locked. Blocking that requires a host firewall rule, which is outside the
agent's scope by design.

## Two places decide whether traffic passes

WireGuard's `AllowedIPs` filters in **both directions**: it decides which packets enter the
tunnel and it **discards** inbound packets whose source address is absent from the sending
peer's `AllowedIPs`.

| Location | Role | Controlled by | A security boundary? |
|---|---|---|---|
| Client-side `AllowedIPs` | Routing intent | The client, freely editable | No — untrustworthy |
| Server-side `allowed_ips` | Cryptokey routing | The agent | Blocks source spoofing only |
| `forward_policy` (nftables) | Enforcement | The agent | **Yes — the actual boundary** |

### Why narrowing `allowed_ips` does not isolate peers

This is the most counter-intuitive point in the system.

Suppose peer A has `allowed_ips = 10.10.0.5/32` on the server. A can still send a packet with
`src=10.10.0.5 dst=10.10.0.6`:

1. The server receives it, decrypts, and checks whether the source falls within A's
   `allowed_ips` — it does, so the packet is accepted
2. The server routes on `dst=10.10.0.6`, matching `10.10.0.0/24 dev wg0`
3. Cryptokey routing finds the peer holding `10.10.0.6` in its `allowed_ips` — peer B
4. The packet is encrypted and sent to B

Narrowing `allowed_ips` prevents A from **spoofing a source address**; it does not prevent A
from **talking to B**. Blocking on the client side is not trustworthy because a client edits
its own config.

**Real isolation requires a rule in the FORWARD chain.**

## Worked example: letting A on wg0 reach B on wg1

```
Server side — handled by the agent:
  wg0.forward_policy: inter_interface = ALLOW_LIST, allowed_peer_interfaces = [wg1]
  wg1.forward_policy: inter_interface = ALLOW_LIST, allowed_peer_interfaces = [wg0]
  sysctl:  conf.wg0.forwarding=1, conf.wg1.forwarding=1     ← eth0 untouched
  peer A on wg0:  allowed_ips = 10.10.0.5/32                ← unchanged /32
  peer B on wg1:  allowed_ips = 10.20.0.5/32                ← unchanged /32

Client side — must widen, or traffic silently fails:
  A's config:  AllowedIPs = 10.10.0.0/24, 10.20.0.0/24
  B's config:  AllowedIPs = 10.20.0.0/24, 10.10.0.0/24
```

The second counter-intuitive point: **server-side `allowed_ips` needs no change.** The server
already has a route to `10.20.0.0/24 dev wg1` from wg1's own address. What must widen is the
**client** config — something the agent only *generates*, never *enforces*.

Forgetting to widen client-side `AllowedIPs` is the leading cause of the symptom *"policy is
set to ALLOW but nothing reaches the other side"*. That is why `ClientRouting.AUTO`
(`REQ-KEY-031`) exists: the agent derives the value from policy so operators neither compute
it nor compute it wrongly.

## Why only WireGuard interfaces need forwarding enabled

The kernel decides forwarding from the `forwarding` sysctl of the **input** interface, not from
a global variable:

| Flow | Outbound needs | Return needs | Touches uplink? |
|---|---|---|---|
| Peer to peer on `wg0` | `conf.wg0.forwarding=1` | `conf.wg0.forwarding=1` | No |
| `wg0` to `wg1` | `conf.wg0.forwarding=1` | `conf.wg1.forwarding=1` | No |
| Peer to internet | `conf.wg0.forwarding=1` | `conf.eth0.forwarding=1` | **Yes** |

The first two flows involve only WireGuard interfaces, which the agent fully owns. Peer-to-peer
and interface-to-interface traffic therefore work **without touching global host
configuration**.

Only egress requires forwarding on the uplink, because the return path arrives on `eth0`. That
lies outside the agent's ownership and needs an explicit opt-in.

## Why ALLOW does not guarantee traffic passes

The agent owns only `table inet wg_agent` and leaves the host firewall alone
([ADR-0008](../10-decisions/ADR-0008-no-host-firewall-ownership.md)).

In nftables, base chains on the same hook are evaluated in sequence:
- A `drop` verdict in any chain **terminates immediately and discards the packet**
- An `accept` verdict merely ends that chain and **continues** to the next

The agent can therefore **guarantee blocking** but **cannot guarantee passing**. `ufw`,
`firewalld` or Docker may still block in their own chains.

Read `ALLOW` as **"the agent does not block"**.

When this situation arises, `DiagnoseInterface` with the `foreign_forward_chains` check
([SPEC-11](../20-spec/SPEC-11-diagnostics.md)) lists the other tables hooked into FORWARD and
names the culprit.

## Asymmetry

The agent's rules are **stateful**: the forward chain begins with
`ct state established,related accept`. Policy therefore applies only to **new** connections.

When `wg0` permits `wg1` but `wg1` does not permit `wg0`:
- `wg0` can initiate connections and replies return normally
- `wg1` cannot initiate towards `wg0`

This is intended behavior. Two-way communication requires declaring the relationship on both
interfaces. Validation warns about one-sided declarations (`REQ-VAL-023`) because they usually
indicate an oversight.

## Read next

- [Topology patterns](../50-guides/topology-patterns.md) — four common configurations
- [SPEC-02](../20-spec/SPEC-02-forward-policy.md) — normative requirements
