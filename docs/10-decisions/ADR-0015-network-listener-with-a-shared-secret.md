---
id: ADR-0015
title: A network listener authenticated by one shared secret
status: Proposed
owner: Vinh Nguyen
created: 2026-09-29
updated: 2026-09-29
supersedes: [ADR-0009]
superseded_by: null
affects: [SPEC-04, SPEC-05, SPEC-09, SPEC-12]
---

# ADR-0015: A network listener authenticated by one shared secret

## Context

[ADR-0009](ADR-0009-local-only-listeners.md) confines every v1 listener to the host and leaves a
remote platform to supply its own hop. The operator manages nodes remotely and asked for the REST
API of [ADR-0014](ADR-0014-rest-api-described-by-openapi.md) to be reachable directly, with
authentication by a simple secret.

Under [ADR-0013](ADR-0013-drive-wg-and-wg-quick.md) the agent runs as root and controls every
tunnel it created. Whoever holds the credential holds that control, so the credential and the
channel carrying it are the whole of the access boundary.

Measured on .NET 10 NativeAOT (SDK 10.0.401), with `TreatWarningsAsErrors` passed through to the
AOT compiler:

| Observation | Result |
|---|---|
| Kestrel HTTPS from a supplied PEM certificate and key | publishes with no warning; serves HTTP/1.1 and HTTP/2 over TLS |
| A self-signed certificate generated in-process at first start, ECDSA P-256 or RSA 2048 | serves; key written `0600`; reused unchanged on restart |
| Its SHA-256 fingerprint and SPKI pin computed in-process | match `openssl`; `curl --pinnedpubkey` accepts the pin and refuses a wrong one |
| Bearer check in constant time | missing, wrong or `Basic` → 401; correct → 200; health unauthenticated |
| OpenSSL at run time | `libssl.so.3` loaded on demand; `libssl3t64` present in the default Debian 13 and Ubuntu 24.04 images, pulled in by the essential `coreutils` |

## Alternatives considered

### A — Local only, remote through an SSH tunnel, as ADR-0009
- For: no credential of the agent's own; SSH already authenticates the operator
- Against: not what the operator asked for; every caller needs an SSH hop

### B — Mutual TLS
- For: the strongest option; no bearer secret to replay
- Against: a certificate authority, issuance and rotation — the opposite of simple

### C — One bearer secret, over TLS off loopback
- For: one value in one standard header; `curl` works; the channel protects the secret
- Against: a leaked secret is full control until rotated; one secret means one role

### D — One bearer secret over plaintext HTTP
- For: simplest to run
- Against: the secret crosses the network in clear, and it is root-equivalent on the node

## Decision

Adopt **C**.

- One secret, drawn from a cryptographically secure source with at least 256 bits of entropy,
  generated at installation, stored in a root-owned file of mode `0600`, and printed once by the
  command that generates it. A command replaces it; none prints it again.
- Every request except the health endpoint carries it as `Authorization: Bearer <secret>`,
  compared in constant time. A missing and a wrong secret are refused alike.
- The listener binds to one configured address. Plaintext HTTP is permitted on a loopback
  address only; any other address requires TLS.
- The certificate is one the operator supplies or, when none is configured, a self-signed one
  the agent generates on first start. The command that prints the secret also prints the
  certificate's fingerprint, which a client pins.
- Roles, several secrets and mutual TLS stay in the backlog.

## Consequences

### Positive
- A platform or a script reaches a node directly, with one header
- A misconfiguration cannot put the secret on the network in clear
- The token machinery of SPEC-05 shrinks to one secret without roles

### Negative — the price paid
- The secret is root-equivalent on the node; a leak is answered by rotating it, and nothing
  limits what a holder does before then
- TLS makes OpenSSL (`libssl3t64`) a runtime dependency of the package — present on every
  supported node already, and declared so the package says so
- A self-signed certificate asks every client to pin a fingerprint rather than trust a CA
- Exposure is only as narrow as the bind address and the host firewall make it

### Follow-on work
- Amend SPEC-05 sections 3 and 4 — `REQ-SEC-070` forbids a non-loopback bind — and the install
  flow of SPEC-09 and SPEC-12

## Conditions for revisiting

- A second caller needing narrower rights than full control — roles and several secrets.
- A platform with its own PKI — mutual TLS.
- A deployment where the node is reachable only through a bastion — the SSH route of ADR-0009.
