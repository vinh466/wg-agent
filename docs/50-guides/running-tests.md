# Running the tests

Three tiers, separated by what privilege they need. The rule that keeps the codebase
maintainable is that only the platform adapters need privilege: if a test above that layer asks
for root, a seam has leaked.

| Tier | Command | Needs | Covers |
|---|---|---|---|
| Documentation | `make check` | nothing | `docs/check-docs.sh` and `docs/check-traceability.sh` |
| Unit | `make test` | a Go toolchain | everything above `internal/platform` |
| Integration | `make docker-test` | Docker | real WireGuard through netlink and wgctrl |
| Privileged | `make docker-test-privileged` | Docker | the forwarding sysctl and nftables tiers |

You do not need a Go toolchain on the host. The container image in `test/docker/Dockerfile`
carries one, so `make docker-test` is enough on a machine with only Docker installed.

## Before anything else: check the environment

```bash
make probe
```

This creates a WireGuard interface inside a container through `netlink`, configures it through
`wgctrl`, reads the interface key and every peer back, and deletes it. It exercises the library
boundary that [architecture.md](../00-overview/architecture.md) describes and the adoption read
that `REQ-RCN-061` and `REQ-RCN-062` depend on.

Run it first on any new machine. When it fails, no test above it can pass, and the reason is
almost always one of the two below.

### The kernel module lives on the host

A container cannot load `wireguard`. Load it on the host:

```bash
sudo modprobe wireguard
lsmod | grep wireguard
```

Kernel 5.6 and later carry the module in tree. On anything older, install
`wireguard-dkms` first.

### The container needs `CAP_NET_ADMIN`

`make docker-test` passes `--cap-add=NET_ADMIN`. Creating a link without it fails with
`operation not permitted`.

## Why a container rather than a bare network namespace

Both work, and the container is preferred for three reasons.

The container gets its own network namespace, so an interface a test creates cannot collide
with one that matters on your machine. Test code never has to create or clean up a namespace.

It pins the userspace. `wg`, `ip` and `nft` come from the image at known versions, so a failure
is a failure of the code rather than of whatever the host happens to have installed.

It carries the Go toolchain, so a contributor needs Docker and nothing else.

## What the privileged tier is for, and why it is separate

Docker mounts `/proc/sys` read-only. Every requirement that writes a sysctl —
`REQ-FWD-020`, `REQ-FWD-022`, `REQ-FWD-024`, `REQ-FWD-025` — therefore fails in an ordinary
container, as does the `nft` table of `REQ-FWD-010`. Those tests carry the `privileged` build
tag and run under `make docker-test-privileged`.

Keep them separate and keep them few. A privileged container is not isolated from the host: it
can write the host's global `net.ipv4.ip_forward`, which `REQ-FWD-021` forbids the agent from
touching at all.

One trap worth knowing before you write an assertion in this tier. Docker enables forwarding in
its containers, so `net.ipv4.conf.<iface>.forwarding` starts at `1` there, while a freshly
booted host usually starts at `0`. A test for `REQ-FWD-024`, which records a baseline and
restores it, has to set the baseline it expects rather than assume one.

## Writing a test

Name the requirement it verifies. `docs/check-traceability.sh` matches these against the spec,
so the ID has to be exact and use underscores:

```go
func TestForwardPolicy_IntraDeny_REQ_FWD_012(t *testing.T) { ... }
```

Put an integration test behind the build tag so `go test ./...` stays privilege-free:

```go
//go:build integration
```

Unit tests above the platform layer use a fake adapter rather than the kernel. That is what
lets the whole reconcile algorithm of `REQ-RCN-022` — drift, adoption, orphan handling — run in
milliseconds without privilege.

## The distribution matrix

`REQ-CFG-013` requires the systemd hardening combination to be verified on every tested
distribution. Docker does not serve that tier: the assertion is about `systemd` unit directives,
which need systemd running as PID 1. Use a virtual machine or a CI runner per distribution.
That work sits with packaging, deferred under `B-03` in the
[backlog](../60-planning/backlog.md).
