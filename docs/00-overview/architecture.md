# Architecture

## Layers

```
                    ┌───────────────────────────────┐
                    │  unix socket · loopback HTTP  │
                    │  gRPC + REST · grpc-gateway   │
                    └───────────────┬───────────────┘
                                    │
                    ┌───────────────▼───────────────┐
                    │        API layer              │
                    │  validate · authz · audit     │
                    └───────────────┬───────────────┘
                                    │
        ┌───────────────────────────▼───────────────────────────┐
        │                    Core service                       │
        │                                                       │
        │   ┌──────────────┐        ┌─────────────────────┐     │
        │   │ Desired state│◄──────►│  Reconcile engine   │     │
        │   │   (bbolt)    │        │  diff · apply · retry│    │
        │   └──────────────┘        └──────────┬──────────┘     │
        └──────────────────────────────────────┼────────────────┘
                                               │
        ┌──────────────────────────────────────▼────────────────┐
        │                  Platform layer                       │
        │                                                       │
        │  ┌─────────┐  ┌──────────┐  ┌────────┐  ┌──────────┐  │
        │  │ wgctrl  │  │ netlink  │  │ nft    │  │ keys     │  │
        │  │ device  │  │ link/addr│  │ policy │  │curve25519│  │
        │  │ peers   │  │ route    │  │ NAT    │  │          │  │
        │  └────┬────┘  └────┬─────┘  └───┬────┘  └──────────┘  │
        └───────┼────────────┼────────────┼────────────────────┘
                │            │            │
                ▼            ▼            ▼
        ┌───────────────────────────────────────────┐
        │              Linux kernel                 │
        │   wireguard module · rtnetlink · nf_tables│
        └───────────────────────────────────────────┘
```

## Library responsibilities

This boundary is the most commonly misunderstood part of the design. `wgctrl-go`
**cannot create a network interface** — it configures a WireGuard device only. Creating
links, assigning addresses, setting MTU and adding routes all belong to netlink.

| Operation | Library | API |
|---|---|---|
| Create / delete interface | `vishvananda/netlink` | `LinkAdd(&netlink.Wireguard{})`, `LinkDel` |
| Up / down | `vishvananda/netlink` | `LinkSetUp`, `LinkSetDown` |
| Add / remove addresses | `vishvananda/netlink` | `AddrAdd`, `AddrDel`, `AddrList` |
| MTU | `vishvananda/netlink` | `LinkSetMTU` |
| Routes for AllowedIPs | `vishvananda/netlink` | `RouteAdd`, `RouteDel`, `RouteList` |
| Link event subscription | `vishvananda/netlink` | `LinkSubscribe` |
| Private key, listen port, fwmark | `wgctrl-go` | `ConfigureDevice` |
| Add / update / remove peers | `wgctrl-go` | `ConfigureDevice` + `PeerConfig` |
| Read device and statistics | `wgctrl-go` | `Device()`, `Devices()` |
| Key generation | `wgctrl-go/wgtypes` | `GeneratePrivateKey`, `GenerateKey` |
| Forward policy, NAT | `google/nftables` | dedicated `inet wg_agent` table |

**Invariant:** no `exec.Command` in production paths. Test helpers only — `REQ-SEC-041`.

## Repository layout

The Go module path is `wg-agent`. A bare path suits a module with no external consumers; the
Go client SDK of M5 is what makes a resolvable path necessary, and changing it then is one
`go mod edit -module` and a rewrite of the import prefix.

```
cmd/wg-agent/              entrypoint and subcommands per SPEC-12
api/proto/wgagent/v1/      .proto — source of truth for the API
gen/                       generated code (gRPC, gateway, OpenAPI) — committed
internal/
  server/                  gRPC server, gateway, listeners
  auth/                    socket peer credentials, token lookup, principal extraction
  audit/                   audit log writer
  service/                 business logic: interface, peer, key, config, diagnose, overview
  store/                   bbolt desired state, schema migration
  reconcile/               reconcile engine, work queue, backoff
  platform/
    wg/                    wgctrl adapter
    link/                  netlink adapter: link, addr, route
    nft/                   forward policy and NAT
    sysctl/                per-interface sysctl reads and writes
  wgconfig/                .conf and QR code generation
  metrics/                 Prometheus collectors
  validate/                validation rules per SPEC-07
  errors/                  error model, reason codes
packaging/
  systemd/                 unit, sysusers, tmpfiles, logrotate, /etc/default sample
  debian/                  nfpm configuration and maintainer scripts → .deb
install.sh                 install, update, uninstall — REQ-CFG-029
docs/                      documentation — start at docs/README.md
```

## Spec module to package mapping

| Spec | Primary packages |
|---|---|
| [SPEC-01](../20-spec/SPEC-01-resource-model.md) Resource model | `api/proto`, `internal/service` |
| [SPEC-02](../20-spec/SPEC-02-forward-policy.md) Forward policy | `internal/platform/nft`, `internal/platform/sysctl` |
| [SPEC-03](../20-spec/SPEC-03-state-reconcile.md) State and reconcile | `internal/store`, `internal/reconcile` |
| [SPEC-04](../20-spec/SPEC-04-api-conventions.md) API conventions | `internal/server`, `internal/errors` |
| [SPEC-05](../20-spec/SPEC-05-security.md) Security | `internal/server`, `internal/auth` |
| [SPEC-06](../20-spec/SPEC-06-key-management.md) Key management | `internal/service`, `internal/wgconfig` |
| [SPEC-07](../20-spec/SPEC-07-validation.md) Validation | `internal/validate` |
| [SPEC-08](../20-spec/SPEC-08-observability.md) Observability | `internal/metrics`, `internal/audit` |
| [SPEC-09](../20-spec/SPEC-09-config-deployment.md) Configuration and deployment | `cmd/wg-agent`, `packaging/` |
| [SPEC-10](../20-spec/SPEC-10-lifecycle.md) Lifecycle | `internal/store`, `cmd/wg-agent` |
| [SPEC-11](../20-spec/SPEC-11-diagnostics.md) Diagnostics | `internal/service` |

## Test strategy

| Level | Approach |
|---|---|
| Unit | The ports in `internal/platform` are the seam: adapters implement them against the kernel, `internal/platform/fake` implements them in memory, and nothing above that layer needs privilege |
| Integration | Real WireGuard inside a dedicated network namespace. A container supplies one, along with a pinned `wg`, `ip` and `nft` and the Go toolchain — see [running the tests](../50-guides/running-tests.md) |
| Reconcile | Inject drift manually — delete a link, add a foreign peer, change MTU — assert convergence |
| Roaming | Change a peer endpoint externally and assert reconcile does not overwrite it (`REQ-RCN-013`) |
| Concurrency | Concurrent writers on one interface; assert serialization and revision behavior |
| Security | Scan every response, log and audit record for leaked secrets (`REQ-SEC-051`) |
| Compatibility | CI matrix: Debian 11/12/13, Ubuntu 20.04/22.04/24.04 |

Every test names the REQ ID it verifies, per the convention in
[20-spec/README.md](../20-spec/README.md).
