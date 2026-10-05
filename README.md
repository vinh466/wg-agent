# wg-agent

A small control-plane agent that manages the WireGuard interfaces of **one Linux node** — a thin,
safe wrapper over `wg` and `wg-quick`. Drive it from a CLI on the node, or from a REST API over your
own private network.

It replaces editing `wg-quick` files by hand: create interfaces, add and remove peers, hand a client
its `.conf`, read status — and it refuses the configurations WireGuard would otherwise accept and
then silently mishandle.

Think of it as the automation layer you would otherwise write yourself. It owns one node's
interfaces and nothing more: *which* addresses, *which* peers and *which* nodes remain your
decision, or that of the orchestrator above it. One agent per node, reached the same way whether
you are at the node's shell or scripting against a fleet.

> Internal tool · IPv4 only · Debian 13+ / Ubuntu 24.04 LTS+ (amd64) · one bearer token over plain
> HTTP on a network you control.

## Why

- **One command per change, from anywhere on your private network.** No shell on the node to add a
  peer, and no restart for one either — `wg syncconf` leaves every other session untouched.
- **Refusal before breakage.** Host bits, a default route, a port already in use, a peer carrying
  the node's own key — the mistakes WireGuard accepts and then mishandles come back as a reason code.
- **A complete client file, once.** Generated with the peer, carrying the right endpoint and port.
- **The data plane runs without the agent.** Interfaces belong to their `wg-quick@` units, so
  stopping or upgrading the agent never drops a tunnel.

## Install

On Debian 13+ / Ubuntu 24.04 LTS+ (amd64):

```bash
curl -fsSL https://raw.githubusercontent.com/vinh466/wg-agent/main/packaging/install.sh | sudo bash
```

- **Update:** re-run the same command.
- **Remove:** `curl -fsSL …/install.sh | sudo bash -s -- --uninstall` — your tunnels keep running.

The installer fetches the latest release `.deb`, verifies its SHA256, installs it, generates a token
at `/etc/wg-agent/token`, and starts the service on `127.0.0.1:9585`. To reach the API from another
host, set `WG_AGENT_LISTEN_ADDRESS` in `/etc/default/wg-agent` to an address on your private network.

## A 60-second tour

```bash
# create an interface
sudo wg-agent interface create wg0 --addresses 10.8.0.1/24 --listen-port 51820

# add a peer and get a ready-to-use client .conf (the agent generates the key pair)
sudo wg-agent peer add wg0 --generate-keypair --node-endpoint vpn.example.com > client.conf

# add a peer whose public key you already have
sudo wg-agent peer add wg0 --public-key <BASE64_PUBLIC_KEY> --allowed-ips 10.8.0.2/32

sudo wg-agent peer list wg0
sudo wg-agent interface get wg0
```

The same operations over the API, with the token, from your private network:

```bash
TOKEN=$(sudo cat /etc/wg-agent/token)
curl -H "Authorization: Bearer $TOKEN" http://NODE:9585/v1/interfaces
```

Already running WireGuard by hand? Bring it under the agent in one command —
see **[Importing an existing interface](docs/50-guides/importing-an-existing-interface.md)**.

New here? Start with **[Getting started](docs/50-guides/getting-started.md)**. Looking up a command
mid-task? The **[CLI reference](docs/50-guides/cli-reference.md)** has every one with an example.

## What's in this release

- Interfaces, peers and client configs; status read from `wg show`
- A CLI on the node and a REST API behind one bearer token
- `PostUp` / `PostDown` hooks (set through the CLI)
- Structured JSON logs; a `.deb`, an install script and a GitHub release pipeline

Deliberately **not** in v1 — forward policy / NAT managed by the agent, drift auto-correction,
zero-downtime adoption of a live interface, IPv6, metrics and an audit log. Each is written up with
the reason in the [roadmap](docs/60-planning/roadmap.md) and the [backlog](docs/60-planning/backlog.md).

## How it's built

**Spec-first:** the specification is written and approved before any code, and every line of code
traces back to a `REQ-*` requirement. C# on .NET 10, shipped as one NativeAOT binary for `linux-x64`.
It runs `wg` and `systemctl` as its only child processes; `wg-quick` runs in its own systemd unit.

| To understand… | Read |
|---|---|
| What it is, and who it's for | [product](docs/00-overview/product.md) |
| The shape of the system | [architecture](docs/00-overview/architecture.md) |
| The whole documentation map | [docs/README.md](docs/README.md) |
| The API contract | [api/openapi.yaml](api/openapi.yaml) · [rendered](docs/30-api/api.md) |
| The normative requirements | [docs/20-spec](docs/20-spec/) |
| Why a decision was made | [docs/10-decisions](docs/10-decisions/) |
| Contributing to the docs or the code | [docs/CONTRIBUTING.md](docs/CONTRIBUTING.md) · [CLAUDE.md](CLAUDE.md) |

## Status

**P1–P3 delivered** — core, CLI, REST API and packaging. The unit, integration and packaging test
tiers pass, and the binary publishes NativeAOT with no trim or AOT warning. See the
[roadmap](docs/60-planning/roadmap.md) for what each phase covered and what comes next.

## License

[MIT](LICENSE) © 2026 Vinh Nguyen
