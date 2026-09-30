---
id: SPEC-07
title: Validation
prefix: VAL
status: Accepted
version: 2.0
owner: Vinh Nguyen
created: 2026-08-03
updated: 2026-09-30
depends_on: [SPEC-01, SPEC-02]
adrs: [ADR-0005, ADR-0006, ADR-0011, ADR-0013, ADR-0017]
milestone: P1
---

# SPEC-07: Validation

## 1. Scope

Every validation rule and its severity. This is the only place validation rules are
defined; other modules reference them by REQ ID.

## 2. Principles

The agent performs no IPAM. It does reject configurations that cause hard-to-diagnose
incidents, because detecting a fault at the API call is far cheaper than debugging it in
production.

> **REQ-VAL-001** — An error-severity finding MUST block the write and return the
> corresponding reason code.

> **REQ-VAL-002** — A warning-severity finding MUST appear in `status.warnings` without
> blocking the write.

A warning carries a reason code from the set of `REQ-API-041` just as an error does, which is
what `REQ-RES-033` stores. A caller branching on a warning should no more parse its message than
one branching on a failure.

## 3. Error severity

> **REQ-VAL-010** — The agent MUST reject an interface name not matching
> `^[a-zA-Z][a-zA-Z0-9_-]{0,14}$`, or equal to `all` or `default`, with `INTERFACE_NAME_INVALID`.

The kernel refuses `all` and `default` with `EINVAL`, because each would collide with a directory
of the same name under `/proc/sys/net/ipv4/conf/`; refusing them here names the reason instead of
failing when the unit starts.

> **REQ-VAL-011** — The agent MUST reject a public key that is not base64 of exactly 32
> bytes with `PUBLIC_KEY_INVALID`.

A string whose unused final bits are not zero decodes to 32 bytes and is still not their base64:
WireGuard's own tools refuse it, and accepting it would give one key two spellings, so one peer
could be described twice.

> **REQ-VAL-012** — The agent MUST reject two peers on one interface holding identical
> `allowed_ips` entries with `ALLOWED_IPS_DUPLICATE`.

Cryptokey routing would be ambiguous: the kernel applies longest-prefix matching, and for
two identical prefixes the later-configured peer silently displaces the earlier one.

> **REQ-VAL-013** — The agent MUST reject a `listen_port` already used by a WireGuard
> interface on the host other than the one the spec names, with `LISTEN_PORT_IN_USE`.

> **REQ-VAL-014** — The agent MUST reject `addresses` overlapping a WireGuard interface on the
> host other than the one the spec names, with `ADDRESS_CONFLICT`.

> **REQ-VAL-015** — The agent MUST reject a create request naming a network link, or a file
> under `/etc/wireguard/`, that already exists, with `INTERFACE_EXISTS`.

`REQ-VAL-013` and `REQ-VAL-014` reach foreign interfaces as well as managed ones. A port or
subnet held by a link the agent did not create collides just as firmly, and checking only
managed interfaces would let the spec pass validation and fail when applied. They reach a
managed interface whose `enabled` is false as well: it has no device, but its unit would collide
the moment it is enabled. Both exclude the interface the spec names, so validating an adopted spec
does not match it against the very link it was read from. Two subnets overlap when one contains
the other.

`REQ-VAL-013` is not made redundant by the kernel. Two WireGuard devices may both hold one
listen port while at most one of them is up; the second bind is refused on the transition to up,
with `Address in use`. A port collision between two existing links is therefore reachable
exactly when one is down, which is the state adoption would take over and then bring up.

`REQ-VAL-015` is what keeps a name collision from becoming a silent takeover. Under
[SPEC-13](SPEC-13-applying-changes.md) a create writes `/etc/wireguard/<name>.conf` and starts
`wg-quick@<name>`: an existing file of that name is the operator's under `REQ-APL-002`, whether or
not it is running, and an existing link is somebody's live interface. Adoption under
[ADR-0011](../10-decisions/ADR-0011-operator-initiated-adoption.md) is the supported path to
managing either, and it is explicit.

A repeated create of a managed interface is refused like any other, which `REQ-RES-003` permits
because the store is left unchanged — the same answer `REQ-API-071` gives for a repeated peer
create.

> **REQ-VAL-016** — The agent MUST reject an `InterfaceSpec` whose `addresses` list is empty
> with `ADDRESSES_REQUIRED`.

`addresses` is a required field of `InterfaceSpec`, and nothing else rejected an empty list. A
WireGuard link with no address is a valid routed configuration on the host, so an adopted spec
can reach validation carrying one, and `REQ-KEY-031` would have no interface address to
substitute when generating a client configuration.

> **REQ-VAL-017** — The agent MUST reject a `PeerSpec` whose `allowed_ips` list is empty with
> `ALLOWED_IPS_REQUIRED`, unless `REQ-KEY-047` assigns one.

`allowed_ips` is a required field of `PeerSpec`, and the kernel permits a peer to carry none.
Such a peer receives no traffic, so storing it would describe an interface the operator did not
mean to have. Adoption is where this arrives, since a peer added by hand may have been left
half-configured.

> **REQ-VAL-020** — The agent MUST reject any address or CIDR of the IPv6 family with
> `IPV6_NOT_SUPPORTED`.

> **REQ-VAL-047** — The agent MUST reject an `addresses`, `allowed_ips` or `client_allowed_ips`
> entry that is not an address with a prefix length, with `CIDR_INVALID`.

Every other rule on those fields reads a network; an entry that is not one — `10.8.0.1` with no
prefix, or text — would otherwise reach `wg-quick` and fail there. An IPv6 entry is refused under
`REQ-VAL-020` rather than this rule.

Silent omission is prohibited, as is accepting the value without configuring it. Reasoning
in [ADR-0005](../10-decisions/ADR-0005-ipv4-only-in-v1.md).

> **REQ-VAL-036** — The agent MUST reject a `listen_port` outside 1 to 65535 with
> `LISTEN_PORT_INVALID`.

> **REQ-VAL-037** — The agent MUST reject a `persistent_keepalive` or `client_persistent_keepalive`
> above 65535 with `KEEPALIVE_INVALID`.

Both fields are 16-bit in WireGuard and 32-bit on the wire. A port of `0` asks the kernel to
choose one, which [SPEC-01](SPEC-01-resource-model.md) excludes because the choice changes at
every restart.

> **REQ-VAL-038** — The agent MUST reject an `mtu` outside 68 to 65535 with `MTU_INVALID`.

A WireGuard link accepts an MTU of `0` and of `65536`, and below 68 the kernel deletes every IPv4
address of the interface, which then do not return when the MTU is raised again. `REQ-VAL-032`
warns about the unusual values inside the valid range.

> **REQ-VAL-039** — The agent MUST reject a `private_key` or `preshared_key` that is not base64
> of exactly 32 bytes with `KEY_INVALID`.

> **REQ-VAL-040** — The agent MUST reject a peer whose public key is the interface's own public
> key with `PEER_IS_INTERFACE`.

WireGuard accepts such a peer without an error and never installs it, so it would be stored and
rendered, and missing from every status read.

> **REQ-VAL-041** — The agent MUST reject an `allowed_ips` entry with host bits set with
> `ALLOWED_IPS_NOT_CANONICAL`.

WireGuard masks the host bits away, so `10.0.0.5/24` becomes `10.0.0.0/24` and `REQ-VAL-012` would
miss it as a duplicate of `10.0.0.0/24`. The caller may have meant `10.0.0.5/32`; refusing asks
rather than guesses. `addresses` keep their host bits and are not affected.

> **REQ-VAL-042** — The agent MUST reject an `endpoint` that is not a host and a port from 1 to
> 65535, or a `node_endpoint` that is not a host, with `ENDPOINT_INVALID`.

`endpoint` is where the node reaches a peer; `node_endpoint`, a field of the request that creates a
peer with a generated key pair, is where the client reaches the node, and its port is the
interface's own under `REQ-KEY-037`.

> **REQ-VAL-044** — The agent MUST reject an `allowed_ips` entry whose prefix length is 0 with
> `ALLOWED_IPS_DEFAULT_ROUTE`.

`wg-quick` turns a default route among a peer's allowed IPs into full-tunnel policy routing on the
node itself: a firewall mark, a routing table of its own, and a rule sending every unmarked packet
through that peer. The node's own traffic — the replies of an SSH session included — would leave
through the tunnel. `manage_routes`, delivered later with backlog B-12, is what lets an operator
render `Table = off` and route such a peer by hand; until then the entry is refused.

> **REQ-VAL-045** — The agent MUST reject a `post_up` or `post_down` command containing a line
> break with `HOOK_INVALID`.

A line break would end the command and begin a line of the file's own, so a hook could add keys —
another `PostUp`, a `PrivateKey` — that `REQ-APL-003` exists to exclude.

> **REQ-VAL-046** — The agent MUST reject a peer created with `generate_keypair` and without
> `allowed_ips` when the interface's first subnet has no free host address, with `SUBNET_FULL`.

> **REQ-VAL-043** — The agent MUST reject a peer created with `generate_keypair` none of whose
> `allowed_ips` entries lies within the interface's subnets, with `CLIENT_ADDRESS_MISSING`.

The client configuration takes its `Address` from those entries under `REQ-KEY-043`; without one
the file would carry none.

> **REQ-VAL-048** — The agent MUST reject a peer creation request carrying both a `public_key` and
> `generate_keypair` with `PUBLIC_KEY_INVALID`.

> **REQ-VAL-049** — The agent MUST reject a peer creation request carrying both a `preshared_key`
> and `generate_preshared_key` with `KEY_INVALID`.

A request that names a key and asks for one to be generated has two answers, and choosing either
would silently discard what the caller sent.

> **REQ-VAL-021** — The agent MUST reject `external = ALLOW` combined with
> `nat.enable_uplink_forwarding = false` with `FORWARD_POLICY_NEEDS_UPLINK`.

The declaration is self-contradictory: policy permits egress while the kernel precondition
for egress is absent.

> **REQ-VAL-022** — The agent MUST reject `allowed_peer_interfaces` naming a nonexistent
> interface with `PEER_INTERFACE_NOT_FOUND`.

A typo here causes silent loss of connectivity, so it is caught at write time.

## 4. Warning severity

> **REQ-VAL-030** — The agent MUST warn with `ALLOWED_IPS_OVERLAP` when an `allowed_ips` entry
> overlaps, at a differing prefix length, an entry of the same or another peer of the interface.

Longest-prefix matching makes this valid, but it usually indicates a mistake.

> **REQ-VAL-031** — The agent MUST warn with `ALLOWED_IPS_OUT_OF_SUBNET` when a peer's
> `allowed_ips` entry falls outside every one of the interface's subnets.

Valid for site-to-site, usually a mistake otherwise.

> **REQ-VAL-032** — The agent MUST warn with `MTU_OUT_OF_RANGE` when `mtu` falls outside the
> range 1280 to 1500.

> **REQ-VAL-033** — The agent MUST warn with `ENDPOINT_NOT_IP` when `endpoint` is a hostname
> rather than an IP address.

The kernel stores only the resolved address, so a DNS change does not propagate.

> **REQ-VAL-023** — The agent MUST warn with `INTER_INTERFACE_ONE_SIDED` when an
> `inter_interface = ALLOW_LIST` relationship is declared in one direction only.

Valid and stateful per `REQ-FWD-017`, but usually a forgotten reciprocal declaration.

> **REQ-VAL-034** — The agent MUST warn with `ALLOWED_PEER_INTERFACES_IGNORED` when
> `allowed_peer_interfaces` is non-empty while `inter_interface != ALLOW_LIST`.

The field is ignored in that combination, which usually reflects a misunderstanding.

> **REQ-VAL-035** — The agent MUST warn with `EXTERNAL_WITHOUT_NAT` when `external = ALLOW`
> is combined with `nat.enabled = false`.

Traffic leaves without source NAT, so return traffic almost certainly has no route back.
Syntactically valid, practically broken.

## 5. Summary

| Rule | REQ | Severity |
|---|---|---|
| Invalid interface name | REQ-VAL-010 | Error |
| Malformed public key | REQ-VAL-011 | Error |
| Duplicate `allowed_ips` | REQ-VAL-012 | Error |
| Duplicate `listen_port` | REQ-VAL-013 | Error |
| Overlapping `addresses` | REQ-VAL-014 | Error |
| Create naming an existing link | REQ-VAL-015 | Error |
| Empty `addresses` | REQ-VAL-016 | Error |
| Empty `allowed_ips` | REQ-VAL-017 | Error |
| IPv6 address | REQ-VAL-020 | Error |
| Entry that is not a CIDR | REQ-VAL-047 | Error |
| Port outside 1 to 65535 | REQ-VAL-036 | Error |
| Keepalive above 65535 | REQ-VAL-037 | Error |
| MTU outside 68 to 65535 | REQ-VAL-038 | Error |
| Malformed private or preshared key | REQ-VAL-039 | Error |
| Peer carrying the interface's own key | REQ-VAL-040 | Error |
| Host bits in `allowed_ips` | REQ-VAL-041 | Error |
| Malformed `endpoint` | REQ-VAL-042 | Error |
| Generated peer with no address in the subnets | REQ-VAL-043 | Error |
| Default route in `allowed_ips` | REQ-VAL-044 | Error |
| Line break in a hook | REQ-VAL-045 | Error |
| No free address for a generated peer | REQ-VAL-046 | Error |
| A key supplied and generated at once | REQ-VAL-048, REQ-VAL-049 | Error |
| `external` without uplink forwarding | REQ-VAL-021 | Error |
| Unknown `allowed_peer_interfaces` entry | REQ-VAL-022 | Error |
| One-sided `inter_interface` | REQ-VAL-023 | Warning |
| Overlapping `allowed_ips` at differing prefixes | REQ-VAL-030 | Warning |
| `allowed_ips` outside the subnet | REQ-VAL-031 | Warning |
| MTU outside the usual range | REQ-VAL-032 | Warning |
| Hostname `endpoint` | REQ-VAL-033 | Warning |
| Redundant `allowed_peer_interfaces` | REQ-VAL-034 | Warning |
| `external` without NAT | REQ-VAL-035 | Warning |
