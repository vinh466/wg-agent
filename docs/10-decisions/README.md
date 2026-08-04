# Architecture Decision Records

Where the reasons behind the system's shape are recorded. Specs state *what*; ADRs explain
*why*.

## Rules

1. **An `Accepted` ADR is immutable.** Never edit its body. To change a decision, write a
   new ADR and mark the old one `superseded_by`.
2. **Record the rejected alternatives.** The greatest value of an ADR is that a later
   reader learns an option *was* considered and why it lost — otherwise they propose it
   again six months on.
3. **Always fill in "Conditions for revisiting".** A decision with no stated conditions for
   reversal becomes dogma instead of engineering.
4. **One ADR, one decision.** Bundling several decisions into one document removes the
   ability to supersede them independently.

## Index

| ID | Title | Status | Affects |
|---|---|---|---|
| [ADR-0001](ADR-0001-declarative-model.md) | Declarative model with a reconcile loop | Accepted | SPEC-01, SPEC-03, SPEC-04 |
| [ADR-0002](ADR-0002-netlink-over-wg-quick.md) | Netlink and wgctrl instead of wg-quick | Accepted | SPEC-01, SPEC-02, SPEC-03 |
| [ADR-0003](ADR-0003-protobuf-source-of-truth.md) | Protobuf as the source of truth | Accepted | SPEC-04 |
| [ADR-0004](ADR-0004-byok-by-default.md) | BYOK by default for peer keys | Accepted | SPEC-06 |
| [ADR-0005](ADR-0005-ipv4-only-in-v1.md) | IPv4 only in v1 | Accepted | SPEC-01, SPEC-02, SPEC-07 |
| [ADR-0006](ADR-0006-three-axis-forward-policy.md) | Three-axis forward policy | Accepted | SPEC-02, SPEC-07 |
| [ADR-0007](ADR-0007-no-shell-hooks.md) | No shell hooks exposed through the API | Accepted | SPEC-05 |
| [ADR-0008](ADR-0008-no-host-firewall-ownership.md) | The agent does not own the host firewall | Accepted | SPEC-02, SPEC-11 |

## Numbering

Numbers increase and are never reused. A superseded ADR keeps its number and stays in the
index — the history of decisions is itself valuable information.
