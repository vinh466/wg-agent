# CLI reference

A lookup sheet for the `wg-agent` command line: every subcommand, its purpose, an example, and the
flags you reach for most. For a step-by-step walkthrough instead, see
[getting started](getting-started.md); the authoritative list of subcommands is
[SPEC-12](../20-spec/SPEC-12-cli.md).

The always-current reference is the binary itself — `--help` works on every command:

```bash
wg-agent --help
wg-agent interface --help
wg-agent peer add --help
```

Every subcommand but `serve` acts on the node directly (no running agent required), takes the same
validation as the API, and prints to the same store the API uses.

## Global options

These work on any subcommand (they are recursive):

| Option | Meaning |
|---|---|
| `--output text\|json` | Human table (default) or JSON for scripts |
| `--config <path>` | Configuration file (default `/etc/default/wg-agent`) |
| `--node-endpoint <host>` | Host clients reach this node at (for generated client configs) |
| `--listen-address <ip:port>` | API bind address (default `127.0.0.1:9585`) |
| `--state-path <path>` | Store file (default `/var/lib/wg-agent/state.json`) |
| `--token-file <path>` | Token file (default `/etc/wg-agent/token`) |
| `--apply-timeout <Ns>` · `--peer-online-threshold <Ns>` · `--log-level <level>` | As in `/etc/default/wg-agent` |

A flag overrides the file and the environment. Durations are whole seconds with an `s`, e.g. `10s`.

## interface

| Command | Does |
|---|---|
| `interface create <name>` | Create an interface |
| `interface list` | List managed interfaces |
| `interface get <name>` | Show one interface, its spec and status |
| `interface update <name>` | Change only the fields the flags name |
| `interface delete <name>` | Stop its unit and remove its file and peers |

Fields (on `create` and `update`): `--addresses <cidr>` (once per address), `--listen-port <n>`,
`--mtu <n>`, `--enabled true|false`, `--labels key=value` (once per label), `--post-up <cmd>` and
`--post-down <cmd>` (once per line).

```bash
# create wg0 on 10.8.0.1/24, port 51820 (private key generated if omitted)
wg-agent interface create wg0 --addresses 10.8.0.1/24 --listen-port 51820

# a hardened interface with hooks
wg-agent interface create wg0 --addresses 10.8.0.1/24 \
  --post-up "iptables -A FORWARD -i wg0 -j ACCEPT" \
  --post-down "iptables -D FORWARD -i wg0 -j ACCEPT"

# change one field — the rest is kept
wg-agent interface update wg0 --mtu 1380
# clear a field with an empty value
wg-agent interface update wg0 --post-up ""
# stop / start through the agent (keeps systemd and the store in step)
wg-agent interface update wg0 --enabled false
wg-agent interface update wg0 --enabled true

wg-agent interface list
wg-agent interface get wg0
wg-agent interface delete wg0
```

Create an interface **and its peers from one JSON document** (how a hand-made interface moves under
the agent — see [importing an existing interface](importing-an-existing-interface.md)):

```bash
wg-agent interface create wg0 --file /root/wg0.json
```

## peer

| Command | Does |
|---|---|
| `peer add <interface>` | Add a peer — a supplied key, or one the agent generates |
| `peer list <interface>` | List the peers, each with status |
| `peer get <interface> <public-key>` | Show one peer |
| `peer update <interface> <public-key>` | Change only the fields the flags name |
| `peer remove <interface> <public-key>` | Remove a peer |

Fields: `--allowed-ips <cidr>` (once per entry), `--endpoint <host:port>`,
`--persistent-keepalive <n>`, `--labels key=value`. For `add` with a generated key pair:
`--generate-keypair`, `--generate-preshared-key`, `--node-endpoint <host>`,
`--client-allowed-ips <cidr>`, `--client-persistent-keepalive <n>`, `--dns <ip>`.

```bash
# bring your own public key — give it a /32 in the interface's subnet
wg-agent peer add wg0 --public-key <BASE64_PUBLIC_KEY> --allowed-ips 10.8.0.2/32

# let the agent generate the key pair and print a ready client .conf (private key shown once)
wg-agent peer add wg0 --generate-keypair --node-endpoint vpn.example.com > client.conf
# a full-tunnel client with DNS and a preshared key
wg-agent peer add wg0 --generate-keypair --node-endpoint vpn.example.com \
  --client-allowed-ips 0.0.0.0/0 --dns 1.1.1.1 --generate-preshared-key > client.conf

wg-agent peer list wg0
wg-agent peer update wg0 <BASE64_PUBLIC_KEY> --persistent-keepalive 25
wg-agent peer remove wg0 <BASE64_PUBLIC_KEY>
```

Adding, changing or removing a peer needs **no restart** — other sessions stay up. A key never
appears on the command line as an argument; the agent generates it, or it comes in the JSON document.

## token and version

```bash
wg-agent token rotate        # generate + store a new token, print it once
wg-agent version             # the version and the commit
wg-agent version --output json
```

No command prints an existing token; `token rotate` is the only way to see one, and only the value
it just made.

## Output and scripting

- `--output json` prints a resource (or array) for reads, and a result object for writes
  (`{ "interface": …, "restarted": … }` or `{ "peer": …, "restarted": …, "private_key": … }`).
- A failing command exits non-zero and writes `wg-agent: <REASON_CODE>: <message>` to **stderr**.
- `peer add --generate-keypair` prints the client `.conf` alone on **stdout** (redirect it to a
  file); everything else, including the new peer's public key, goes to stderr.

## Common refusals

The agent refuses what WireGuard would accept and then mishandle. The reason code is on the last
line; the fix is usually one of these:

| Reason code | Fix |
|---|---|
| `ALLOWED_IPS_REQUIRED` | A BYOK peer needs at least one `--allowed-ips` |
| `ALLOWED_IPS_NOT_CANONICAL` | Host bits set — use `/32` for one host (e.g. `10.8.0.5/32`) |
| `ALLOWED_IPS_DUPLICATE` | Another peer already holds that IP |
| `INTERFACE_EXISTS` | The link or `/etc/wireguard/<name>.conf` already exists — remove it first |
| `INTERFACE_NOT_MANAGED` | The agent did not create this interface |
| `PEER_EXISTS` | That public key is already on the interface |
| `ENDPOINT_REQUIRED` | A generated peer needs `--node-endpoint` (or `node.endpoint` in the config) |
| `ADDRESS_CONFLICT` / `LISTEN_PORT_IN_USE` | Another interface on the host holds that subnet or port |

Full list and HTTP mapping: [SPEC-04 section 7](../20-spec/SPEC-04-api-conventions.md).
