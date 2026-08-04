---
id: ADR-0004
title: BYOK by default for peer keys, server generation optional
status: Accepted
owner: Vinh Nguyen
created: 2026-08-03
updated: 2026-08-04
supersedes: []
superseded_by: null
affects: [SPEC-06]
---

# ADR-0004: BYOK by default for peer keys

## Context

WireGuard's core security property is that a private key never leaves the device that owns
it.

The original concept nonetheless required client config and QR code generation. Producing
a complete config file requires the client private key inside it, meaning the agent must
generate and therefore see it. The two requirements contradict each other and need an
explicit resolution rather than an ambiguous middle ground.

## Alternatives considered

### A — BYOK only
The client generates the keypair and sends only the public key.

- For: strictest option, matching WireGuard's security model
- Against: config and QR generation must move out of the agent to the platform layer,
  which is an even worse place to hold private keys

### B — Server generation as the primary path
- For: most convenient for the platform, QR codes work immediately
- Against: the agent becomes a concentration point of risk for every client private key

### C — Both modes, BYOK as the default
- For: the default path is the safe path, while QR flows remain possible
- Against: two code paths to test; the no-persistence rule must be strictly enforced

## Decision

Adopt **alternative C**, with mandatory constraints on the server-generated mode:

- Generated private keys **MUST NOT** be persisted — not to the store, the log or the
  audit log
- They appear only in the single peer-creation response; no API can retrieve them again
- The mode **MUST** be refused over an unencrypted transport
- A global flag can disable the mode entirely in hardened environments

The default configuration must be the safe configuration. Convenience requires a
deliberate opt-in.

## Consequences

### Positive
- Cautious operators never let the agent touch a client private key
- QR-based onboarding remains available for end-user VPN flows

### Negative — the price paid
- Two code paths through peer creation
- Automated tests must assert that keys never leak into any output; manual review is not
  sufficient assurance

### Follow-on work
- Logging must redact at the type level: secrets use a dedicated type whose `String()`
  returns `[REDACTED]`, making accidental logging impossible
- Tests must scan every response, log line and audit record for leaked keys

## Conditions for revisiting

If the server-generated mode goes unused over time, remove it to shrink the risk surface.
