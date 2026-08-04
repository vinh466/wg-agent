---
updated: 2026-08-04
---

# Open questions

Decisions not yet made. Every entry carries an **owner** and a **deadline** — an open question
without a deadline stays open forever.

Reviewed weekly. Once settled: write an ADR if the decision is architectural, update the spec,
then delete the entry from this list.

Settled entries are not archived here. OQ-01 through OQ-05 closed on 2026-08-04 and OQ-09 with
OQ-10 on 2026-08-05; the outcomes live in
[ADR-0009](../10-decisions/ADR-0009-local-only-listeners.md),
[ADR-0010](../10-decisions/ADR-0010-install-script-over-released-deb.md), `REQ-LIF-040`,
`REQ-CFG-013`, `REQ-DIA-030`, `REQ-DIA-031`, and the scope table in
[product.md](../00-overview/product.md).

---

## Blocking the current milestone

None.

---

## Non-blocking

Every remaining entry belongs to SPEC-10 or SPEC-02 and sits at M3 or later, outside the MVP.

### OQ-06 — Should state exports be passphrase-encrypted?
**Owner:** Vinh Nguyen · **Relates to:** [SPEC-10](../20-spec/SPEC-10-lifecycle.md) ·
**Settle before:** M3

An export file contains interface private keys. Is mode `0600` sufficient, or is
application-level encryption needed?

---

### OQ-07 — Partial state import
**Owner:** Vinh Nguyen · **Relates to:** [SPEC-10](../20-spec/SPEC-10-lifecycle.md) ·
**Settle before:** M3

Whole-store import only, or the ability to import a single interface?

---

### OQ-08 — Downward schema migration
**Owner:** Vinh Nguyen · **Relates to:** [SPEC-10](../20-spec/SPEC-10-lifecycle.md) ·
**Settle before:** M3

Support schema downgrade, or rely solely on the pre-migration backup? The latter is
substantially simpler.

---

### OQ-11 — Re-derive `masquerade_out_interface` when the default route changes?
**Owner:** Vinh Nguyen · **Relates to:** [SPEC-02](../20-spec/SPEC-02-forward-policy.md) ·
**Settle before:** M4

Derivation happens during reconcile only. A default route change leaves the masquerade rule
pointing at the wrong interface.

---

### OQ-12 — DNS re-resolution for hostname endpoints
**Owner:** Vinh Nguyen · **Relates to:** [SPEC-01](../20-spec/SPEC-01-resource-model.md) ·
**Settle before:** after v1

The kernel stores only the resolved address, so a DNS change silently disconnects the peer.
Validation warns about it today (`REQ-VAL-033`).

A caution for whoever implements this: it must not conflict with `REQ-RCN-051`, which forbids
overwriting an endpoint the kernel has learned. DNS re-resolution and roaming are two mechanisms
that easily fight each other.
