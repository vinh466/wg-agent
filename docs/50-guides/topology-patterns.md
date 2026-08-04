# Choosing a topology

> Task-oriented guide. Underlying concepts are in the
> [connectivity model](../40-concepts/connectivity-model.md); normative requirements in
> [SPEC-02](../20-spec/SPEC-02-forward-policy.md).

## Four common patterns

| Pattern | `intra` | `inter` | `external` | Use when |
|---|---|---|---|---|
| Isolated hub | `DENY` | `DENY` | `DENY` | Peers reach only services on the server. Bastion, internal apps |
| **Flat LAN per group** ← default | `ALLOW` | `DENY` | `DENY` | Each interface is an independent LAN. Teams, separate customers |
| Segmented with a bridge | `ALLOW` | `ALLOW_LIST` | `DENY` | Staff reach prod; contractors stay isolated |
| VPN gateway | `DENY` | `DENY` | `ALLOW` plus NAT | Exit node, internet egress through the server |

### Why the default is a flat LAN per group

It matches the natural expectation when an operator creates one interface and adds several
peers: members are expected to see each other.

One point to state plainly: this default is **not** deny-all. Absolute isolation requires
declaring `intra_interface: DENY` explicitly. The other two axes remain `DENY`, so nothing
leaks beyond the interface.

## Example configurations

### Isolated hub

```yaml
forward_policy:
  intra_interface: DENY
  inter_interface: DENY
  external: DENY
```

Peers reach only services on the server itself, over the INPUT path which always passes. They
neither reach each other nor leave the node.

### Flat LAN per group — the default

```yaml
# No declaration needed; these are the defaults
```

### Segmented with a bridge

```yaml
# wg-staff
forward_policy:
  intra_interface: ALLOW
  inter_interface: ALLOW_LIST
  allowed_peer_interfaces: [wg-prod]

# wg-prod
forward_policy:
  intra_interface: ALLOW
  inter_interface: ALLOW_LIST
  allowed_peer_interfaces: [wg-staff]

# wg-contractor — inter_interface omitted, so it defaults to DENY
forward_policy:
  intra_interface: ALLOW
```

Declare the relationship on **both** interfaces when both sides need to initiate. A one-sided
declaration is valid and stateful, but usually reflects an oversight, so validation warns
about it.

### VPN gateway

```yaml
forward_policy:
  intra_interface: DENY
  inter_interface: DENY
  external: ALLOW
nat:
  enabled: true
  masquerade_out_interface: eth0
  enable_uplink_forwarding: true
```

`enable_uplink_forwarding` is mandatory whenever `external = ALLOW`; without it the agent
rejects the spec. Enabling forwarding on the uplink reaches outside the agent's ownership and
therefore requires an explicit declaration.

## Several interfaces or one shared interface?

| | Several interfaces | One interface, split by subnet |
|---|---|---|
| Isolation | By `iif`/`oif` — one rule, unambiguous | By address — many rules, error-prone |
| UDP ports | One per interface | A single port |
| Server keys | One per interface, rotated independently | Rotation disconnects everyone |
| Blast radius | Losing `wg1` leaves `wg0` intact | One failure affects everything |
| Client config | Must target the right port | Simpler |

**Recommendation:** split interfaces along **trust boundaries** — tenant, environment,
privilege level. Within one boundary, share an interface and partition by subnet.

Dozens of interfaces on a node is reasonable. Thousands is not — a design that allocates one
interface per customer at SaaS scale needs revisiting.

## A constraint to know upfront

**Interface subnets must not overlap.** `wg0` and `wg1` cannot both use `10.10.0.0/24`; the
agent rejects that with `ADDRESS_CONFLICT` (`REQ-VAL-014`).

Multi-tenancy with overlapping addresses requires network namespaces, which are outside the v1
scope.

## After changing policy

Changing server-side `forward_policy` is **not sufficient** for inter-interface flows.
Client-side `AllowedIPs` must widen as well.

The correct approach: regenerate the client config with `ClientRouting.AUTO`. The agent derives
`AllowedIPs` from the current policy and returns a `routing_explanation` describing how.

When traffic still does not pass, run `DiagnoseInterface`
([SPEC-11](../20-spec/SPEC-11-diagnostics.md)).
