# Normative specification

> **This is the source of truth.** When any other document disagrees with this directory,
> this directory wins and the other document is the bug.

## How to read

### Normative keywords

Per RFC 2119, uppercase and in English so they stand apart from surrounding prose.

| Keyword | Meaning |
|---|---|
| **MUST** / **MUST NOT** | Absolute requirement. Violation is an implementation defect |
| **SHOULD** / **SHOULD NOT** | Strong recommendation. Deviation requires a recorded reason |
| **MAY** | Genuinely optional |

A sentence without a keyword is **explanatory**, not normative. Implementation never
follows explanatory prose — a behavior described only in prose is a spec defect and should
be reported.

### Identified requirements

Every normative statement carries a stable ID of the form `REQ-<AREA>-<NNN>` and is
presented like this:

> **REQ-XXX-001** — When `intra_interface = DENY`, the agent MUST drop packets whose input
> and output interface are both that interface.

The example above deliberately uses the placeholder area `XXX`: a real ID in a guidance
document would register as a duplicate definition.

Each requirement is **atomic** — one sentence, exactly one keyword — so it maps one-to-one
onto tests. Numbering rules are in [docs/README.md §5.4](../README.md): never reuse, never
reassign; dropped requirements are struck through rather than deleted.

### Traceability

| Direction | Method |
|---|---|
| Requirement → implementation | Grep the ID in the source tree |
| Requirement → test | Test names embed the ID: `TestForwardPolicy_IntraDeny_REQ_FWD_012` |
| Code → reason it exists | A comment cites the ID, the ID leads to the spec, the spec leads to an ADR |

CI verifies two properties: every ID referenced in code exists in the spec, and every
requirement marked `Implemented` has at least one referencing test.

## Module index

| ID | Module | Prefix | Status | Phase |
|---|---|---|---|---|
| [SPEC-01](SPEC-01-resource-model.md) | Resource model | `RES` | Implemented | P1 |
| [SPEC-02](SPEC-02-forward-policy.md) | Forward policy and NAT | `FWD` | Accepted | Backlog B-04 |
| [SPEC-03](SPEC-03-state-reconcile.md) | Desired state and reconcile | `RCN` | Implemented | P1; reconcile B-09, adoption B-10 |
| [SPEC-04](SPEC-04-api-conventions.md) | API conventions, concurrency, errors | `API` | Implemented | P2 |
| [SPEC-05](SPEC-05-security.md) | Security, authentication, authorization | `SEC` | Accepted | P1 to P3 |
| [SPEC-06](SPEC-06-key-management.md) | Key management | `KEY` | Implemented | P1 |
| [SPEC-07](SPEC-07-validation.md) | Validation | `VAL` | Implemented | P1 |
| [SPEC-08](SPEC-08-observability.md) | Metrics, logs, audit | `OBS` | Accepted | Logs P3; the rest B-01 |
| [SPEC-09](SPEC-09-config-deployment.md) | Configuration, packaging, deployment | `CFG` | Accepted | P1 to P3 |
| [SPEC-10](SPEC-10-lifecycle.md) | Lifecycle: upgrade, backup, DR | `LIF` | **Draft** | Backlog B-02 |
| [SPEC-11](SPEC-11-diagnostics.md) | Diagnostics and node overview | `DIA` | Accepted | Backlog B-06, B-10 |
| [SPEC-12](SPEC-12-cli.md) | Command line surface | `CLI` | Implemented | P1, P2 |
| [SPEC-13](SPEC-13-applying-changes.md) | Applying a change through wg and wg-quick | `APL` | Implemented | P1 |

The phases are those of the [roadmap](../60-planning/roadmap.md); a backlog entry names the
[backlog](../60-planning/backlog.md) block holding the module's deferred requirements.

SPEC-10 remains `Draft`. Three of its decisions are unsettled — export encryption,
partial import and downward migration — and all three wait with B-02. Approving a module whose
own text says *Undecided* would drain the status vocabulary of meaning, so it waits for those
answers.

## Dependency graph

Derived from the `depends_on` front-matter of each module. An arrow points from a module to the
one it depends on, so anything with no outgoing arrow can be read first.

```mermaid
graph RL
  SPEC01["SPEC-01<br/>Resource model<br/>(P1)"]
  SPEC04["SPEC-04<br/>API conventions<br/>(P2)"]
  SPEC02["SPEC-02<br/>Forward policy<br/>(backlog)"]
  SPEC03["SPEC-03<br/>State and reconcile<br/>(P1)"]
  SPEC05["SPEC-05<br/>Security<br/>(P1–P3)"]
  SPEC06["SPEC-06<br/>Key management<br/>(P1)"]
  SPEC07["SPEC-07<br/>Validation<br/>(P1)"]
  SPEC08["SPEC-08<br/>Observability<br/>(P3 logs)"]
  SPEC09["SPEC-09<br/>Config and deployment<br/>(P1–P3)"]
  SPEC10["SPEC-10<br/>Lifecycle<br/>(backlog · Draft)"]
  SPEC11["SPEC-11<br/>Diagnostics<br/>(backlog)"]
  SPEC12["SPEC-12<br/>CLI<br/>(P1–P2)"]
  SPEC13["SPEC-13<br/>Applying changes<br/>(P1)"]

  SPEC02 --> SPEC01
  SPEC03 --> SPEC01
  SPEC03 --> SPEC02
  SPEC04 --> SPEC01
  SPEC05 --> SPEC04
  SPEC06 --> SPEC01
  SPEC06 --> SPEC05
  SPEC07 --> SPEC01
  SPEC07 --> SPEC02
  SPEC08 --> SPEC01
  SPEC08 --> SPEC03
  SPEC09 --> SPEC05
  SPEC10 --> SPEC03
  SPEC10 --> SPEC06
  SPEC10 --> SPEC09
  SPEC11 --> SPEC02
  SPEC11 --> SPEC03
  SPEC12 --> SPEC04
  SPEC12 --> SPEC05
  SPEC12 --> SPEC09
  SPEC13 --> SPEC01
  SPEC13 --> SPEC03

  classDef draft stroke-dasharray: 5 5
  class SPEC10 draft
```

SPEC-01 carries no dependency, which is why it is read first. Two edges point from a P1 module
into the backlog: SPEC-03 and SPEC-07 depend on SPEC-02 through their deferred sections — the
reconcile steps that apply policy, and the policy validation rules — which stay inert until B-04
returns. Nothing P1 implements reads SPEC-02.

## Module boundaries

To prevent duplication, each topic has exactly one home:

| Topic | Belongs to | Does not belong to |
|---|---|---|
| Interface and peer fields | SPEC-01 | — |
| Default field values | SPEC-01 | SPEC-09 (which covers overriding only) |
| nftables rules, sysctl | SPEC-02 | SPEC-03 (which only invokes them) |
| Reconcile algorithm | SPEC-03 | — |
| How a change reaches WireGuard: files, units, synchronisation, restarts | SPEC-13 | SPEC-03 (which covers the store, the lock and the deferred reconcile) |
| Field ownership during reconcile | SPEC-03 | SPEC-01 |
| RPC shapes, error codes, revisions | SPEC-04 | — |
| Listeners, authentication, roles | SPEC-05 | SPEC-09 (which covers configuration only) |
| Key generation, rotation, storage | SPEC-06 | — |
| Validation rules and severities | SPEC-07 | Other modules reference only |
| Metric names, log fields | SPEC-08 | — |
| Configuration keys, systemd, packaging | SPEC-09 | SPEC-12 (which covers commands only) |
| Backup, upgrade, migration | SPEC-10 | — |
| Adoption and release of an existing interface | SPEC-03 | SPEC-10 (which covers import of an export file only) |
| Diagnostic check list, node overview | SPEC-11 | SPEC-08 (which covers continuous signals) |
| CLI subcommands, token issuance | SPEC-12 | SPEC-05 (which covers token semantics) |

When the correct module is unclear, place the requirement where a reader would look first
and cross-reference from the other location.
