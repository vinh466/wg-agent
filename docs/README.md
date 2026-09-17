# wg-agent — Documentation map

> **Single entry point.** Every document in this project lives under `docs/` and is
> reachable from this page.

This is a **spec-first** project: the specification is authored and approved before
implementation, and code traces back to it.

Contributor rules: [CONTRIBUTING.md](CONTRIBUTING.md) · Agent rules: [CLAUDE.md](../CLAUDE.md)

---

## 1. Founding principles

Four rules determine the entire structure. Violating them is how documentation rots.

### 1.1. Split by content *type*, not by feature

Five content types have different change rates and different audiences, so they live in
different places:

| Type | Answers | Lifetime | Directory |
|---|---|---|---|
| **Decision** | Why was this chosen? | Immutable once accepted | `10-decisions/` |
| **Normative** | What MUST the system do? | Changes with releases | `20-spec/` |
| **Explanation** | How does it work? | Stable, teaching-oriented | `40-concepts/` |
| **Guide** | How do I do X? | Changes with operational reality | `50-guides/` |
| **Planning** | When, and who? | High churn | `60-planning/` |

A document that answers more than one of these questions must be split.

### 1.2. Only `20-spec/` is normative

`20-spec/` is the **single source of truth**. Every other directory is informative and
references it.

> When two documents disagree, **the spec wins** and the other document is the bug.

### 1.3. One fact, one home

Never copy content between documents — link instead. A number, a rule, a default value
is defined in exactly **one** place.

Duplication is debt: two copies drift apart and nobody knows which is right.

### 1.4. Every normative statement has a stable ID

Each requirement in `20-spec/` carries an ID of the form `REQ-<AREA>-<NNN>`. Tests and
code reference that ID, making traceability bidirectional: from requirement to
implementation, and from a line of code back to the reason it exists.

This is what turns spec-first into a mechanism rather than a documentation ritual.

---

## 2. Structure

```
docs/
├── README.md              ← this page
├── CONTRIBUTING.md        Rules and checklists for changing docs
├── check-docs.sh          Automated integrity and style checks
├── check-traceability.sh  Code and test references resolve to a requirement
├── check-mermaid.sh       Every diagram parses and renders
│
├── 00-overview/           Product positioning, architecture, glossary
├── 10-decisions/          ADRs — architectural decisions, append-only
├── 20-spec/               ★ NORMATIVE — the source of truth
├── 30-api/                API contract (generated from .proto, never hand-edited)
├── 40-concepts/           Explanations — how the system works
├── 50-guides/             Task-oriented operational guides
├── 60-planning/           Roadmap, milestones, risks, open questions, backlog
├── 90-rfcs/              Proposals under discussion, not yet accepted
└── 99-archive/            Superseded documents, kept for history
```

Numeric prefixes exist for one reason: they make filesystem order match reading order.
Without them `ls` returns `api, concepts, decisions, guides, overview, ...`, which puts
the API contract in front of the product definition.

---

## 3. Quick navigation

### New to the project
1. [Product positioning](00-overview/product.md) — what wg-agent is and is not
2. [Architecture](00-overview/architecture.md) — layers and boundaries
3. [Connectivity model](40-concepts/connectivity-model.md) — the hardest concept
4. [Decision index](10-decisions/README.md) — why the system has this shape

### Writing code
1. [How to read the spec](20-spec/README.md) — RFC 2119 conventions, REQ ID system
2. The spec module covering the area under work
3. [Roadmap](60-planning/roadmap.md) — current milestone

### Operating a node
1. [Guides](50-guides/README.md) — install, listeners and tokens, diagnostics
2. [Topology patterns](50-guides/topology-patterns.md) — pick a configuration

### Proposing a change
→ [Section 4](#4-spec-first-workflow)

---

## 4. Spec-first workflow

```
   Idea
     │
     ▼
 ┌─────────────┐   Unclear or contentious?
 │  90-rfcs/   │◄── Write an RFC, discuss, settle
 └──────┬──────┘    (skip for small, obvious changes)
        │
        ▼
 ┌─────────────┐   Architectural trade-off?
 │10-decisions/│◄── Record an ADR: context, alternatives, consequences
 └──────┬──────┘    ADRs are immutable — supersede, never edit
        │
        ▼
 ┌─────────────┐   ★ MANDATORY before implementation
 │  20-spec/   │◄── Add or amend a REQ with an ID
 └──────┬──────┘
        │
        ▼
 ┌─────────────┐
 │  .proto     │◄── Update the contract, regenerate 30-api/
 └──────┬──────┘
        │
        ▼
 ┌─────────────┐
 │ Impl + test │◄── Each test names the REQ ID it verifies
 └──────┬──────┘
        │
        ▼
 ┌─────────────┐
 │40-concepts/ │◄── Update explanations and guides if behavior changed
 │  50-guides/ │
 └─────────────┘
```

**No code without a REQ ID.** Work that needs behavior absent from the spec stops and
extends the spec first.

---

## 5. Conventions

### 5.1. Front-matter

Every document in `10-decisions/`, `20-spec/` and `90-rfcs/` opens with a YAML block:

```yaml
---
id: SPEC-02
title: Forward policy
status: Accepted
version: 1.0
owner: Vinh Nguyen
created: 2026-08-03
updated: 2026-08-04
depends_on: [SPEC-01]
adrs: [ADR-0006]
milestone: M4
---
```

### 5.2. Status lifecycle

| Status | Meaning | Implementable? |
|---|---|---|
| `Draft` | Being written, unstable | No |
| `Review` | Complete, awaiting approval | No |
| `Accepted` | Approved and normative | Yes |
| `Implemented` | Built and covered by tests | Running |
| `Superseded` | Replaced — links to its successor | Do not use |

### 5.3. Normative keywords

`20-spec/` uses RFC 2119: **MUST**, **MUST NOT**, **SHOULD**, **SHOULD NOT**, **MAY** —
uppercase, in English, to stand apart from surrounding prose.

Prose without a keyword is explanatory and is never implemented.

### 5.4. REQ ID system

Format `REQ-<AREA>-<NNN>`.

| AREA | Scope | Module |
|---|---|---|
| `RES` | Resource model | SPEC-01 |
| `FWD` | Forward policy, NAT | SPEC-02 |
| `RCN` | Desired state, reconcile | SPEC-03 |
| `API` | API conventions, concurrency, errors | SPEC-04 |
| `SEC` | Security, authentication, authorization | SPEC-05 |
| `KEY` | Key management | SPEC-06 |
| `VAL` | Validation | SPEC-07 |
| `OBS` | Metrics, logs, audit | SPEC-08 |
| `CFG` | Configuration, packaging, deployment | SPEC-09 |
| `LIF` | Lifecycle: upgrade, backup, DR | SPEC-10 |
| `DIA` | Diagnostics | SPEC-11 |
| `CLI` | Command line surface | SPEC-12 |

Numbering rules:

- Numbers are **never reused**, even after a requirement is removed
- Numbers are **never reassigned** to insert a requirement — always take the next free one
- A new requirement group opens the next free decade, so a section reads as a block
- A dropped requirement is struck through as `~~**REQ-XXX-NNN**~~ (removed in v1.2)`, not deleted,
  with the emphasis inside the strikethrough so `check-docs.sh` still counts the ID as defined
- `XXX` is reserved for examples in guidance documents and is never a real area

IDs appear in test names, code comments, commit messages and issues. Reassigning a
number breaks every historical reference.

### 5.5. File naming

| Directory | Pattern | Example |
|---|---|---|
| `10-decisions/` | `ADR-NNNN-short-slug.md` | `ADR-0002-netlink-over-wg-quick.md` |
| `20-spec/` | `SPEC-NN-topic.md` | `SPEC-02-forward-policy.md` |
| `90-rfcs/` | `RFC-NNNN-short-slug.md` | `RFC-0001-node-enrollment.md` |
| Elsewhere | `kebab-case.md` | `connectivity-model.md` |

### 5.6. Cross-references

Use relative paths including the `.md` extension. Cite a requirement by ID rather than
copying its text:

```markdown
✅ Peers are isolated per REQ-FWD-012.
❌ Peers are isolated because the agent adds `iif wg0 oif wg0 drop`.
```

The second form drifts out of sync the first time the spec changes.

---

## 6. Maintenance

| Task | Cadence | Owner |
|---|---|---|
| Reconcile every spec `status` against the code | End of each milestone | Owner |
| Review [open questions](60-planning/open-questions.md) | Weekly | Owner |
| Integrity and style checks | CI, every PR | `check-docs.sh` |
| Traceability between code and requirements | CI, every PR | `check-traceability.sh` |
| Diagrams parse and render | CI, every PR | `check-mermaid.sh` |
| Move superseded documents to `99-archive/` | As needed | Whoever supersedes |

```bash
docs/check-docs.sh          # exit 0 = pass
docs/check-traceability.sh
docs/check-mermaid.sh       # needs Docker; skips without it
```

The automated checks are mandatory. Without them the rules above are only promises.
Seven checks run in `check-docs.sh`: links, duplicate IDs, orphan IDs, banned
vocabulary, requirement format, length caps, front-matter. Details in
[CONTRIBUTING.md](CONTRIBUTING.md).

`check-mermaid.sh` renders every fenced `mermaid` block through `mermaid-cli`. A
diagram exists to be looked at, and one that fails to parse renders as nothing while
the page still looks complete — which is the failure a reader cannot report, because
they see no diagram to describe.
