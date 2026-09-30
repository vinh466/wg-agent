---
id: SPEC-05
title: Security, authentication and authorization
prefix: SEC
status: Accepted
version: 2.0
owner: Vinh Nguyen
created: 2026-08-03
updated: 2026-09-30
depends_on: [SPEC-04]
adrs: [ADR-0007, ADR-0013, ADR-0015]
milestone: P1–P3
---

# SPEC-05: Security, authentication and authorization

## 1. Scope

The listener, authentication, authorization, least-privilege execution, child processes,
handling of sensitive data.

**Not in this module:**
- WireGuard key generation and storage → [SPEC-06](SPEC-06-key-management.md)
- How these values are configured → [SPEC-09](SPEC-09-config-deployment.md)

## 2. Threat model

The agent runs as root and can create tunnels into an internal network. An exposed endpoint is
equivalent to a compromised network.

Under [ADR-0015](../10-decisions/ADR-0015-network-listener-with-a-shared-secret.md) the API
listener serves plain HTTP on one configured address, loopback when none is configured, and the
operator places any other address on a private network. The controls that matter are the one
token and the file that holds it, the bind address, and the private network: the channel carries
the token in clear, so whoever reads that network holds the node. ADR-0015 records the reasoning,
and the TLS route measured for a channel that crosses a network the operator does not control.

## 3. Listener

> **REQ-SEC-084** — When no listener address is configured, the agent MUST bind to a loopback
> address.

An address beyond loopback is the operator's choice under ADR-0015, and `REQ-CFG-022` keeps the
package from making it on the operator's behalf.

> **REQ-SEC-083** — When the metrics listener is enabled, the agent MUST refuse to start unless
> its bind address is a loopback address.

`REQ-SEC-083` exists because the metrics endpoint is not the harmless surface it looks like.
`REQ-OBS-002` exposes a series per peer, so the endpoint names every public key and handshake
time on the node — the same disclosure `REQ-DIA-024` keeps out of the overview, on a listener
that carries no token under `REQ-SEC-071`. `REQ-OBS-003` is the operator's opt-out, not the
reason the exposure exists.

## 4. Authentication

The listener derives identity from one bearer token, the single shared secret of ADR-0015.

> **REQ-SEC-071** — Every request arriving on the HTTP listener other than the health
> endpoint MUST carry a bearer token in the `Authorization` header.

> **REQ-SEC-080** — The health endpoint MUST be served without authentication.

`REQ-SEC-080` keeps a liveness probe from needing a credential. The endpoint reports only
whether the agent is serving, so it discloses nothing an attacker on the host could not
observe from the process table.

> **REQ-SEC-072** — The agent MUST refuse to start when no token is configured.

> **REQ-SEC-078** — A request whose token is missing or wrong MUST be rejected with HTTP status
> `401` and reason `TOKEN_INVALID`.

One reason covers both cases deliberately. Distinguishing a missing token from a wrong one tells
a caller that the header form was right, which is information an attacker gains and a legitimate
caller does not need.

> **REQ-SEC-073** — Token comparison MUST be constant-time.

> **REQ-SEC-074** — The agent MUST reject a token file granting any access beyond its owner.

> **REQ-SEC-082** — The token file MUST be owned by the account running the agent.

`REQ-SEC-074` and `REQ-SEC-082` are one control split in two. Mode `0600` alone leaves the
file unreadable by the agent whenever the writing command ran as a different account, and the
agent then refuses to start under `REQ-SEC-072`. `REQ-RCN-004` states the same pairing for the
store.

> **REQ-SEC-075** — Each configured token MUST map to exactly one role.

> **REQ-SEC-076** — A token value MUST NOT appear in any log entry, audit record or API
> response.

> **REQ-SEC-085** — A replaced token MUST be honoured, and the token it replaced refused, from the
> first request after the replacement.

The agent reads the token file again when it changes rather than on a signal. That is what lets
the command replacing the token take effect without finding the running agent, which neither a
pid file nor a child process may do.

> **REQ-SEC-079** — The principal attributed to a request MUST be `unix/<uid>` on the unix
> socket and `token/<label>` on the HTTP listener.

`REQ-SEC-079` gives [SPEC-08](SPEC-08-observability.md) a stable audit identity without exposing
the token value; it arrives with several tokens and the audit log.

A bearer token has no expiry and is not bound to a caller. Revocation is a replacement under
`REQ-SEC-085`, which is proportionate while the token reaches the node only over the operator's
private network.

## 5. Authorization

With one token every authenticated caller holds full control. The roles below arrive with several
tokens; until then `REQ-SEC-075`, `REQ-SEC-020` and `REQ-SEC-021` have nothing to decide.

> **REQ-SEC-020** — The agent MUST support two roles.

| Role | Permitted |
|---|---|
| `reader` | Read-only operations: Get, List, Health |
| `admin` | All operations |

> **REQ-SEC-021** — An identity absent from the role mapping MUST be rejected with
> `PERMISSION_DENIED`.

Finer-grained RBAC belongs to the platform layer.

## 6. Least-privilege execution

> **REQ-SEC-086** — The agent MUST run as root with `CAP_NET_ADMIN` as the only capability in its
> bounding set.

[ADR-0013](../10-decisions/ADR-0013-drive-wg-and-wg-quick.md) needs root: `/etc/wireguard/` is
root's `0700` directory, and starting a unit takes root or a polkit grant. The bounding set, and
the read-only filesystem of `REQ-CFG-043`, carry the weight a non-root account carried before.

> **REQ-SEC-031** — The installation package MUST ship a hardened systemd unit.

Unit details are in [SPEC-09](SPEC-09-config-deployment.md).

## 7. Child processes and shell hooks

> **REQ-SEC-040** — The API MUST NOT expose any field accepting a shell command, an
> executable path, or command-line arguments.

> **REQ-SEC-087** — The agent MUST start no child process other than `wg` and `systemctl`.

> **REQ-SEC-088** — The agent MUST start a child process with a fixed argument vector and never
> through a shell.

> **REQ-SEC-089** — The agent MUST pass a key to a child process only through its standard input.

ADR-0013 drives WireGuard through the two programs that own its files and units, and no other.
A fixed argument vector means no request field reaches a string a shell parses. The argument
list of every process is readable through `/proc` by every account on the host, so a key placed
there would be a key disclosed; standard input is not. `REQ-APL-003` keeps hooks out of every file
the agent renders, so no caller reaches a shell through `wg-quick` either.

Full reasoning for the hook rule is in [ADR-0007](../10-decisions/ADR-0007-no-shell-hooks.md): an
API that accepts a shell string and runs it as root is a remote code execution endpoint, and no
level of authentication changes that.

## 8. Sensitive data

| Data | Rule |
|---|---|
| Interface private key | Stored at `0600`, in the store and in the interface's configuration file. Never returned, never logged |
| Server-generated peer private key | Never stored. Returned once, inside the client configuration. Never logged |
| Preshared key | Stored, in the store and in the configuration file. Returned only inside the client configuration of the request that generated it. Never logged |
| API token | Stored at `0600`. Printed only by the command that generates it. Never logged |
| Public key | Not sensitive. Freely logged and returned |

> **REQ-SEC-050** — Sensitive values MUST be represented by a dedicated type whose every textual
> and serialised form is `[REDACTED]`.

> **REQ-SEC-051** — The test suite MUST scan every response, log entry and audit record to
> assert no secret key is emitted.

`REQ-SEC-050` places redaction at the type level rather than relying on author discipline,
which is the only way to make it dependable. A structured logger or a serialiser that reads the
value's properties rather than calling one method is the case the wording has to reach.

## 9. Removed requirements

Removed in v1.2 by [ADR-0009](../10-decisions/ADR-0009-local-only-listeners.md), which
confines v1 to local listeners. Remote management over TCP with mTLS is deferred; see the
scope table in [product.md](../00-overview/product.md).

~~**REQ-SEC-002**~~ — Default bind restriction. Replaced by the stronger `REQ-SEC-070`, which
forbids a non-loopback bind outright rather than by default.

~~**REQ-SEC-004**~~ — mTLS mandatory on TCP. No TCP listener accepts non-loopback traffic.

~~**REQ-SEC-005**~~ — Loopback restriction under `--insecure-no-auth`. The flag no longer
exists; `REQ-SEC-070` applies unconditionally.

~~**REQ-SEC-061**~~ — Periodic `WARN` under `--insecure-no-auth`. The flag no longer exists.

~~**REQ-SEC-010**~~ — Minimum TLS version. No TLS listener.

~~**REQ-SEC-011**~~ — Client certificate verification against a CA bundle. No mTLS.

~~**REQ-SEC-012**~~ — Identity from the SPIFFE URI SAN or Subject CN. Replaced by peer
credentials and tokens under section 4.

~~**REQ-SEC-013**~~ — Certificate reload on `SIGHUP`. No certificates.

~~**REQ-SEC-014**~~ — `allowed_client_identities` enforcement. Replaced by the token to role
mapping under `REQ-SEC-075`.

Removed in v2.0 by [ADR-0013](../10-decisions/ADR-0013-drive-wg-and-wg-quick.md), which drives
`wg` and `wg-quick` as root, and [ADR-0015](../10-decisions/ADR-0015-network-listener-with-a-shared-secret.md),
which serves the API on a configured address:

~~**REQ-SEC-001**~~ — Unix socket as the default listener. The listener is HTTP; `REQ-SEC-084`
binds it to loopback when no address is configured.

~~**REQ-SEC-060**~~ — No TCP listener by default. Replaced by `REQ-SEC-084`.

~~**REQ-SEC-003**~~ — Mode `0660` and a configurable group on the unix socket. No socket.

~~**REQ-SEC-070**~~ — Refusal to start on a non-loopback HTTP bind. ADR-0015 permits one on the
operator's private network.

~~**REQ-SEC-077**~~ — `admin` for every caller on the unix socket. No socket.

~~**REQ-SEC-081**~~ — Token file reload on `SIGHUP`. Replaced by `REQ-SEC-085`, which needs no
signal.

~~**REQ-SEC-030**~~ — A non-root account holding only `CAP_NET_ADMIN`. Replaced by
`REQ-SEC-086`.

~~**REQ-SEC-062**~~ — No package configuration running the agent as root. ADR-0013 requires root.

~~**REQ-SEC-041**~~ — No child process in any production path. Replaced by `REQ-SEC-087` to
`REQ-SEC-089`.
