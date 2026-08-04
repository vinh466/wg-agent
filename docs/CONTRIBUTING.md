# Contributing to the documentation

Rules and checklists for changing anything under `docs/`. Agent-facing instructions live
in [CLAUDE.md](../CLAUDE.md) at the repository root; the rules are identical.

---

## 1. Why these rules exist

This is a spec-first project. The specification only holds value as a source of truth if
it stays a specification. The observed failure mode is accretion: documentation written
iteratively — especially with an AI assistant — accumulates narrative, rationale,
hedging and temporal references until it reads like a transcript of how the design was
reached rather than a statement of what the system does.

Three layers guard against that.

| Layer | Mechanism | Enforced by |
|---|---|---|
| Structure | Rationale in ADRs, teaching in concepts, requirements in specs | Review |
| Style | Banned vocabulary, requirement format, length caps | `check-docs.sh` |
| Integrity | No broken links, no duplicate or orphan REQ IDs | `check-docs.sh` |

Rules that cannot be checked mechanically get violated. Everything in the Style and
Integrity layers is therefore a CI failure, not advice.

---

## 2. Where content belongs

Five content types, five directories. A document that answers more than one of these
questions must be split.

| Question it answers | Directory | Normative? |
|---|---|---|
| Why was this chosen? | `10-decisions/` | No |
| What MUST the system do? | `20-spec/` | **Yes** |
| How does it work? | `40-concepts/` | No |
| How do I do X? | `50-guides/` | No |
| When, and who? | `60-planning/` | No |

For the topic-to-module mapping inside `20-spec/`, see
[20-spec/README.md](20-spec/README.md).

### Deciding between spec and ADR

| Content | Goes to |
|---|---|
| "The agent MUST reject IPv6 addresses" | Spec |
| "IPv6 was rejected because a partial implementation is worse than none" | ADR |
| "Alternative A was considered and rejected" | ADR |
| The default value of a field | Spec |
| Why that default was chosen | ADR |

A spec states the rule. An ADR defends it. Mixing them means the rule cannot be read
quickly and the defence cannot be superseded independently.

---

## 3. Writing rules

### 3.1. Banned vocabulary

| Category | Banned | Scope |
|---|---|---|
| First/second person | I, we, you, our, us, let's, my | Everywhere except `50-guides/` |
| Temporal | currently, recently, previously, soon, at the moment, just added, will be | Everywhere |
| Conversational | as discussed, as mentioned, as noted, note that, worth noting, keep in mind, obviously, of course, basically, actually | Everywhere |
| Hedging | probably, maybe, perhaps, seems, appears to | Everywhere |
| Meta-narrative | this document will, in this section, below we, as we saw | Everywhere |

Two scopes exist because the categories serve different purposes. Impersonal prose matters
for normative and explanatory text, where the subject is the system rather than the reader.
Task-oriented guides address the reader directly, which is correct form, so the person rule
is lifted in `50-guides/` while every other rule still applies. `60-planning/` is exempt
entirely, since schedule and intent are its subject.

Temporal words are the most damaging category: they go stale silently and nothing detects
it. Write timeless statements and put dates in front-matter.

### 3.2. Requirement format

```markdown
> **REQ-FWD-012** — When `intra_interface = DENY`, the agent MUST drop packets whose
> input and output interface are both that interface.
```

- One requirement per line, one sentence
- Exactly one RFC 2119 keyword, uppercase, in English
- Prose without a keyword is explanatory and MUST NOT be implemented

### 3.3. Length caps

| Type | Limit |
|---|---|
| `20-spec/SPEC-*.md` | 400 lines |
| `10-decisions/ADR-*.md` | 180 lines |
| `40-concepts/*.md` | 320 lines |
| `50-guides/*.md` | 280 lines |

Exceeding a cap means the document has two subjects. Split it — do not raise the cap.

### 3.4. REQ ID rules

- Format `REQ-<AREA>-<NNN>`, areas listed in [README.md](README.md)
- Never reuse a number, even for a removed requirement
- Never renumber to insert; always take the next free number
- A removed requirement is struck through with the removal version and reason
- `XXX` is reserved for examples in guidance documents and is never a real area

---

## 4. Checklists

### Adding or amending a requirement

- [ ] Correct module per the boundary table
- [ ] Next free number in that area
- [ ] Exactly one RFC 2119 keyword
- [ ] Single sentence
- [ ] Rationale is one labelled paragraph, or moved to an ADR
- [ ] Cross-references use IDs, not copied text
- [ ] Front-matter `updated` and `version` bumped
- [ ] `check-docs.sh` exits 0

### Writing an ADR

- [ ] Number is the next free one
- [ ] Rejected alternatives recorded with their trade-offs
- [ ] Negative consequences stated explicitly
- [ ] "Conditions for revisiting" filled in — an ADR without this is dogma
- [ ] `affects` lists the specs it touches
- [ ] Listed in [10-decisions/README.md](10-decisions/README.md)
- [ ] Under 180 lines

### Superseding an ADR

- [ ] Old ADR body left untouched
- [ ] Old ADR `status` set to `Superseded`, `superseded_by` filled in
- [ ] New ADR `supersedes` filled in
- [ ] Both remain in the index

### Implementing a requirement

- [ ] Test name embeds the REQ ID
- [ ] Spec `status` moved to `Implemented`
- [ ] Behavior change reflected in concepts and guides

---

## 5. Running the checks

```bash
docs/check-docs.sh        # exit 0 = pass
```

| Check | Failure means |
|---|---|
| Broken links | A path is wrong or a file moved without updating referrers |
| Duplicate REQ ID | The same ID is defined twice |
| Orphan REQ ID | A referenced ID has no definition |
| Banned vocabulary | Conversational or temporal language leaked in |
| Requirement format | A REQ line is missing a keyword or spans sentences |
| Length cap | A document has grown past its limit and needs splitting |
| Front-matter | A required key is missing |

Excluded from all checks: `99-archive/` (superseded) and `_template.md` (intentional
placeholders).
