> ⛔ **SUPERSEDED — 2026-08-04**
>
> Not to be used for decisions or implementation. The content has been split into
> [20-spec](../20-spec/), [10-decisions](../10-decisions/) and [40-concepts](../40-concepts/).
> Retained for historical reference only.
>
> Translated from the Vietnamese original on 2026-08-04 with no change to its content.

---

# wg-agent — Technical Specification v1

> Status: **Draft** · Origin: `wireframe-very-first.md`, the initial concept sketch, which is
> no longer present in the repository.
> This document replaced the wireframe as the source of truth for the design.

---

## 0. Founding decisions (settled)

| # | Decision | Choice |
|---|---|---|
| D1 | API model | **Declarative with a reconcile loop** — a desired state store that survives reboots |
| D2 | Network layer | **Native netlink and wgctrl**, NAT through `google/nftables` (opt-in). No `exec.Command` |
| D3 | Transport | **Protobuf → gRPC and grpc-gateway** (REST/OpenAPI generated automatically) |
| D4 | Peer private keys | **BYOK by default**, with a server-generated mode returning the private key **exactly once** |
| D5 | Address family | **IPv4 only in v1.** IPv6 is rejected explicitly rather than silently ignored |
| D6 | Forward policy | Three-axis `ForwardPolicySpec`. Default **flat LAN per group**: peers on one interface see each other, no crossing to other interfaces, no internet egress |

---

## 1. Positioning and scope

### 1.1. Positioning

`wg-agent` is a **node-level control plane agent** for WireGuard on Linux. It turns the
WireGuard state of **one machine** into an API-addressable resource under a declarative model.

The critical boundary: the agent manages **one node**. Orchestration across nodes is the
responsibility of the platform layer above it.

```
┌─────────────────────────────────────────────┐
│  Platform / SaaS / Panel / Terraform / K8s  │  ← Org, user, billing, RBAC, audit, IPAM
└──────────────────────┬──────────────────────┘
                       │ gRPC / REST (mTLS)
        ┌──────────────┼──────────────┐
        ▼              ▼              ▼
   ┌─────────┐    ┌─────────┐    ┌─────────┐
   │wg-agent │    │wg-agent │    │wg-agent │   ← 1 agent per node
   │ node-a  │    │ node-b  │    │ node-c  │
   └─────────┘    └─────────┘    └─────────┘
```

### 1.2. In scope

- Interface lifecycle: create, delete, up/down, addresses, MTU, listen port, fwmark
- Peer lifecycle: create, update, delete, list, get
- Routes on the interface matching AllowedIPs
- Key generation and rotation; public key import
- Runtime state read from the kernel (handshake, transfer, endpoint)
- Local desired state storage with automatic reconciliation
- Client config (`.conf`) and QR code generation, with `AllowedIPs` derived from policy
- Three-axis forward policy through nftables: peer-to-peer, interface-to-interface, egress
- NAT/masquerade through nftables (opt-in)
- Metrics, health, audit log

**IPv4 only.** IPv6 is outside the v1 scope and is rejected explicitly during validation.

### 1.3. Out of scope (never done here)

| Not done | Belongs to |
|---|---|
| VPN user authentication | Platform |
| User, organization and tenant management | Platform |
| Billing, quota | Platform |
| UI, dashboard | Platform |
| RBAC, IAM, business ACL | Platform |
| IPAM (allocating peer addresses) | Platform — the agent only **validates** |
| Multi-node orchestration, mesh topology | Platform |
| Business database | Platform |
| `PostUp` / `PostDown` shell hooks | **Permanently prohibited** — see §7.5 |
| Policy routing and fwmark for full-tunnel clients | Client-side concern |
| Host DNS management (`resolvconf`) | Client-side concern |
| **IPv6** | Deferred — see §12. v1 **rejects explicitly**, never silently |
| Userspace WireGuard (`boringtun`, `wireguard-go`) | Deferred — see §12 |
| Network namespaces | Deferred — see §12 |
| Overlapping subnets across interfaces (multi-tenant duplicate addressing) | Requires network namespaces — see §12 |

---

## 2. Architecture

### 2.1. Layer diagram

```
                    ┌───────────────────────────────┐
                    │   gRPC  :  Unix socket / mTLS │
                    │   REST  :  grpc-gateway       │
                    └───────────────┬───────────────┘
                                    │
                    ┌───────────────▼───────────────┐
                    │        API Layer              │
                    │  validate · authz · audit     │
                    └───────────────┬───────────────┘
                                    │
        ┌───────────────────────────▼───────────────────────────┐
        │                    Core Service                       │
        │                                                       │
        │   ┌──────────────┐        ┌─────────────────────┐     │
        │   │ Desired State│◄──────►│  Reconcile Engine   │     │
        │   │   (bbolt)    │        │  diff · apply · retry│    │
        │   └──────────────┘        └──────────┬──────────┘     │
        └──────────────────────────────────────┼────────────────┘
                                               │
        ┌──────────────────────────────────────▼────────────────┐
        │                  Platform Layer                       │
        │                                                       │
        │  ┌─────────┐  ┌──────────┐  ┌────────┐  ┌──────────┐  │
        │  │ wgctrl  │  │ netlink  │  │ nft    │  │ keys     │  │
        │  │ device  │  │ link/addr│  │ (opt)  │  │curve25519│  │
        │  │ peers   │  │ route    │  │ NAT    │  │          │  │
        │  └────┬────┘  └────┬─────┘  └───┬────┘  └──────────┘  │
        └───────┼────────────┼────────────┼────────────────────┘
                │            │            │
                ▼            ▼            ▼
        ┌───────────────────────────────────────────┐
        │              Linux Kernel                 │
        │   wireguard module · rtnetlink · nf_tables│
        └───────────────────────────────────────────┘
```

### 2.2. Library responsibilities

The original wireframe left this unclear. The precise boundary:

| Operation | Library | Specific API |
|---|---|---|
| Create / delete interface | `vishvananda/netlink` | `LinkAdd(&netlink.Wireguard{})` / `LinkDel` |
| Up / Down | `vishvananda/netlink` | `LinkSetUp` / `LinkSetDown` |
| Add / remove addresses | `vishvananda/netlink` | `AddrAdd` / `AddrDel` / `AddrList` |
| MTU | `vishvananda/netlink` | `LinkSetMTU` |
| Routes for AllowedIPs | `vishvananda/netlink` | `RouteAdd` / `RouteDel` / `RouteList` |
| Link event subscription | `vishvananda/netlink` | `LinkSubscribe` |
| PrivateKey, ListenPort, FwMark | `wgctrl-go` | `ConfigureDevice` |
| Add / update / remove peers | `wgctrl-go` | `ConfigureDevice` + `PeerConfig` |
| Read device and statistics | `wgctrl-go` | `Device()` / `Devices()` |
| Key generation | `wgctrl-go/wgtypes` | `GeneratePrivateKey` / `GenerateKey` |
| NAT and forwarding (opt-in) | `google/nftables` | dedicated `inet wg_agent` table |

**Invariant:** no `exec.Command` anywhere in the execution path. Permitted in test helpers.

### 2.3. Repository layout

```
cmd/wg-agent/              # entrypoint, flags, wiring
api/proto/wgagent/v1/      # .proto — source of truth for the API
gen/                       # generated code (gRPC, gateway, OpenAPI) — committed
internal/
  server/                  # gRPC server, gateway, listeners, TLS
  auth/                    # mTLS peer identity, principal extraction
  audit/                   # audit log writer
  service/                 # business logic: interface, peer, key, config
  store/                   # bbolt desired state, schema migration
  reconcile/               # reconcile engine, work queue, backoff
  platform/
    wg/                    # wgctrl adapter
    link/                  # netlink adapter (link, addr, route)
    nft/                   # nftables module (opt-in)
  wgconfig/                # .conf and QR code generation
  metrics/                 # Prometheus collectors
  validate/                # spec validation, conflict detection
  errors/                  # error model, reason codes
packaging/
  systemd/                 # unit file, sysusers, tmpfiles
  debian/                  # nfpm config → .deb
docs/
```

---

## 3. Resource model

### 3.1. Principles

- Every resource separates **`spec`** (desired state, written by callers) from **`status`**
  (observed state, read-only, sourced from the kernel).
- `Interface` and `Peer` are **two separate collections**, not nested in one spec.
  *Reason:* this lets Terraform or an operator manage individual peers without contesting
  ownership with the interface spec. Atomic replacement of a whole peer set uses
  `BatchUpdatePeers` (§4.4).
- Every write operation is **idempotent**.

### 3.2. Interface

**Identity:** `name`, for example `wg0` or `wg-prod`.

Name constraint: matches `^[a-zA-Z][a-zA-Z0-9_-]{0,14}$`. Maximum 15 characters because Linux
`IFNAMSIZ` is 16, including the NUL terminator.

#### `InterfaceSpec`

| Field | Type | Required | Notes |
|---|---|---|---|
| `name` | string | ✅ | Immutable after creation |
| `private_key` | string (base64) | ❌ | Omitted → the agent generates one. **Never readable afterwards** |
| `listen_port` | uint32 | ❌ | `0` → the kernel chooses. The real port appears in `status` |
| `addresses` | []string (CIDR) | ✅ | Addresses assigned to the interface, e.g. `["10.10.0.1/24"]`. **IPv4 only in v1** — IPv6 CIDRs are rejected |
| `mtu` | uint32 | ❌ | Default `1420` |
| `fwmark` | uint32 | ❌ | Default `0` (disabled) |
| `manage_routes` | bool | ❌ | Default `true`. Setting `false` matches `Table=off` in wg-quick |
| `forward_policy` | `ForwardPolicySpec` | ❌ | See §3.4. Default: flat LAN per group |
| `nat` | `NatSpec` | ❌ | See §3.5. Disabled by default |
| `enabled` | bool | ❌ | Default `true`. `false` keeps the link present but DOWN |
| `labels` | map<string,string> | ❌ | Free-form metadata for the platform layer. The agent does not interpret it |

#### `InterfaceStatus`

| Field | Source |
|---|---|
| `public_key` | Derived from the private key |
| `listen_port` | The port the kernel has bound |
| `instance_id` | UUID generated when **the link is created**. A change means counters reset (§5.4) |
| `created_at` | When the link was created |
| `oper_state` | `UP` / `DOWN` / `ABSENT` |
| `peer_count` | Peer count in the kernel |
| `observed_generation` | The spec generation that finished reconciling |
| `condition` | `READY` / `PROGRESSING` / `DEGRADED` plus `reason` and `message` |
| `last_reconcile_at` | Timestamp |

### 3.3. Peer

**Identity:** the pair `(interface_name, public_key)`. The public key is the natural key — the
kernel itself uses it as the identifier.

#### `PeerSpec`

| Field | Type | Required | Notes |
|---|---|---|---|
| `interface_name` | string | ✅ | Immutable |
| `public_key` | string (base64) | ✅ | Immutable. Changing the key means delete plus create |
| `preshared_key` | string (base64) | ❌ | Write-only |
| `allowed_ips` | []string (CIDR) | ✅ | Cryptokey routing |
| `endpoint` | string | ❌ | `host:port`. Usually empty in the hub model |
| `persistent_keepalive` | uint32 (seconds) | ❌ | `0` disables. `25` recommended for peers behind NAT |
| `labels` | map<string,string> | ❌ | Free-form metadata |

#### `PeerStatus`

| Field | Notes |
|---|---|
| `last_handshake_at` | **Null when no handshake has occurred** — not epoch 0 |
| `handshake_age_seconds` | Null when no handshake has occurred |
| `online` | Normalized heuristic: `handshake_age < 180s` (§5.3) |
| `rx_bytes` / `tx_bytes` | Cumulative counters. **Reset when the link is recreated** (§5.4) |
| `resolved_endpoint` | The endpoint the kernel holds — may differ from the spec when the peer initiates |
| `protocol_version` | From the kernel |

### 3.4. ForwardPolicySpec

Forward policy is a **first-class resource**, separate from NAT. Reason: this answers "who may
talk to whom", whereas NAT answers "how is the source address rewritten". They are different
questions and are usually configured independently.

#### Three axes

| Field | Type | Default | Meaning |
|---|---|---|---|
| `intra_interface` | `ALLOW` \| `DENY` | **`ALLOW`** | Peer to peer within the **same** interface |
| `inter_interface` | `ALLOW` \| `DENY` \| `ALLOW_LIST` | **`DENY`** | To a **different** managed WireGuard interface |
| `allowed_peer_interfaces` | []string | `[]` | Only meaningful when `inter_interface = ALLOW_LIST` |
| `external` | `ALLOW` \| `DENY` | **`DENY`** | Egress through a non-WireGuard interface (the uplink) |

The defaults correspond to the **flat LAN per group** pattern (§3.6): each interface is an
independent LAN whose members see each other, with no leakage to other groups and no automatic
internet access.

#### `ALLOW` semantics — read this carefully

The agent owns only the `inet wg_agent` table and does **not** touch the host firewall. In
nftables, base chains on the same hook are evaluated in sequence: a `drop` verdict in any chain
**terminates evaluation and discards the packet**, while `accept` only ends that chain and
**continues** to the next. The consequence is asymmetric:

| Value | Agent action | Guarantee |
|---|---|---|
| `DENY` | Adds a `drop` rule | ✅ **Blocking is certain** |
| `ALLOW` | **Adds no rule** | ⚠️ Only that *the agent does not block*. `ufw`, `firewalld` or Docker may still block |

`ALLOW` must be read as **"the agent does not block"**, not as **"traffic is guaranteed to
pass"**. This is the direct price of the decision not to seize the host firewall — accepted
deliberately, and it must be stated clearly in user documentation.

#### Asymmetry and connection state

Rules are **stateful**: the agent's chain always opens with
`ct state established,related accept`. Policy therefore applies only to **new connections**:

> When `wg0` permits `wg1` but `wg1` does not permit `wg0`, `wg0` can initiate connections and
> replies return normally, while `wg1` cannot initiate towards `wg0`.

This is intended behavior, not a defect. Two-way communication requires declaring the
relationship on both interfaces.

#### Sysctl: minimal scope

The kernel decides forwarding from the `forwarding` sysctl of the **input** interface rather
than from a global variable. The agent exploits this to minimize its footprint on the host:

| Flow | Outbound needs | Return needs | Touches uplink? |
|---|---|---|---|
| Peer to peer on `wg0` | `conf.wg0.forwarding=1` | `conf.wg0.forwarding=1` | ❌ **No** |
| `wg0` to `wg1` | `conf.wg0.forwarding=1` | `conf.wg1.forwarding=1` | ❌ **No** |
| Peer to internet | `conf.wg0.forwarding=1` | `conf.eth0.forwarding=1` | ✅ **Yes** |

Agent rules:

- `intra_interface = ALLOW` or `inter_interface ≠ DENY` → the agent sets
  `net.ipv4.conf.<wgN>.forwarding=1` **only on the WireGuard interfaces it created**. Those are
  resources the agent fully owns, so no warning and no separate opt-in is required.
- `external = ALLOW` → forwarding on the uplink is **mandatory**. That reaches outside the
  agent's ownership, so it requires an explicit `nat.enable_uplink_forwarding = true`, a `WARN`
  log entry and an audit record. Without the flag, the spec is rejected with
  `FORWARD_POLICY_NEEDS_UPLINK`.

The agent records the original sysctl value and restores it when the interface is removed,
**except** on the uplink, which is left alone because other host components may depend on it.

### 3.5. NatSpec (opt-in)

When enabled, the agent creates and owns **only** the `inet wg_agent` table. It touches no other
table, so it coexists safely with `ufw`, `firewalld` and Docker.

| Field | Type | Default | Notes |
|---|---|---|---|
| `enabled` | bool | `false` | Enables masquerade |
| `masquerade_out_interface` | string | — | Egress interface, e.g. `eth0`. Empty → derived from the default route |
| `enable_uplink_forwarding` | bool | `false` | Writes `net.ipv4.conf.<uplink>.forwarding`. Mandatory when `forward_policy.external = ALLOW` |

> `enable_uplink_forwarding` reaches **outside the agent's ownership**. It must be declared
> explicitly, logged at WARN, recorded in the audit log, and never restored automatically when
> the interface is removed.

### 3.6. Connectivity model (operational reference)

This section defines no new API. It describes how the resources above combine into real network
behavior — the part most often misunderstood in operation.

#### Reference topology

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
**unnecessary** — only the port differs.

#### Five traffic flows

| # | Flow | Chain | Governed by |
|---|---|---|---|
| 1 | Peer to server (`10.10.0.1`) | INPUT | **Always passes.** Not forwarding |
| 2 | Peer to peer on `wg0` | FORWARD `wg0→wg0` | `intra_interface` |
| 3 | `wg0` to `wg1` | FORWARD `wg0→wg1` | `inter_interface` |
| 4 | Peer to internet | FORWARD `wg0→eth0` plus NAT | `external` and `nat` |
| 5 | Peer to a LAN behind the server | FORWARD `wg0→eth0` | `external` plus a static host route |

Flow 1 deserves attention: a peer can **always** reach services on the server itself, even with
all three axes locked. Blocking it requires a host firewall rule, which is outside the agent's
scope by design.

#### Two places decide whether traffic passes

WireGuard's `AllowedIPs` filters in **both directions**: it decides which packets enter the
tunnel and it **discards** inbound packets whose `src` is absent from the sending peer's
`AllowedIPs`.

| Location | Role | Controlled by | A security boundary? |
|---|---|---|---|
| Client-side `AllowedIPs` | Routing intent | The client, freely editable | ❌ **Untrustworthy** |
| Server-side `allowed_ips` | Cryptokey routing | The agent | ⚠️ Blocks `src` spoofing, **not** peer-to-peer traffic |
| `forward_policy` (nftables) | Enforcement | The agent | ✅ **The actual boundary** |

> **Narrowing server-side `allowed_ips` does NOT isolate peers.** Peer A with
> `allowed_ips = 10.10.0.5/32` can still send `src=10.10.0.5 dst=10.10.0.6`: the server accepts
> it because `src` matches, then routes it to B. Real isolation **requires** a FORWARD rule.

#### Example: letting A on wg0 reach B on wg1

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

The counter-intuitive point: **server-side `allowed_ips` needs no change.** The server already
has a route to `10.20.0.0/24 dev wg1` from wg1's own address. What must widen is the **client**
config — something the agent only *generates* (§4.7), never *enforces*.

Forgetting to widen client-side `AllowedIPs` is the leading cause of the symptom *"policy is set
to ALLOW but nothing gets through"*. That is precisely why `ClientRouting.AUTO` (§4.7) exists.

#### Four operational patterns

| Pattern | `intra` | `inter` | `external` | Use when |
|---|---|---|---|---|
| Isolated hub | `DENY` | `DENY` | `DENY` | Clients reach only services on the server. Bastion, internal apps |
| **Flat LAN per group** ← **default** | **`ALLOW`** | **`DENY`** | **`DENY`** | Each interface is an independent LAN. Teams, separate customers |
| Segmented with a bridge | `ALLOW` | `ALLOW_LIST` | `DENY` | Staff reach prod; contractors stay isolated |
| VPN gateway | `DENY` | `DENY` | `ALLOW` plus NAT | Exit node, internet egress through the server |

The *flat LAN per group* default was chosen because it matches the natural expectation when an
operator creates one interface and adds several peers. The trade-off: this default is **not**
deny-all — absolute isolation requires declaring `intra_interface: DENY` explicitly. The other
two axes remain `DENY`, so nothing leaks beyond the interface.

#### Several interfaces or one shared interface?

| | Several interfaces | One interface, split by subnet |
|---|---|---|
| Isolation | By `iif`/`oif` — one rule, unambiguous | By address — many rules, error-prone |
| UDP ports | One per interface | A single port |
| Server keys | One per interface, rotated independently | Rotation disconnects everyone |
| Blast radius | Losing `wg1` leaves `wg0` intact | One failure affects everything |
| Client config | Must target the right port | Simpler |

**Recommendation:** split interfaces along **trust boundaries** (tenant, environment, privilege
level); within one boundary, share an interface and partition by subnet. Dozens of interfaces on
a node is reasonable; thousands is not.

**A constraint to know upfront:** §9 requires interface subnets **not to overlap**. `wg0` and
`wg1` cannot both use `10.10.0.0/24`. Multi-tenancy with duplicate addressing requires network
namespaces — §12, outside the v1 scope.

---

## 4. API

### 4.1. Principles

- The source of truth is `api/proto/wgagent/v1/*.proto`. gRPC code, the gateway and OpenAPI are
  all generated from it with `buf`.
- Package `wgagent.v1`. Versioning follows the package; a breaking change moves to `v2`.
- REST paths are declared with `google.api.http` annotations.

### 4.2. Service surface

```protobuf
service InterfaceService {
  rpc CreateInterface (CreateInterfaceRequest) returns (Interface);
  rpc GetInterface    (GetInterfaceRequest)    returns (Interface);
  rpc ListInterfaces  (ListInterfacesRequest)  returns (ListInterfacesResponse);
  rpc UpdateInterface (UpdateInterfaceRequest) returns (Interface);  // PATCH, with field_mask
  rpc ReplaceInterface(ReplaceInterfaceRequest)returns (Interface);  // PUT, upsert
  rpc DeleteInterface (DeleteInterfaceRequest) returns (google.protobuf.Empty);
  rpc RotateInterfaceKey (RotateInterfaceKeyRequest) returns (RotateInterfaceKeyResponse);
}

service PeerService {
  rpc CreatePeer      (CreatePeerRequest)      returns (CreatePeerResponse);
  rpc GetPeer         (GetPeerRequest)         returns (Peer);
  rpc ListPeers       (ListPeersRequest)       returns (ListPeersResponse);
  rpc UpdatePeer      (UpdatePeerRequest)      returns (Peer);
  rpc ReplacePeer     (ReplacePeerRequest)     returns (Peer);
  rpc DeletePeer      (DeletePeerRequest)      returns (google.protobuf.Empty);
  rpc BatchUpdatePeers(BatchUpdatePeersRequest) returns (BatchUpdatePeersResponse);
}

service RuntimeService {
  rpc GetInterfaceStatus (GetInterfaceStatusRequest) returns (InterfaceStatus);
  rpc ListPeerStatus     (ListPeerStatusRequest)     returns (ListPeerStatusResponse);
  rpc WatchPeerStatus    (WatchPeerStatusRequest)    returns (stream PeerStatusEvent); // gRPC only
}

service ConfigService {
  rpc GenerateClientConfig (GenerateClientConfigRequest) returns (GenerateClientConfigResponse);
  rpc GenerateKeyPair      (GenerateKeyPairRequest)      returns (GenerateKeyPairResponse);
}

service SystemService {
  rpc GetHealth  (google.protobuf.Empty) returns (HealthResponse);
  rpc GetVersion (google.protobuf.Empty) returns (VersionResponse);
  rpc Reconcile  (ReconcileRequest)      returns (ReconcileResponse); // force an immediate pass
}
```

### 4.3. REST mapping

| Method | Path | RPC |
|---|---|---|
| `POST` | `/v1/interfaces` | CreateInterface |
| `GET` | `/v1/interfaces` | ListInterfaces |
| `GET` | `/v1/interfaces/{name}` | GetInterface |
| `PUT` | `/v1/interfaces/{name}` | ReplaceInterface |
| `PATCH` | `/v1/interfaces/{name}` | UpdateInterface |
| `DELETE` | `/v1/interfaces/{name}` | DeleteInterface |
| `POST` | `/v1/interfaces/{name}:rotateKey` | RotateInterfaceKey |
| `GET` | `/v1/interfaces/{name}/status` | GetInterfaceStatus |
| `POST` | `/v1/interfaces/{name}/peers` | CreatePeer |
| `GET` | `/v1/interfaces/{name}/peers` | ListPeers |
| `GET` | `/v1/interfaces/{name}/peers/{public_key}` | GetPeer |
| `PUT` | `/v1/interfaces/{name}/peers/{public_key}` | ReplacePeer |
| `PATCH` | `/v1/interfaces/{name}/peers/{public_key}` | UpdatePeer |
| `DELETE` | `/v1/interfaces/{name}/peers/{public_key}` | DeletePeer |
| `POST` | `/v1/interfaces/{name}/peers:batchUpdate` | BatchUpdatePeers |
| `POST` | `/v1/interfaces/{name}/peers/{public_key}:generateConfig` | GenerateClientConfig |
| `GET` | `/v1/healthz` · `/v1/readyz` | GetHealth |

> `public_key` in a path uses **unpadded base64url** to stay URL-safe.

### 4.4. BatchUpdatePeers

For operators and Terraform needing atomic synchronization of an entire peer set.

```protobuf
message BatchUpdatePeersRequest {
  string interface_name = 1;
  repeated PeerSpec peers = 2;
  // true  → peers absent from the list are deleted (absolute synchronization)
  // false → upsert only, nothing deleted
  bool replace_all = 3;
  string interface_revision = 4;  // optimistic concurrency
}
```

Semantics: all changes occur inside **one store transaction** and **one `ConfigureDevice` call**.
Either everything succeeds or nothing changes.

### 4.5. Concurrency and idempotency

- Every resource carries a `revision`, an opaque string that changes whenever the spec changes.
- gRPC: a `revision` field in the request. REST: the `If-Match` and `ETag` headers.
- A mismatched revision returns `FAILED_PRECONDITION` (HTTP `412`).
- An omitted revision overwrites unconditionally — permitted, though machine clients should
  always send one.
- In-process: a per-interface mutex. All operations on `wg0` are serialized.
- **Never** use wgctrl's `ReplacePeers: true` except in exactly two places:
  `BatchUpdatePeers{replace_all=true}` and a full reconcile.

### 4.6. Write semantics: synchronous

Every write is **applied synchronously** before returning:

```
validate spec
  → write desired state (bbolt txn)
  → apply to the kernel  (default timeout 10s)
  → re-read status from the kernel
  → return the resource with its status
```

On application failure the desired state is **retained** — it represents the caller's intent —
and the returned resource carries `condition = DEGRADED` with a reason under HTTP
`202 Accepted`. The reconcile loop keeps retrying.

*Why synchronous:* Terraform providers need the outcome immediately. The reconcile loop is a
safety net, not the primary execution path.

### 4.7. GenerateClientConfig and `ClientRouting`

Client-side `AllowedIPs` determines which traffic a client sends into the tunnel and which
inbound traffic it accepts (§3.6). Computing it incorrectly causes silent loss of connectivity.
The agent therefore **derives** the value from `forward_policy` rather than leaving each caller
to recompute it.

```protobuf
enum ClientRouting {
  CLIENT_ROUTING_AUTO         = 0;  // derived from forward_policy — default
  CLIENT_ROUTING_SERVER_ONLY  = 1;  // the server address only
  CLIENT_ROUTING_LOCAL_SUBNET = 2;  // the interface's own subnet
  CLIENT_ROUTING_FULL_TUNNEL  = 3;  // 0.0.0.0/0
  CLIENT_ROUTING_CUSTOM       = 4;  // exactly extra_allowed_ips
}

message GenerateClientConfigRequest {
  string        interface_name    = 1;
  string        public_key        = 2;
  ClientRouting routing           = 3;
  repeated string extra_allowed_ips = 4;  // always added, except in CUSTOM mode
  string        endpoint_override = 5;    // public hostname or address the client connects to
  repeated string dns             = 6;    // written into the .conf only; host DNS untouched
  bool          include_qr_code   = 7;
}
```

#### `AUTO` derivation rule

```
always                         → <interface server address>/32
intra_interface = ALLOW        → + the interface's own subnet
inter_interface = ALLOW        → + the subnet of EVERY other managed interface
inter_interface = ALLOW_LIST   → + the subnets of allowed_peer_interfaces
external        = ALLOW        → + 0.0.0.0/0
```

When `0.0.0.0/0` is present, narrower entries it subsumes are omitted.

The response always carries `derived_allowed_ips` and `routing_explanation`, a string describing
how the result was reached, so the platform can show it back to the user for diagnosis.

> **Note:** the generated `AllowedIPs` is **configuration advice**, not enforcement. A client can
> widen it freely. The only security boundary is server-side `forward_policy` (§3.6).

---

## 5. Desired state and reconcile

This is the part the original wireframe omitted entirely, and it is what distinguishes a control
plane from an RPC shim.

### 5.1. Store

- Engine: **bbolt** (pure Go, no cgo, single file, transactional).
- Path `/var/lib/wg-agent/state.db`, mode `0600`, owned by `wg-agent`.
- Buckets: `interfaces`, `peers`, `meta` (schema version).
- Stores only **spec** values and `instance_id`. **Never** status or counters — status is always
  read directly from the kernel.
- Schema migration is versioned and runs at startup.

**On private keys:** an interface private key must be stored to rebuild the interface after a
reboot. Protection is file mode `0600` plus a dedicated account. That is proportionate to the
risk: anyone holding `CAP_NET_ADMIN` or root on the machine can read the key from the kernel
directly. Envelope encryption through an external KMS is discussed in §12.

### 5.2. Reconcile engine

**Reconcile triggers:**

| Source | Scope |
|---|---|
| After every API write | The affected interface |
| Agent startup | All interfaces |
| Periodic timer (default 30s, configurable) | All interfaces |
| Netlink `LinkSubscribe` reporting a deleted or downed link | The affected interface |
| Explicit `Reconcile` RPC | As requested |

**Algorithm, for each interface in desired state:**

```
1. Link present?            → no: LinkAdd
2. Type is wireguard?       → no: DEGRADED, leave it untouched (safe)
3. Device config matches?   → no: ConfigureDevice (delta, UpdateOnly)
4. Peer set matches?        → diff by public key:
                                present in kernel only → Remove
                                missing                → Add
                                differs from spec      → Update (delta)
5. Addresses match?         → AddrAdd / AddrDel per diff
6. MTU matches?             → LinkSetMTU
7. manage_routes = true?    → sync routes with the union of allowed_ips
8. spec.enabled?            → LinkSetUp / LinkSetDown
9. Forwarding sysctl match? → conf.<wgN>.forwarding per forward_policy (§3.4)
10. nftables rules match?   → sync chains in table inet wg_agent:
                               forward_policy → drop rules
                               nat.enabled    → masquerade rule
11. Write status and observed_generation
```

**Foreign interfaces:** a WireGuard interface present on the host but **absent** from desired
state is **not deleted** by the agent. It is reported in `ListInterfaces` with
`managed = false`. Deleting resources created by another party is unacceptable behavior for an
agent.

**Backoff:** on failure, exponential backoff from 1s to 60s with jitter. Error state is exposed
through `condition` and metrics.

### 5.3. Normalizing peer "online"

The kernel has no notion of a peer being online. The agent normalizes it so every caller agrees:

```
online = last_handshake != null && (now - last_handshake) < 180s
```

180s is three times `rekey-after-time` (120s), tolerating one missed handshake cycle. It is
configurable through `peer_online_threshold`. Raw `handshake_age_seconds` is always exposed
alongside so the platform can apply its own rule.

### 5.4. Counter reset detection

Kernel `rx_bytes` and `tx_bytes` are cumulative **from link creation**. Recreating a link — after
a reboot, or when reconcile rebuilds a deleted interface — resets them to zero.

The agent exposes `interface.status.instance_id`, a UUID generated on every successful
`LinkAdd`. The platform computes traffic deltas by the rule:

```
instance_id unchanged → delta = new - old
instance_id changed   → delta = new        (counters reset)
```

The agent does **not** accumulate traffic itself. That is business data belonging to the platform
layer (§1.3).

---

## 6. Key management

### 6.1. Interface keys

- Omitting `private_key` at creation makes the agent generate one with
  `wgtypes.GeneratePrivateKey()`.
- A private key **never** appears in any response. `GetInterface` returns only
  `status.public_key`.
- `RotateInterfaceKey` generates a new key and applies it atomically, returning the new public
  key.

> ⚠️ Rotating an interface key **disconnects every peer** until each is updated with the server's
> new public key. The agent returns an explicit warning in the response; coordinating the peer
> updates is the platform layer's responsibility.

### 6.2. Peer keys — two modes

**Mode A — BYOK (default, recommended)**

```
CreatePeerRequest { public_key: "...", allowed_ips: [...] }
```
The client generates the keypair and sends only the public key. The agent **never** learns the
private key. This is WireGuard's correct security model.

**Mode B — Server-generated (convenience)**

```
CreatePeerRequest { generate_keypair: true, allowed_ips: [...] }
→ CreatePeerResponse { peer: {...}, generated_private_key: "..." }   // EXACTLY ONCE
```

Mandatory constraints:
- The generated private key is **never stored** — not in the store, the log or the audit log.
- It exists only in that one response. No API retrieves it again.
- It works only over an encrypted transport (mTLS or unix socket). Over plaintext the request is
  rejected with `FAILED_PRECONDITION`.
- A global flag `security.allow_server_generated_keys` (default `true`) lets hardened
  environments disable the mode entirely.

### 6.3. Preshared keys

Write-only. The agent can generate one (`generate_preshared_key: true`) and return it once under
the same mechanism as Mode B.

---

## 7. Security model

The agent runs with `CAP_NET_ADMIN` and can create tunnels into an internal network. An exposed
endpoint is equivalent to a compromised network. This section states requirements, not options.

### 7.1. Default listener

| Priority | Listener | Auth |
|---|---|---|
| Default | Unix socket `/run/wg-agent/wg-agent.sock`, mode `0660`, group `wg-agent` | Filesystem permissions plus `SO_PEERCRED` |
| Optional | TCP with TLS | **mTLS mandatory** |
| Discouraged | TCP plaintext | Only behind an explicit flag, bound to loopback |

- **No** default configuration binds `0.0.0.0`.
- TCP plaintext requires both `--insecure-no-auth` **and** a loopback bind address. It logs WARN
  every 60s while active and refuses to start on a non-loopback address.

### 7.2. mTLS

- The agent holds a server certificate and key plus a CA bundle for verifying clients.
- TLS 1.3 minimum.
- Client identity is the **SPIFFE URI SAN** when present, otherwise the Subject CN.
- Optional `security.allowed_client_identities` restricts access. Empty means any certificate
  signed by the trusted CA is accepted.
- Certificate reload on `SIGHUP` supports short-lived certificate rotation.

### 7.3. Authorization

v1 uses a **two-role** model mapped from client identity:

| Role | Permitted |
|---|---|
| `reader` | Read-only RPCs: Get, List, Status, Watch, Health |
| `admin` | All operations |

Configured through an explicit identity-to-role mapping. Finer-grained RBAC belongs to the
platform layer (§1.3).

### 7.4. Least-privilege execution

The agent runs under a dedicated `wg-agent` account, **not root**, with an ambient capability:

```ini
[Service]
User=wg-agent
Group=wg-agent
AmbientCapabilities=CAP_NET_ADMIN
CapabilityBoundingSet=CAP_NET_ADMIN
NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=yes
PrivateTmp=yes
PrivateDevices=yes
ProtectKernelTunables=yes         # see the note below on forwarding sysctl
ProtectControlGroups=yes
RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6 AF_NETLINK
RestrictNamespaces=yes
LockPersonality=yes
MemoryDenyWriteExecute=yes
SystemCallArchitectures=native
ReadWritePaths=/var/lib/wg-agent /run/wg-agent /proc/sys/net/ipv4/conf
```

**On the forwarding sysctl.** `ProtectKernelTunables=yes` mounts `/proc/sys` read-only, while the
agent needs to write `net.ipv4.conf.<iface>.forwarding` (§3.4) — and with the default
`intra_interface = ALLOW` that write is the ordinary path rather than an exception.

Handling, in order of preference:

1. **Keep `ProtectKernelTunables=yes` plus `ReadWritePaths=/proc/sys/net/ipv4/conf`** — only the
   required subtree opens while other tunables stay read-only. This is the preferred option.
   **It requires verification on each target distribution's systemd version** during M2, because
   carve-out behavior for `/proc/sys` is inconsistent across releases.
2. When option 1 does not work: `ProtectKernelTunables=no`, documented plainly as a hardening
   trade-off.

The agent must verify sysctl writability **at startup** rather than during reconcile. Failure
returns `SYSCTL_WRITE_DENIED` with a message naming both options above, instead of leaving the
interface to drift into a confusing `DEGRADED` state later.

### 7.5. Prohibition on shell hooks — permanent

The API does **not** and **will never** expose `PostUp`, `PostDown`, `PreUp`, `PreDown` or any
field accepting a shell command.

Reason: an API that accepts a shell string and runs it with `CAP_NET_ADMIN` is a remote code
execution endpoint. No level of authentication makes that safe. This is an invariant design
constraint, not a temporary limitation.

### 7.6. Handling sensitive data

| Data | Rule |
|---|---|
| Interface private key | Stored at `0600`. Never returned, never logged |
| Peer private key (mode B) | Never stored. Returned once. Never logged |
| Preshared key | Stored. Never returned, never logged |
| Public key | Not sensitive. Freely logged and returned |

The logger must redact at the type level — a dedicated secret type whose `String()` returns
`[REDACTED]`, so accidental logging is impossible.

---

## 8. Error model

### 8.1. Mapping

Errors return `google.rpc.Status` with an `ErrorInfo` detail. The gateway maps to HTTP by the
standard rules.

| gRPC code | HTTP | Case |
|---|---|---|
| `INVALID_ARGUMENT` | 400 | Malformed spec |
| `PERMISSION_DENIED` | 403 | Insufficient role |
| `NOT_FOUND` | 404 | No such interface or peer |
| `ALREADY_EXISTS` | 409 | Duplicate creation |
| `FAILED_PRECONDITION` | 412 / 400 | Revision mismatch, module not loaded, port in use |
| `RESOURCE_EXHAUSTED` | 429 | Rate limit exceeded |
| `INTERNAL` | 500 | Unexpected failure |
| `UNAVAILABLE` | 503 | Netlink or kernel temporarily unresponsive |

### 8.2. Reason codes

Machines must distinguish errors without parsing strings. `ErrorInfo.reason` takes these values:

```
WG_MODULE_NOT_LOADED       KERNEL_TOO_OLD           MISSING_CAP_NET_ADMIN
INTERFACE_NAME_INVALID     INTERFACE_NOT_FOUND      INTERFACE_EXISTS
INTERFACE_NOT_MANAGED      LISTEN_PORT_IN_USE       ADDRESS_CONFLICT
PEER_NOT_FOUND             PEER_EXISTS              PUBLIC_KEY_INVALID
ALLOWED_IPS_DUPLICATE      ALLOWED_IPS_OVERLAP      ALLOWED_IPS_OUT_OF_SUBNET
REVISION_MISMATCH          RECONCILE_FAILED         PLAINTEXT_TRANSPORT_REJECTED
NFTABLES_UNAVAILABLE       STORE_CORRUPT            IPV6_NOT_SUPPORTED
FORWARD_POLICY_NEEDS_UPLINK                         PEER_INTERFACE_NOT_FOUND
SYSCTL_WRITE_DENIED
```

### 8.3. Startup checks

The agent checks the following before serving requests, failing early with a clear message:

1. Kernel 5.6 or later, or the `wireguard` module loaded or loadable
2. `CAP_NET_ADMIN` held
3. The store is writable
4. When NAT is enabled: `nf_tables` available
5. When mTLS is enabled: certificate, key and CA readable and unexpired

`/readyz` reports OK only when all checks pass **and** the first reconcile pass has completed.

---

## 9. Validation

The agent performs no IPAM, but it **must** catch configurations that cause hard-to-diagnose
incidents:

| Check | Severity | Reason |
|---|---|---|
| Identical `allowed_ips` between two peers on one interface | **Error** | Ambiguous cryptokey routing; the later peer silently displaces the earlier |
| Overlapping `allowed_ips` at differing prefix lengths | Warning in status | The kernel uses longest-prefix matching — valid, but usually a mistake |
| `allowed_ips` outside the interface subnet | Warning | Valid for site-to-site, usually a mistake otherwise |
| `listen_port` duplicated across managed interfaces | **Error** | The bind would fail |
| `addresses` overlapping another managed interface | **Error** | Ambiguous routing |
| Invalid interface name or over 15 characters | **Error** | The kernel rejects it |
| Public key not base64 of 32 bytes | **Error** | — |
| MTU outside 1280–1500 | Warning | Below 1280 is almost certainly a mistake; above 1500 fragments on most paths |
| `endpoint` given as a hostname | Warning | The kernel stores only the resolved address; DNS changes do not propagate |
| **Any IPv6 address or CIDR** | **Error** `IPV6_NOT_SUPPORTED` | v1 is IPv4 only (§12). Rejected explicitly, **never silently ignored** |
| `external = ALLOW` with `nat.enable_uplink_forwarding = false` | **Error** `FORWARD_POLICY_NEEDS_UPLINK` | A contradictory declaration — traffic would never pass |
| `allowed_peer_interfaces` naming a nonexistent interface | **Error** `PEER_INTERFACE_NOT_FOUND` | A typo causes silent loss of connectivity |
| `allowed_peer_interfaces` non-empty while `inter_interface ≠ ALLOW_LIST` | Warning | The field is ignored — usually a misunderstanding |
| One-sided `inter_interface = ALLOW_LIST` (wg0→wg1 but not wg1→wg0) | Warning | Valid and stateful (§3.4), but usually a forgotten reciprocal declaration |
| `external = ALLOW` with `nat.enabled = false` | Warning | Traffic leaves without SNAT, so almost certainly has no route back |

Warnings appear in `status.conditions` and do not block writes.

---

## 10. Observability

### 10.1. Metrics

A dedicated listener, default `127.0.0.1:9586`, path `/metrics`.

| Metric | Type | Labels |
|---|---|---|
| `wg_agent_build_info` | gauge | `version`, `commit`, `go_version` |
| `wg_agent_interfaces_total` | gauge | `managed` |
| `wg_agent_interface_up` | gauge | `interface` |
| `wg_agent_interface_peers` | gauge | `interface` |
| `wg_agent_peer_last_handshake_seconds` | gauge | `interface`, `public_key` |
| `wg_agent_peer_rx_bytes_total` | counter | `interface`, `public_key` |
| `wg_agent_peer_tx_bytes_total` | counter | `interface`, `public_key` |
| `wg_agent_peers_online` | gauge | `interface` |
| `wg_agent_reconcile_duration_seconds` | histogram | `interface` |
| `wg_agent_reconcile_errors_total` | counter | `interface`, `reason` |
| `wg_agent_reconcile_drift_total` | counter | `interface`, `field` |
| `wg_agent_api_requests_total` | counter | `method`, `code` |

> **Cardinality:** the `public_key` label explodes at thousands of peers. The flag
> `metrics.per_peer = false` (default `true`) switches to per-interface aggregates only.
> Documentation must state the recommended threshold.

`wg_agent_reconcile_drift_total` is the most operationally significant metric — it reveals that
something outside the agent is modifying WireGuard.

### 10.2. Logs

Structured JSON on stdout, collected by systemd. Fields: `ts`, `level`, `msg`, `interface`,
`peer`, `principal`, `request_id`, `error`, `reason`.

### 10.3. Audit log

Written separately to `/var/log/wg-agent/audit.jsonl`, one JSON line per **state-changing
operation**:

```json
{
  "ts": "2026-08-03T10:22:31Z",
  "request_id": "01J...",
  "principal": "spiffe://corp/svc/vpn-controller",
  "action": "CreatePeer",
  "resource": "interface/wg0/peer/AbC...=",
  "result": "OK",
  "revision_before": "7",
  "revision_after": "8",
  "changed_fields": ["allowed_ips", "persistent_keepalive"]
}
```

Never contains private or preshared key values — field names only.

---

## 11. Configuration and deployment

### 11.1. Configuration file

`/etc/wg-agent/config.yaml`. Every key is overridable through the `WG_AGENT_<PATH>` environment
variable.

```yaml
server:
  unix_socket: /run/wg-agent/wg-agent.sock
  socket_mode: "0660"
  socket_group: wg-agent
  tcp:
    enabled: false
    address: "127.0.0.1:8443"
    tls:
      cert_file: /etc/wg-agent/tls/server.crt
      key_file:  /etc/wg-agent/tls/server.key
      client_ca_file: /etc/wg-agent/tls/client-ca.crt
      min_version: "1.3"

security:
  allow_server_generated_keys: true
  allowed_client_identities: []      # empty = any certificate signed by the trusted CA
  roles:
    "spiffe://corp/svc/vpn-controller": admin
    "spiffe://corp/svc/monitoring":     reader

state:
  path: /var/lib/wg-agent/state.db

reconcile:
  interval: 30s
  apply_timeout: 10s
  backoff_min: 1s
  backoff_max: 60s

runtime:
  peer_online_threshold: 180s

# Defaults for new interfaces when the caller omits forward_policy.
# These values are the "flat LAN per group" pattern (§3.6).
defaults:
  forward_policy:
    intra_interface: ALLOW      # peers on one interface see each other
    inter_interface: DENY       # no leakage to other interfaces
    external: DENY              # no automatic internet access
  mtu: 1420

metrics:
  enabled: true
  address: "127.0.0.1:9586"
  per_peer: true

audit:
  enabled: true
  path: /var/log/wg-agent/audit.jsonl

log:
  level: info
  format: json
```

### 11.2. System requirements

The real constraint is the **kernel**, not the distribution:

| Requirement | Value |
|---|---|
| Kernel | 5.6 or later for in-tree WireGuard, or older with `wireguard-dkms` |
| Capability | `CAP_NET_ADMIN` |
| nftables (only when NAT is enabled) | `nf_tables` available |
| Tested distributions | Debian 11/12/13, Ubuntu 20.04/22.04/24.04 |

On those distributions the kernel requirement is always satisfied: Ubuntu 20.04 backported
WireGuard into 5.4, and Debian 11 ships 5.10.

### 11.3. Packaging

- Static binary, pure Go, `CGO_ENABLED=0`. Architectures: `amd64`, `arm64`.
- `.deb` built with `nfpm`.
- Includes the systemd unit, a `sysusers.d` entry creating the `wg-agent` account, a
  `tmpfiles.d` entry creating `/run/wg-agent`, and a sample configuration.
- `postinst` does **not** enable the TCP listener. The default is always the unix socket.

---

## 12. Outside the v1 scope (considered and deferred)

| Item | Reason for deferral | Conditions for revisiting |
|---|---|---|
| Userspace WireGuard (boringtun / wireguard-go) | Needed only where the kernel module cannot load (restricted containers). Adds an entire second backend | A genuine requirement from container users |
| Network namespaces | Substantially increases netlink complexity | A node-level multi-tenancy use case |
| Envelope encryption of keys through an external KMS | `0600` plus a dedicated account is proportionate to the threat model | A compliance requirement |
| Multi-node orchestration and mesh | Belongs to the platform layer by design | Never — this is a fixed boundary |
| IPAM | Belongs to the platform layer by design | Possibly as an opt-in module if several consumers need it |
| **IPv6 (entirely)** | IPv4 satisfies present requirements. Partial support is worse than none | A genuine requirement. Quite possibly **never** |
| Per-peer rate limiting | Requires `tc`/qdisc integration, a different scope entirely | A genuine requirement |

---

## 13. Roadmap

| Milestone | Content | Exit criteria |
|---|---|---|
| **M0 — Foundation** | Repo layout, `.proto`, `buf` toolchain, netlink and wgctrl adapters, in-memory interface and peer CRUD | `go test` runs in a netns: create an interface, add a peer, read statistics |
| **M1 — Declarative** | bbolt store, reconcile engine, revision/ETag, validation, error model | Reboot restores every interface and peer. Deleting a link by hand rebuilds it |
| **M2 — Security** | Unix socket, mTLS, roles, systemd hardening, `.deb` package | Install from `.deb`, run non-root with `CAP_NET_ADMIN`, mTLS working |
| **M3 — Operations** | Prometheus metrics, audit log, health/ready, structured logs | A Grafana dashboard shows handshake, traffic and drift |
| **M4 — Networking and UX** | `ForwardPolicySpec`, nftables, per-interface sysctl, NAT, `ClientRouting.AUTO`, QR, `BatchUpdatePeers`, `WatchPeerStatus` | Create a peer → receive a QR code → connect immediately. All four §3.6 patterns verified by netns tests |
| **M5 — Ecosystem** | Terraform provider, Go client SDK, published OpenAPI, documentation | `terraform apply` manages peers and detects drift |

---

## 14. Test strategy

| Level | Approach |
|---|---|
| Unit | netlink and wgctrl adapters behind interfaces, with fakes in service tests |
| Integration | A dedicated **network namespace** (`unshare -n`) with real WireGuard. No containers |
| Reconcile | Inject drift by hand (delete a link, add a foreign peer, change MTU) and assert convergence |
| Concurrency | Multiple goroutines writing one interface; assert serialization and revision correctness |
| Security | Assert no private key appears in any response, log or audit record (a full output scan) |
| Compatibility | CI matrix: Debian 11/12/13, Ubuntu 20.04/22.04/24.04 |

---

## 15. Known risks

| Risk | Impact | Mitigation |
|---|---|---|
| An operator edits WireGuard by hand outside the agent | Reconcile reverts their change | The `reconcile_drift_total` metric plus clear logging. Unmanaged interfaces are left alone |
| Interface key rotation disconnects every peer | Widespread outage | An explicit warning in the response; documentation covering the coordination procedure |
| Metric cardinality at thousands of peers | Prometheus overload | The `metrics.per_peer` flag |
| Store corruption | Loss of desired state | Transactional writes, startup checksum, documented backup procedure |
| Hostname endpoints do not follow DNS changes | Peers disconnect silently | A validation warning. Periodic re-resolution is a v2 candidate |
| Enabling uplink forwarding reaches outside the agent's ownership | Effects beyond WireGuard | Only with `external = ALLOW` **and** `enable_uplink_forwarding`. WARN plus audit. WireGuard-to-WireGuard traffic does **not** need it (§3.4) |
| `ALLOW` does not guarantee passage because the host firewall may block | Operator believes policy is open while traffic fails | Semantics stated in §3.4; `status.conditions` warns when a foreign FORWARD chain is detected |
| The `intra_interface = ALLOW` default is not deny-all | Peers on one interface see each other unintentionally | Stated in §3.6. The other two axes remain `DENY`, so nothing leaks beyond the interface |
| Client-side `AllowedIPs` not widened after opening `inter_interface` | Silent loss of connectivity, very hard to diagnose | `ClientRouting.AUTO` (§4.7) derives it, with `routing_explanation` in the response |
