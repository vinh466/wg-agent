---
updated: 2026-08-04
---

# Open questions

Decisions not yet made. Every entry carries an **owner** and a **deadline** — an open question
without a deadline stays open forever.

Reviewed weekly. Once settled: write an ADR if the decision is architectural, update the spec,
then delete the entry from this list.

---

## Blocking the current milestone

### OQ-01 — How are mTLS certificates issued and rotated?
**Relates to:** [SPEC-05](../20-spec/SPEC-05-security.md) · **Settle before:** M2

The spec assumes certificates already exist at configured paths. Unanswered: who issues them,
how the platform learns an agent's identity, and what the node enrollment procedure is.

**Recommendation:** keep the agent **passive** — no phone-home, no self-enrollment. An operator
or PKI issues certificates through configuration management. The agent offers only a
`wg-agent tls fingerprint` command for out-of-band verification. Automated enrollment moves to
the deferred list.

---

### OQ-02 — Zero-downtime server key rotation
**Relates to:** [SPEC-06](../20-spec/SPEC-06-key-management.md) · **Settle before:** M3

`RotateInterfaceKey` disconnects every peer. WireGuard cannot hold two server keys on one
interface simultaneously.

**Recommendation:** document the **parallel-run** procedure as an operational guide: create a new
interface on a different port, re-add the peers (client public keys are unchanged and therefore
reusable), distribute new configs, then delete the old interface. This is documentation, not an
agent feature.

---

### OQ-03 — Request rate limiting
**Relates to:** [SPEC-04](../20-spec/SPEC-04-api-conventions.md) · **Settle before:** M2

The error model defines `RESOURCE_EXHAUSTED` but nothing produces it.

**Recommendation:** with mTLS plus an identity allowlist the exposure is low. Either implement a
simple per-principal limit, or declare it out of scope for v1 — and in that case remove the
error code rather than leaving one that never occurs.

---

### OQ-04 — Confirm scale targets by benchmark
**Relates to:** [SPEC-10](../20-spec/SPEC-10-lifecycle.md) · **Settle before:** M1

`REQ-LIF-040` proposes 50 interfaces per node, 2,000 peers per interface, 10,000 peers per node,
and full reconcile under 2 seconds. None of it is verified.

Benchmark results determine the default `reconcile.interval` and the recommended threshold for
disabling `metrics.per_peer`.

---

### OQ-05 — `ProtectKernelTunables` across target systemd versions
**Relates to:** [SPEC-09](../20-spec/SPEC-09-config-deployment.md) · **Settle before:** M2

`REQ-CFG-011` proposes `ProtectKernelTunables=yes` combined with
`ReadWritePaths=/proc/sys/net/ipv4/conf`. Carve-out behavior for `/proc/sys` is inconsistent
across systemd versions.

This needs real verification on Debian 11/12/13 and Ubuntu 20.04/22.04/24.04. It is a genuine
hardening risk, not a formality.

---

## Non-blocking

### OQ-06 — Should state exports be passphrase-encrypted?
**Relates to:** [SPEC-10](../20-spec/SPEC-10-lifecycle.md) · **Settle before:** M3

An export file contains interface private keys. Is mode `0600` sufficient, or is
application-level encryption needed?

---

### OQ-07 — Partial state import
**Relates to:** [SPEC-10](../20-spec/SPEC-10-lifecycle.md) · **Settle before:** M3

Whole-store import only, or the ability to import a single interface?

---

### OQ-08 — Downward schema migration
**Relates to:** [SPEC-10](../20-spec/SPEC-10-lifecycle.md) · **Settle before:** M3

Support schema downgrade, or rely solely on the pre-migration backup? The latter is
substantially simpler.

---

### OQ-09 — Per-peer diagnostics
**Relates to:** [SPEC-11](../20-spec/SPEC-11-diagnostics.md) · **Settle before:** M1

Diagnostics operate at interface granularity. Is per-peer granularity worth adding?

---

### OQ-10 — Is `hint` free-form text or an identifier?
**Relates to:** [SPEC-11](../20-spec/SPEC-11-diagnostics.md) · **Settle before:** M1

An identifier lets the platform localize the message; free-form text is easier to author. Both
are possible: an identifier plus a default string.

---

### OQ-11 — Re-derive `masquerade_out_interface` when the default route changes?
**Relates to:** [SPEC-02](../20-spec/SPEC-02-forward-policy.md) · **Settle before:** M4

Derivation happens during reconcile only. A default route change leaves the masquerade rule
pointing at the wrong interface.

---

### OQ-12 — DNS re-resolution for hostname endpoints
**Relates to:** [SPEC-01](../20-spec/SPEC-01-resource-model.md) · **Settle before:** after v1

The kernel stores only the resolved address, so a DNS change silently disconnects the peer.
Validation warns about it today (`REQ-VAL-033`).

A caution for whoever implements this: it must not conflict with `REQ-RCN-051`, which forbids
overwriting an endpoint the kernel has learned. DNS re-resolution and roaming are two mechanisms
that easily fight each other.
