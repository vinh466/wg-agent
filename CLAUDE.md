# wg-agent — Agent Instructions

WireGuard control plane agent for Linux. **Spec-first project**: the specification is
authored and approved before implementation, and code traces back to it.

Read [docs/README.md](docs/README.md) before touching anything under `docs/`.

---

## Hard rules

1. **No code without a REQ ID.** If a task needs behavior not covered by a `REQ-*` in
   `docs/20-spec/`, stop and write the spec first. Do not implement and document later.

2. **`docs/20-spec/` is the only normative source.** When any other document, comment,
   or memory contradicts it, the spec wins and the other artifact is the bug.

3. **Never edit an `Accepted` ADR.** Supersede it with a new ADR instead.

4. **Never renumber or reuse a REQ ID.** Removed requirements are struck through, not
   deleted. IDs appear in tests, commits and issues.

5. **One fact, one home.** Link to it; never copy it. See the module boundary table in
   `docs/20-spec/README.md` to find where a topic belongs.

6. **Run `docs/check-docs.sh` before finishing any docs change.** A non-zero exit is a
   blocking failure, not a warning.

7. **No child processes in production paths** (`REQ-SEC-041`) — no `Process.Start`,
   no shelling out to `wg`, `ip`, `sysctl` or `nft`. Test helpers only.

---

## Writing rules for `docs/`

The failure mode this project guards against is documentation that accumulates
conversational narrative until the spec reads like a transcript instead of a
specification. `check-docs.sh` enforces the mechanical part of these rules.

### Banned vocabulary

| Category | Do not write | Scope |
|---|---|---|
| First/second person | I, we, you, our, us, let's, my | Everywhere except `50-guides/` |
| Temporal | currently, recently, previously, soon, at the moment, just added, will be | Everywhere |
| Conversational | as discussed, as mentioned, as noted, note that, worth noting, keep in mind, obviously, of course, basically, actually | Everywhere |
| Hedging | probably, maybe, perhaps, seems, appears to | Everywhere |
| Meta-narrative | this document will, in this section, below we, as we saw | Everywhere |

`50-guides/` may address the reader directly — that is correct form for task-oriented
writing. `60-planning/` is exempt entirely, since schedule and intent are its subject.

Temporal words go stale silently, which is the worst failure mode for a source of truth.
Write timeless statements and put dates in front-matter.

### Required in `docs/20-spec/`

- Every requirement is one line: `> **REQ-<AREA>-<NNN>** — <single sentence>`
- Each requirement contains exactly one RFC 2119 keyword: `MUST`, `MUST NOT`,
  `SHOULD`, `SHOULD NOT`, `MAY` — uppercase, in English
- Prose without a keyword is **explanatory only** and is never implemented
- Rationale is one short labelled paragraph after the requirement, or it belongs in an ADR
- Comparisons between rejected alternatives belong in ADRs, never in a spec

### Length caps

| Type | Limit | On exceeding |
|---|---|---|
| `20-spec/SPEC-*.md` | 400 lines | Split the module |
| `10-decisions/ADR-*.md` | 180 lines | Tighten; an ADR is one decision |
| `40-concepts/*.md` | 320 lines | Split the concept |
| `50-guides/*.md` | 280 lines | Split by task |

Caps exist to force splitting instead of accretion. Growing a file past its cap is
how a spec turns into a transcript.

---

## Workflow for a spec change

```
1. Locate the module      docs/20-spec/README.md → boundary table
2. Architectural choice?  write an ADR first (docs/10-decisions/)
3. Add or amend the REQ   next free number in that area, never reuse
4. Update .proto          if the contract changes; regenerate docs/30-api/
5. Implement + test       test name embeds the REQ ID it verifies
6. Update concepts/guides only if observable behavior changed
7. docs/check-docs.sh     must exit 0
```

Do not skip step 1. Placing a requirement in the wrong module is what creates the
duplication that rule 5 exists to prevent.

## Test naming

```csharp
[Fact]
public void ForwardPolicy_IntraDeny_REQ_FWD_012() { ... }
```

`docs/check-traceability.sh` matches these against the spec, so the ID must be
exact and must use underscores. A hyphenated ID in a method name is a failure,
not a style preference.

---

## Repository layout

Authoritative layout: [docs/00-overview/architecture.md](docs/00-overview/architecture.md).

| Path | Contents |
|---|---|
| `api/proto/wgagent/v1/` | Source of truth for the API contract — [ADR-0003](docs/10-decisions/ADR-0003-protobuf-source-of-truth.md) |
| `src/WgAgent.Platform/` | Ports and the types that cross them. References no adapter |
| `src/WgAgent.Platform.Linux/` | netlink adapters. The only code needing privilege |
| `src/` (rest) | Implementation |
| `tests/` | Test projects |
| `docs/` | All documentation — start at `docs/README.md` |
| `packaging/` | systemd units, Debian packaging |

The dependency points inwards. `WgAgent.Platform` must never reference an
adapter project: that rule is what keeps every layer above it testable without
privilege, and a project reference is how the compiler enforces it.

## Implementation language

C# on .NET 9, published with NativeAOT for `linux-x64` against glibc. The Go
implementation this replaces is preserved on the `impl/go` branch and is worth
consulting when implementing a requirement it already covered — it was verified
against a real kernel.

## Language

Documentation and code are written in **English**. Conversation with maintainers may
be in any language, but nothing conversational is committed.
