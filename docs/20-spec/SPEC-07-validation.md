---
id: SPEC-07
title: Validation
prefix: VAL
status: Accepted
version: 1.1
owner: Vinh Nguyen
created: 2026-08-03
updated: 2026-08-04
depends_on: [SPEC-01, SPEC-02]
adrs: [ADR-0005, ADR-0006]
milestone: M1
---

# SPEC-07: Validation

## 1. Scope

Every validation rule and its severity. This is the only place validation rules are
defined; other modules reference them by REQ ID.

## 2. Principles

The agent performs no IPAM. It does reject configurations that cause hard-to-diagnose
incidents, because detecting a fault at the API call is far cheaper than debugging it in
production.

> **REQ-VAL-001** — An error-severity finding MUST block the write and return the
> corresponding reason code.

> **REQ-VAL-002** — A warning-severity finding MUST appear in `status.conditions` without
> blocking the write.

## 3. Error severity

> **REQ-VAL-010** — The agent MUST reject an interface name not matching
> `^[a-zA-Z][a-zA-Z0-9_-]{0,14}$` with `INTERFACE_NAME_INVALID`.

> **REQ-VAL-011** — The agent MUST reject a public key that is not base64 of exactly 32
> bytes with `PUBLIC_KEY_INVALID`.

> **REQ-VAL-012** — The agent MUST reject two peers on one interface holding identical
> `allowed_ips` entries with `ALLOWED_IPS_DUPLICATE`.

Cryptokey routing would be ambiguous: the kernel applies longest-prefix matching, and for
two identical prefixes the later-configured peer silently displaces the earlier one.

> **REQ-VAL-013** — The agent MUST reject a `listen_port` already used by another managed
> interface with `LISTEN_PORT_IN_USE`.

> **REQ-VAL-014** — The agent MUST reject `addresses` overlapping another managed interface
> with `ADDRESS_CONFLICT`.

> **REQ-VAL-020** — The agent MUST reject any address or CIDR of the IPv6 family with
> `IPV6_NOT_SUPPORTED`.

Silent omission is prohibited, as is accepting the value without configuring it. Reasoning
in [ADR-0005](../10-decisions/ADR-0005-ipv4-only-in-v1.md).

> **REQ-VAL-021** — The agent MUST reject `external = ALLOW` combined with
> `nat.enable_uplink_forwarding = false` with `FORWARD_POLICY_NEEDS_UPLINK`.

The declaration is self-contradictory: policy permits egress while the kernel precondition
for egress is absent.

> **REQ-VAL-022** — The agent MUST reject `allowed_peer_interfaces` naming a nonexistent
> interface with `PEER_INTERFACE_NOT_FOUND`.

A typo here causes silent loss of connectivity, so it is caught at write time.

## 4. Warning severity

> **REQ-VAL-030** — The agent MUST warn when `allowed_ips` entries overlap at differing
> prefix lengths.

Longest-prefix matching makes this valid, but it usually indicates a mistake.

> **REQ-VAL-031** — The agent MUST warn when a peer's `allowed_ips` falls outside the
> interface subnet.

Valid for site-to-site, usually a mistake otherwise.

> **REQ-VAL-032** — The agent MUST warn when `mtu` falls outside the range 1280 to 1500.

> **REQ-VAL-033** — The agent MUST warn when `endpoint` is a hostname rather than an IP
> address.

The kernel stores only the resolved address, so a DNS change does not propagate.

> **REQ-VAL-023** — The agent MUST warn when an `inter_interface = ALLOW_LIST` relationship
> is declared in one direction only.

Valid and stateful per `REQ-FWD-017`, but usually a forgotten reciprocal declaration.

> **REQ-VAL-034** — The agent MUST warn when `allowed_peer_interfaces` is non-empty while
> `inter_interface != ALLOW_LIST`.

The field is ignored in that combination, which usually reflects a misunderstanding.

> **REQ-VAL-035** — The agent MUST warn when `external = ALLOW` is combined with
> `nat.enabled = false`.

Traffic leaves without source NAT, so return traffic almost certainly has no route back.
Syntactically valid, practically broken.

## 5. Summary

| Rule | REQ | Severity |
|---|---|---|
| Invalid interface name | REQ-VAL-010 | Error |
| Malformed public key | REQ-VAL-011 | Error |
| Duplicate `allowed_ips` | REQ-VAL-012 | Error |
| Duplicate `listen_port` | REQ-VAL-013 | Error |
| Overlapping `addresses` | REQ-VAL-014 | Error |
| IPv6 address | REQ-VAL-020 | Error |
| `external` without uplink forwarding | REQ-VAL-021 | Error |
| Unknown `allowed_peer_interfaces` entry | REQ-VAL-022 | Error |
| One-sided `inter_interface` | REQ-VAL-023 | Warning |
| Overlapping `allowed_ips` at differing prefixes | REQ-VAL-030 | Warning |
| `allowed_ips` outside the subnet | REQ-VAL-031 | Warning |
| MTU outside the usual range | REQ-VAL-032 | Warning |
| Hostname `endpoint` | REQ-VAL-033 | Warning |
| Redundant `allowed_peer_interfaces` | REQ-VAL-034 | Warning |
| `external` without NAT | REQ-VAL-035 | Warning |
