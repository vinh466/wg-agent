---
id: SPEC-05
title: Security, authentication and authorization
prefix: SEC
status: Accepted
version: 1.1
owner: Vinh Nguyen
created: 2026-08-03
updated: 2026-08-04
depends_on: [SPEC-04]
adrs: [ADR-0007]
milestone: M2
---

# SPEC-05: Security, authentication and authorization

## 1. Scope

Listeners, mTLS, authorization, least-privilege execution, handling of sensitive data.

**Not in this module:**
- WireGuard key generation and storage → [SPEC-06](SPEC-06-key-management.md)
- How these values are configured → [SPEC-09](SPEC-09-config-deployment.md)

## 2. Threat model

The agent runs with `CAP_NET_ADMIN` and can create tunnels into an internal network. An
exposed endpoint is equivalent to a compromised network.

The core privilege boundary: **a client calling the API over the network is not root on the
node.** Every decision in this module follows from that boundary.

## 3. Listeners

> **REQ-SEC-001** — The default listener MUST be a unix socket.

> **REQ-SEC-060** — The agent MUST NOT listen on TCP by default.

> **REQ-SEC-002** — The agent MUST NOT bind `0.0.0.0` by default in any configuration.

> **REQ-SEC-003** — The unix socket MUST have mode `0660` with a configurable group.

> **REQ-SEC-004** — When TCP is enabled, mTLS MUST be enabled unless the explicit
> `--insecure-no-auth` flag is set.

> **REQ-SEC-005** — With `--insecure-no-auth`, the agent MUST refuse to start when the bind
> address is not a loopback address.

> **REQ-SEC-061** — With `--insecure-no-auth`, the agent MUST emit a periodic `WARN` log
> entry for the lifetime of the process.

## 4. mTLS

> **REQ-SEC-010** — TLS MUST be version 1.3 or later.

> **REQ-SEC-011** — The agent MUST verify client certificates against the configured CA
> bundle.

> **REQ-SEC-012** — Client identity MUST be taken from the SPIFFE URI SAN when present and
> from the Subject CN otherwise.

> **REQ-SEC-013** — The agent SHOULD reload certificates on `SIGHUP` to support short-lived
> certificate rotation.

> **REQ-SEC-014** — When `allowed_client_identities` is non-empty, the agent MUST reject
> identities outside the list even when the certificate is otherwise valid.

## 5. Authorization

> **REQ-SEC-020** — The agent MUST support two roles.

| Role | Permitted |
|---|---|
| `reader` | Read-only RPCs: Get, List, Status, Watch, Diagnose, Health |
| `admin` | All operations |

> **REQ-SEC-021** — An identity absent from the role mapping MUST be rejected with
> `PERMISSION_DENIED`.

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

`exec.Command` may appear in test helpers only.

Full reasoning is in [ADR-0007](../10-decisions/ADR-0007-no-shell-hooks.md): an API that
accepts a shell string and runs it with `CAP_NET_ADMIN` is a remote code execution
endpoint, and no level of authentication changes that.

## 8. Sensitive data

| Data | Rule |
|---|---|
| Interface private key | Stored at `0600`. Never returned, never logged |
| Server-generated peer private key | Never stored. Returned once. Never logged |
| Preshared key | Stored. Never returned, never logged |
| Public key | Not sensitive. Freely logged and returned |

> **REQ-SEC-050** — Sensitive values MUST be represented by a dedicated type whose `String()`
> returns `[REDACTED]`.

> **REQ-SEC-051** — The test suite MUST scan every response, log entry and audit record to
> assert no secret key is emitted.

`REQ-SEC-050` places redaction at the type level rather than relying on author discipline,
which is the only way to make it dependable.

## 9. Open questions

- Certificate issuance and rotation for mTLS. The agent assumes certificates already exist
  at configured paths; node enrollment is undecided. See
  [open questions](../60-planning/open-questions.md), OQ-01.
