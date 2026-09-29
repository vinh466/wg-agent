---
id: ADR-0009
title: Local-only management listeners in v1
status: Superseded
owner: Vinh Nguyen
created: 2026-08-04
updated: 2026-09-29
supersedes: []
superseded_by: ADR-0015
affects: [SPEC-04, SPEC-05, SPEC-06, SPEC-09]
---

# ADR-0009: Local-only management listeners in v1

## Context

The original transport design offered a unix socket by default and an optional TCP listener
protected by mTLS, intended so that a platform on another host could manage a node directly.

That design carries an unsolved dependency: certificate issuance, distribution and rotation,
plus a node enrollment procedure. None of it exists, and building it is a larger effort than
the agent features it protects.

The v1 deployment target is a node reached from the same host — through an operator session,
a co-located platform component, or a reverse proxy. Cross-host management is not required to
ship v1.

## Alternatives considered

### A — Unix socket plus TCP with mTLS (the original design)
- For: a platform reaches any node directly; identity is cryptographic and per-caller
- Against: requires a PKI, an enrollment procedure and certificate rotation before the first
  node runs, which is the largest single block of work in M2

### B — Unix socket only
- For: smallest attack surface; access control reduces to file mode and group membership
- Against: every client needs unix-socket support, which excludes ordinary HTTP tooling and
  most reverse proxies without an adapter

### C — Unix socket plus an HTTP listener bound to loopback
- For: ordinary HTTP tooling works; the listener never leaves the host, so transport
  encryption adds nothing against an attacker who is not already on the machine; a static
  token assigns a role per caller without a PKI
- Against: a token is a bearer credential with no expiry semantics, and any process on the
  host able to read the token file gains that caller's role

## Decision

Adopt **C**.

The trust boundary in v1 is the host, not the network. A caller able to open a loopback
connection or the unix socket has already crossed that boundary, so the mechanism that
matters is file permission on the socket and on the token file rather than transport
cryptography. Deferring mTLS removes the PKI dependency from the critical path without
weakening a boundary that v1 defends.

Remote management over TCP with mTLS stays the intended shape for a later version. This
decision governs when it is built, not whether.

## Consequences

### Positive
- M2 ships with no PKI, no enrollment procedure and no certificate rotation
- The startup path loses certificate loading, expiry checking and `SIGHUP` reload
- Authorization has exactly one identity source per listener: peer credentials on the unix
  socket, a token on the HTTP listener

### Negative — the price paid
- A platform on another host cannot reach the agent without a transport hop it supplies
  itself, such as an SSH tunnel or a co-located component
- Bearer tokens carry no expiry and are not bound to a caller; revocation means editing
  configuration and reloading
- The deployment model assumed by [ADR-0007](ADR-0007-no-shell-hooks.md) names TCP with mTLS
  as primary. That assumption no longer holds, while its conclusion does: an API accepting a
  shell string is a remote code execution endpoint whatever the transport

### Follow-on work
- SPEC-05 drops the mTLS requirement group and gains the token requirements
- SPEC-09 replaces the `server.tcp` configuration block with `server.http`
- SPEC-04 replaces `RESOURCE_EXHAUSTED` with `UNAUTHENTICATED` in the error model

## Conditions for revisiting

Any one of the following:

- A deployment requires a platform on another host to manage nodes without an
  operator-supplied tunnel
- An issuing CA or a SPIFFE workload API becomes available in the target environment
- A compliance requirement demands per-caller cryptographic identity or credential expiry
