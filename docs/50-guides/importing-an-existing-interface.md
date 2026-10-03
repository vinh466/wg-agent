# Importing an existing interface

You already run WireGuard on a node with a hand-written `wg-quick` file, and you want the agent to
manage it. This guide walks the one-time migration. It reuses your existing private key, so clients
reconnect on their own and nothing on their side changes.

## How it works

The agent takes a JSON document describing the interface and its peers and recreates the interface
from it in a single write — `interface create --file`. This is the supported migration path in v1.
(Zero-downtime adoption of a *live* link without recreating it is on the
[backlog](../60-planning/backlog.md), entry B-10.)

Two things follow from that:

- The agent **will not touch a link or a file it did not create**, and it refuses to create an
  interface whose name already exists as a link or a `/etc/wireguard/*.conf`. So you bring the old
  interface down and move its file aside first.
- The create is **atomic**: if anything in the document is invalid, nothing is created.

Downtime is the few seconds between taking the old interface down and the agent bringing it back up.

## 1. Translate the config to a document

Take your `/etc/wireguard/wg0.conf` and write `wg0.json`. Map the fields like this:

| wg-quick | document |
|---|---|
| `[Interface] PrivateKey` | `spec.private_key` |
| `Address` | `spec.addresses` (array) |
| `ListenPort` | `spec.listen_port` |
| `PostUp` / `PostDown` lines | `spec.post_up` / `spec.post_down` (arrays, one entry per line) |
| each `[Peer] PublicKey` | a `peers[]` entry's `public_key` |
| `AllowedIPs` | that peer's `spec.allowed_ips` (array) |
| `Endpoint` | that peer's `spec.endpoint` |

```json
{
  "spec": {
    "private_key": "<YOUR EXISTING wg0 PRIVATE KEY>",
    "listen_port": 51820,
    "addresses": ["10.8.0.1/24"],
    "post_up": ["iptables -A FORWARD -i wg0 -j ACCEPT", "iptables -t nat -A POSTROUTING -o eth0 -j MASQUERADE"],
    "post_down": ["iptables -D FORWARD -i wg0 -j ACCEPT", "iptables -t nat -D POSTROUTING -o eth0 -j MASQUERADE"]
  },
  "peers": [
    { "public_key": "<PEER_1_PUBKEY>", "spec": { "allowed_ips": ["10.8.0.2/32"], "endpoint": "203.0.113.5:51820" } },
    { "public_key": "<PEER_2_PUBKEY>", "spec": { "allowed_ips": ["10.8.0.3/32"] } }
  ]
}
```

Keep the file readable only by root (it holds the private key): `sudo install -m 600 /dev/stdin /root/wg0.json`
or `chmod 600` after writing.

## 2. Fix what the agent will reject

The agent validates before it writes. The two most common blockers in a hand-made file:

- **A peer with no `AllowedIPs`.** WireGuard sends such a peer nothing — it is a dead entry — so the
  agent refuses it with `ALLOWED_IPS_REQUIRED`. Give each peer a `/32` (or the subnet it routes), or
  drop it from the document.
- **An entry with host bits**, e.g. `10.8.0.5/24`. WireGuard would silently mask it; the agent
  refuses it with `ALLOWED_IPS_NOT_CANONICAL`. Write `10.8.0.5/32` if you meant one host.

A comment like `# peer-7` has no place in the document — carry the name as a label instead:
`"labels": {"name": "peer-7"}`.

## 3. Take the old interface down, then import

```bash
sudo wg-quick down wg0 || sudo systemctl stop wg-quick@wg0
sudo systemctl disable wg-quick@wg0
sudo mv /etc/wireguard/wg0.conf /root/wg0.conf.bak    # the agent writes its own file here

sudo wg-agent interface create wg0 --file /root/wg0.json
```

Because the document carries the **same** private key, listen port and addresses, your peers
reconnect without any change on their side.

## 4. Verify

```bash
sudo wg-agent interface get wg0        # state: UP
sudo wg-agent peer list wg0            # your peers, each with status
sudo wg show wg0
```

If `create` exits non-zero, read its last line — it names the reason code (for example
`ALLOWED_IPS_REQUIRED` or `APPLY_FAILED`). On any failure the create rolls back and stores nothing,
so you can fix the document and run it again.

## After importing

- The interface is now the agent's. Add and remove peers with `wg-agent peer add` / `peer remove` —
  no downtime, no editing files.
- Stop and start it **through the agent** (`interface update wg0 --enabled false|true`), not with
  `wg-quick` by hand. Downing it by hand leaves systemd thinking the unit is up while the device is
  gone, which shows as `state: ABSENT`; recover with `sudo systemctl restart wg-quick@wg0`.
- The hooks you imported run in the `wg-quick@wg0` unit exactly as before. The agent never runs
  them itself, and the API can neither read nor change them — they are the CLI's alone.
