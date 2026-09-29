---
id: SPEC-05
title: Security, authentication and authorization
prefix: SEC
status: Accepted
version: 1.8
owner: Vinh Nguyen
created: 2026-08-03
updated: 2026-09-29
depends_on: [SPEC-04]
adrs: [ADR-0007, ADR-0009]
milestone: M2
---

# SPEC-05: Security, authentication and authorization

## 1. Scope

Listeners, authentication, authorization, least-privilege execution, handling of sensitive
data.

**Not in this module:**
- WireGuard key generation and storage → [SPEC-06](SPEC-06-key-management.md)
- How these values are configured → [SPEC-09](SPEC-09-config-deployment.md)

## 2. Threat model

The agent runs with `CAP_NET_ADMIN` and can create tunnels into an internal network. An
exposed endpoint is equivalent to a compromised network.

The privilege boundary in v1 is **the host**. Both listeners are local, so a caller reaching
the API has already obtained execution on the node. The controls that matter are therefore
file permission on the socket and on the token file, plus role separation between callers —
not transport cryptography. [ADR-0009](../10-decisions/ADR-0009-local-only-listeners.md)
records the reasoning and the conditions for widening the boundary.

## 3. Listeners

> **REQ-SEC-001** — The default listener MUST be a unix socket.

> **REQ-SEC-060** — The agent MUST NOT listen on TCP by default.

> **REQ-SEC-003** — The unix socket MUST have mode `0660` with a configurable group.

> **REQ-SEC-070** — When the HTTP listener is enabled, the agent MUST refuse to start unless
> its bind address is a loopback address.

> **REQ-SEC-083** — When the metrics listener is enabled, the agent MUST refuse to start unless
> its bind address is a loopback address.

`REQ-SEC-083` exists because the metrics endpoint is not the harmless surface it looks like.
`REQ-OBS-002` exposes a series per peer, so the endpoint names every public key and handshake
time on the node — the same disclosure `REQ-DIA-024` keeps out of the overview, on a listener
that carries no token under `REQ-SEC-071`. `REQ-OBS-003` is the operator's opt-out, not the
reason the exposure exists.

`REQ-SEC-070` is a startup check rather than a runtime one, so a configuration error surfaces
at deployment instead of on first request.

## 4. Authentication

Each listener carries exactly one identity source. The unix socket derives identity from the
kernel through peer credentials; the HTTP listener derives it from a bearer token.

> **REQ-SEC-071** — Every request arriving on the HTTP listener other than the health
> endpoint MUST carry a bearer token in the `Authorization` header.

> **REQ-SEC-080** — The health endpoint MUST be served without authentication.

`REQ-SEC-080` keeps a liveness probe from needing a credential. The endpoint reports only
whether the agent is serving, so it discloses nothing an attacker on the host could not
observe from the process table. The node overview is a different matter and stays behind a
token, because it names interfaces, addresses and peer counts.

> **REQ-SEC-072** — The agent MUST refuse to start when the HTTP listener is enabled and no
> token is configured.

> **REQ-SEC-078** — A request whose token is missing or unrecognized MUST be rejected with
> `UNAUTHENTICATED` and reason `TOKEN_INVALID`.

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

> **REQ-SEC-081** — The agent MUST reload the token file on `SIGHUP`.

> **REQ-SEC-079** — The principal attributed to a request MUST be `unix/<uid>` on the unix
> socket and `token/<label>` on the HTTP listener.

`REQ-SEC-079` gives [SPEC-08](SPEC-08-observability.md) a stable audit identity from either
listener without exposing the token value.

A bearer token has no expiry and is not bound to a caller. Revocation is an edit to the token
file followed by a reload, which is proportionate while the trust boundary is the host.

## 5. Authorization

> **REQ-SEC-020** — The agent MUST support two roles.

| Role | Permitted |
|---|---|
| `reader` | Read-only RPCs: Get, List, Status, Diagnose, Overview, Health |
| `admin` | All operations |

> **REQ-SEC-077** — A caller connected over the unix socket MUST be granted the `admin` role.

> **REQ-SEC-021** — An identity absent from the role mapping MUST be rejected with
> `PERMISSION_DENIED`.

`REQ-SEC-077` places the access decision for the unix socket in the socket's mode and group
under `REQ-SEC-003`: membership of that group is the grant. Splitting local callers by role
needs the HTTP listener, where each token carries its own role.

Finer-grained RBAC belongs to the platform layer.

## 6. Least-privilege execution

> **REQ-SEC-030** — The agent MUST run under a non-root account holding only the
> `CAP_NET_ADMIN` ambient capability.

> **REQ-SEC-031** — The installation package MUST ship a hardened systemd unit.

> **REQ-SEC-062** — The installation package MUST NOT configure the agent to run as root.

Unit details and the `ProtectKernelTunables` trade-off are in
[SPEC-09](SPEC-09-config-deployment.md).

## 7. Prohibition on shell hooks

> **REQ-SEC-040** — The API MUST NOT expose any field accepting a shell command, an
> executable path, or command-line arguments.

> **REQ-SEC-041** — The agent MUST NOT execute a child process in any production path.

A child process may appear in test helpers only, never in a production path.

Full reasoning is in [ADR-0007](../10-decisions/ADR-0007-no-shell-hooks.md): an API that
accepts a shell string and runs it with `CAP_NET_ADMIN` is a remote code execution
endpoint, and no level of authentication changes that.

## 8. Sensitive data

| Data | Rule |
|---|---|
| Interface private key | Stored at `0600`. Never returned, never logged |
| Server-generated peer private key | Never stored. Returned once. Never logged |
| Preshared key | Stored. Never returned, never logged |
| API token | Stored at `0600`. Never returned, never logged |
| Public key | Not sensitive. Freely logged and returned |

> **REQ-SEC-050** — Sensitive values MUST be represented by a dedicated type whose `String()`
> returns `[REDACTED]`.

> **REQ-SEC-051** — The test suite MUST scan every response, log entry and audit record to
> assert no secret key is emitted.

`REQ-SEC-050` places redaction at the type level rather than relying on author discipline,
which is the only way to make it dependable.

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
