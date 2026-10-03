# Getting started

From an empty node to a working WireGuard interface with a connected client, in a few minutes.
This guide addresses you directly, as a guide should; the normative details live in
[20-spec](../20-spec/).

## Before you start

- A node running **Debian 13+** or **Ubuntu 24.04 LTS+** on **amd64**.
- Root on that node (the agent runs as root, confined by its systemd unit).
- The WireGuard kernel module — in tree on every supported kernel; nothing to install.

## 1. Install

```bash
curl -fsSL https://raw.githubusercontent.com/vinh466/wg-agent/main/packaging/install.sh | sudo bash
```

The installer downloads the latest release `.deb`, verifies its checksum, installs it, generates a
bearer token at `/etc/wg-agent/token`, and starts the service on `127.0.0.1:9585`.

Check it is running:

```bash
systemctl status wg-agent --no-pager
wg-agent version
```

## 2. Create your first interface

```bash
sudo wg-agent interface create wg0 --addresses 10.8.0.1/24 --listen-port 51820
sudo wg-agent interface get wg0
```

`get` should show `state: UP`. The agent wrote `/etc/wireguard/wg0.conf` and started the
`wg-quick@wg0` unit; the interface now survives a reboot on its own.

Omit `--listen-port` to take the default (51820), `--addresses` is required. If the private key is
omitted, the agent generates one; supply your own with a JSON document (see
[importing an existing interface](importing-an-existing-interface.md)).

## 3. Add a peer

**Let the agent generate the key pair** and hand you a ready-to-use client file. Give the host your
clients reach this node at:

```bash
sudo wg-agent peer add wg0 --generate-keypair --node-endpoint vpn.example.com > client.conf
```

`client.conf` is a complete `wg-quick` file — private key, the address the agent picked from the
subnet, the server's public key, endpoint and keepalive. The private key is printed this once and
never stored; copy `client.conf` to the client and `wg-quick up ./client.conf`.

**Or bring your own public key** (the client keeps its private key):

```bash
sudo wg-agent peer add wg0 --public-key <BASE64_PUBLIC_KEY> --allowed-ips 10.8.0.2/32
```

Either way, adding a peer needs **no restart** — existing sessions stay up.

List and inspect:

```bash
sudo wg-agent peer list wg0
sudo wg-agent peer get wg0 <BASE64_PUBLIC_KEY>
```

## 4. Use the REST API

Every CLI operation is also an API call. Read the token and call the API — locally, or from your
private network once you set `WG_AGENT_LISTEN_ADDRESS`:

```bash
TOKEN=$(sudo cat /etc/wg-agent/token)
curl -H "Authorization: Bearer $TOKEN" http://127.0.0.1:9585/v1/interfaces
curl -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"name":"wg1","addresses":["10.9.0.1/24"],"listen_port":51821}' \
  http://127.0.0.1:9585/v1/interfaces
```

The health endpoint needs no token:

```bash
curl http://127.0.0.1:9585/v1/health
```

The full contract is [api/openapi.yaml](../../api/openapi.yaml); a readable rendering is
[30-api/api.md](../30-api/api.md). To expose the API beyond the node, edit
`WG_AGENT_LISTEN_ADDRESS` in `/etc/default/wg-agent` to an address on a network **you** control —
the token crosses it in clear.

## 5. Day-to-day

- **Rotate the token:** `sudo wg-agent token rotate` — prints the new one once; the running agent
  honours it from the next request.
- **Stop or start an interface through the agent**, so systemd and the agent's state stay in step:

  ```bash
  sudo wg-agent interface update wg0 --enabled false   # stop and disable its unit
  sudo wg-agent interface update wg0 --enabled true     # start and enable it
  ```

  Avoid `wg-quick down wg0` by hand: it removes the device while systemd still thinks the unit is
  up, which shows as `state: ABSENT`. Recover with `sudo systemctl restart wg-quick@wg0`.
- **Change a field** with its flag; only the named fields change:
  `sudo wg-agent interface update wg0 --mtu 1380`.

## 6. Update and remove

```bash
# update to the latest release
curl -fsSL https://raw.githubusercontent.com/vinh466/wg-agent/main/packaging/install.sh | sudo bash

# remove the agent — your WireGuard interfaces keep running
curl -fsSL https://raw.githubusercontent.com/vinh466/wg-agent/main/packaging/install.sh | sudo bash -s -- --uninstall
```

Removal never deletes a WireGuard link; it prints the ones it leaves behind so you know what is now
yours to manage.

## Next

- Already have a hand-made interface? [Import it](importing-an-existing-interface.md).
- Picking addresses and routes for several peers: [topology patterns](topology-patterns.md).
- What the agent refuses, and why: [validation](../20-spec/SPEC-07-validation.md).
