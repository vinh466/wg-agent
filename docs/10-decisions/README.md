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
| [ADR-0002](ADR-0002-netlink-over-wg-quick.md) | Netlink and wgctrl instead of wg-quick | Superseded by [ADR-0012](ADR-0012-managed-netlink-in-dotnet.md) | SPEC-01, SPEC-02, SPEC-03 |
| [ADR-0003](ADR-0003-protobuf-source-of-truth.md) | Protobuf as the source of truth | Superseded by [ADR-0014](ADR-0014-rest-api-described-by-openapi.md) | SPEC-04 |
| [ADR-0004](ADR-0004-byok-by-default.md) | BYOK by default for peer keys | Accepted | SPEC-06 |
| [ADR-0005](ADR-0005-ipv4-only-in-v1.md) | IPv4 only in v1 | Accepted | SPEC-01, SPEC-02, SPEC-07 |
| [ADR-0006](ADR-0006-three-axis-forward-policy.md) | Three-axis forward policy | Accepted | SPEC-02, SPEC-07 |
| [ADR-0007](ADR-0007-no-shell-hooks.md) | No shell hooks exposed through the API | Accepted | SPEC-05 |
| [ADR-0008](ADR-0008-no-host-firewall-ownership.md) | The agent does not own the host firewall | Accepted | SPEC-02, SPEC-11 |
| [ADR-0009](ADR-0009-local-only-listeners.md) | Local-only management listeners in v1 | Superseded by [ADR-0015](ADR-0015-network-listener-with-a-shared-secret.md) | SPEC-04, SPEC-05, SPEC-06, SPEC-09 |
| [ADR-0010](ADR-0010-install-script-over-released-deb.md) | Distribution by install script over a released `.deb` | Accepted | SPEC-09, SPEC-12 |
| [ADR-0011](ADR-0011-operator-initiated-adoption.md) | Operator-initiated adoption of a pre-existing interface | Accepted | SPEC-01, SPEC-02, SPEC-03, SPEC-04, SPEC-07, SPEC-11, SPEC-12 |
| [ADR-0012](ADR-0012-managed-netlink-in-dotnet.md) | Managed netlink in .NET, carrying ADR-0002 forward | Superseded by [ADR-0013](ADR-0013-drive-wg-and-wg-quick.md) | SPEC-01, SPEC-02, SPEC-03, SPEC-06, SPEC-09 |
| [ADR-0013](ADR-0013-drive-wg-and-wg-quick.md) | Drive WireGuard through wg and wg-quick in v1 | Accepted | SPEC-01, SPEC-02, SPEC-03, SPEC-05, SPEC-06, SPEC-09, SPEC-12 |
| [ADR-0014](ADR-0014-rest-api-described-by-openapi.md) | A REST API whose contract is an OpenAPI document | Accepted | SPEC-01, SPEC-04, SPEC-12 |
| [ADR-0015](ADR-0015-network-listener-with-a-shared-secret.md) | A network listener authenticated by one shared secret | Accepted | SPEC-04, SPEC-05, SPEC-09, SPEC-12 |

## Numbering

Numbers increase and are never reused. A superseded ADR keeps its number and stays in the
index — the history of decisions is itself valuable information.
