---
id: ADR-0005
title: IPv4 only in v1, with explicit rejection of IPv6
status: Accepted
owner: Vinh Nguyen
created: 2026-08-03
updated: 2026-08-04
supersedes: []
superseded_by: null
affects: [SPEC-01, SPEC-02, SPEC-07]
---

# ADR-0005: IPv4 only in v1

## Context

Full IPv6 support is more than accepting a second address format. It brings:

- A parallel sysctl tree (`net.ipv6.conf.*`) with different semantics — enabling
  forwarding on an interface **disables Router Advertisement acceptance** on it
- A second address family in every nftables rule
- Link-local addresses, scopes, and Duplicate Address Detection
- A doubled test matrix

Present requirements fall entirely within IPv4.

## Alternatives considered

### A — Support IPv6 from v1
- For: no rework later
- Against: substantially widens scope for a requirement that does not exist; doubles the
  test matrix while the core is still unstable

### B — Accept IPv6 addresses and handle them best-effort
- For: superficially flexible
- Against: **worse than no support.** Operators configure IPv6, see no error, and discover
  in production that it does not work

### C — IPv4 only, with explicit rejection during validation
- For: unambiguous scope; failure surfaces immediately rather than during production
  debugging
- Against: operators needing IPv6 cannot use v1

## Decision

Adopt **alternative C**.

The important part is **how** the rejection happens: any IPv6 address or CIDR in a spec is
refused with reason `IPV6_NOT_SUPPORTED` and a clear message. Silent omission is
prohibited, and so is accepting the value without configuring it.

Partial support is worse than no support because it moves the cost of discovering the
limitation from the API call to a production debugging session.

## Consequences

### Positive
- The test matrix halves
- nftables rules and sysctl handling deal with one address family
- Errors are explicit rather than mysterious

### Negative — the price paid
- Unusable in IPv6-only or dual-stack environments
- Adding IPv6 later touches SPEC-01, SPEC-02 and SPEC-07

### Follow-on work
- `REQ-VAL-020`: validation rejects every IPv6 address
- User documentation states the limitation prominently rather than in a footnote

## Conditions for revisiting

A genuine requirement from operators running IPv6-only or dual-stack. Within the current
project scope this may never arise, which is an acceptable outcome.
