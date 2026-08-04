---
id: SPEC-06
title: Key management
prefix: KEY
status: Accepted
version: 1.1
owner: Vinh Nguyen
created: 2026-08-03
updated: 2026-08-04
depends_on: [SPEC-01, SPEC-05]
adrs: [ADR-0004]
milestone: M1
---

# SPEC-06: Key management

## 1. Scope

Generation, storage, rotation and export of WireGuard keys. Client config and QR code
generation.

**Not in this module:**
- API TLS certificates → [SPEC-05](SPEC-05-security.md)
- Backing up the store that holds keys → [SPEC-10](SPEC-10-lifecycle.md)

## 2. Interface keys

> **REQ-KEY-001** — When `private_key` is omitted at interface creation, the agent MUST
> generate one from a cryptographically secure source.

> **REQ-KEY-002** — The agent MUST NOT return an interface private key in any response.

`GetInterface` returns `status.public_key` only.

> **REQ-KEY-003** — `RotateInterfaceKey` MUST generate a new key, apply it atomically, and
> return the new public key.

> **REQ-KEY-004** — The `RotateInterfaceKey` response MUST carry an explicit warning that
> the operation disconnects every peer until each is updated with the new public key.

Coordinating peer updates belongs to the platform layer. The zero-downtime rotation
procedure is documented in [50-guides](../50-guides/README.md).

## 3. Peer keys

### 3.1. BYOK — the default

> **REQ-KEY-010** — The default peer creation mode MUST be BYOK, where the caller supplies a
> public key and the agent never learns the private key.

### 3.2. Server-generated mode

> **REQ-KEY-011** — When `generate_keypair` is true, the agent MUST return the private key in
> that single response.

> **REQ-KEY-012** — A generated private key MUST NOT be written to the store, the log, or the
> audit log.

> **REQ-KEY-013** — The agent MUST NOT expose any API that returns an already generated
> private key.

> **REQ-KEY-014** — The agent MUST reject this mode with `PLAINTEXT_TRANSPORT_REJECTED` over
> an unencrypted transport.

> **REQ-KEY-015** — The agent MUST support a configuration flag that disables this mode
> entirely.

### 3.3. Preshared keys

> **REQ-KEY-020** — A preshared key MUST be write-only.

> **REQ-KEY-021** — When `generate_preshared_key` is true, the agent MUST return the generated
> value exactly once under the same constraints as `REQ-KEY-011` through `REQ-KEY-014`.

## 4. Client config generation

Client-side `AllowedIPs` determines which traffic a client sends into the tunnel and which
inbound traffic it accepts. Computing it incorrectly causes silent loss of connectivity, so
the agent derives it rather than leaving each caller to work it out.

> **REQ-KEY-030** — The agent MUST support the client routing modes listed below.

| Mode | Generated `AllowedIPs` |
|---|---|
| `AUTO` (default) | Derived from `forward_policy` per `REQ-KEY-031` |
| `SERVER_ONLY` | The interface server address as a `/32` |
| `LOCAL_SUBNET` | The interface's own subnet |
| `FULL_TUNNEL` | `0.0.0.0/0` |
| `CUSTOM` | Exactly the supplied `extra_allowed_ips` |

> **REQ-KEY-031** — In `AUTO` mode the agent MUST derive `AllowedIPs` by the rule below.

```
always                         → <interface server address>/32
intra_interface = ALLOW        → + the interface's own subnet
inter_interface = ALLOW        → + the subnet of every other managed interface
inter_interface = ALLOW_LIST   → + the subnets of allowed_peer_interfaces
external        = ALLOW        → + 0.0.0.0/0
```

> **REQ-KEY-032** — When the result contains `0.0.0.0/0`, the agent MUST omit narrower
> entries it subsumes.

> **REQ-KEY-033** — The response MUST include `derived_allowed_ips` and a
> `routing_explanation` describing how the result was reached.

> **REQ-KEY-034** — The agent MUST write the requested `dns` values into the generated
> `.conf` file only.

The agent never alters host DNS configuration.

> **REQ-KEY-035** — The generated config file MUST follow the wg-quick `.conf` format so any
> WireGuard client can read it.

Generated `AllowedIPs` is configuration advice, not enforcement — a client can widen it
freely. The only security boundary is server-side `forward_policy`.

## 5. Open questions

- Envelope encryption of stored keys through an external KMS. Present protection is file
  mode `0600` plus a dedicated account, proportionate because anyone holding
  `CAP_NET_ADMIN` can read keys from the kernel directly. Revisit under a compliance
  requirement. See [open questions](../60-planning/open-questions.md), OQ-06.
