---
id: ADR-0016
title: One NativeAOT binary on .NET 10 for linux-x64 against glibc
status: Accepted
owner: Vinh Nguyen
created: 2026-09-30
updated: 2026-09-30
supersedes: []
superseded_by: null
affects: [SPEC-09]
---

# ADR-0016: One NativeAOT binary on .NET 10 for linux-x64 against glibc

## Context

The implementation language is C#, chosen before this record by the operator when the Go
implementation was retired; the reasons for that choice are not restated here. What the choice
leaves open is the form the build takes on a node.

[ADR-0012](ADR-0012-managed-netlink-in-dotnet.md) settled that form together with its netlink
decision, and [ADR-0013](ADR-0013-drive-wg-and-wg-quick.md) superseded ADR-0012 for the
mechanism alone. The build form lost its record in the process, surviving only as
`REQ-CFG-044` and a paragraph of `CLAUDE.md`. This ADR restates it on its own, so it can be
superseded on its own.

Measured during the specification audit of September 2026, recorded in its section 2 and in
ADR-0015:

- A NativeAOT binary built on glibc 2.39 requires `GLIBC_2.34`; every supported distribution
  ships a newer glibc.
- It runs under `MemoryDenyWriteExecute=yes`, since no JIT writes code at run time.
- ASP.NET Core Minimal APIs with source-generated JSON, and Kestrel HTTPS, publish with no trim
  or AOT warning; JSON transcoding does not.
- A binary serving the API is about 9.4 MB, about 13 MB with TLS.

.NET 10 is the LTS line, supported to November 2028; .NET 9 reaches end of support on
10 November 2026.

## Alternatives considered

### A — Framework-dependent deployment
- For: the smallest artefact
- Against: a .NET runtime installed and patched on every node, a second package to keep in step

### B — Self-contained, with the JIT
- For: every library works as it does in development
- Against: a much larger artefact, and a JIT that writes code at run time, which rules out
  `MemoryDenyWriteExecute=yes`

### C — NativeAOT, self-contained, one binary
- For: nothing to install beside it; W^X holds; start-up is immediate
- Against: every dependency must be trimming- and AOT-safe

## Decision

Adopt **C**, on .NET 10, for `linux-x64` against glibc.

- `TreatWarningsAsErrors` reaches the AOT compiler, so a dependency that is not AOT-safe fails
  the build rather than the node — the property that ruled out JSON transcoding.
- `InvariantGlobalization` is set: nothing the agent does is culture-sensitive, and it removes
  the dependency on ICU.
- The `.deb` depends on `libc6 (>= 2.34)`, the floor measured.
- `arm64` and musl are not built. Each is a second binary, produced when a deployment needs it.

## Consequences

### Positive
- One file to install; nothing on the node to patch but the kernel and `wireguard-tools`
- The systemd unit keeps its strictest memory protection

### Negative — the price paid
- A library is chosen for being AOT-safe before it is chosen for anything else
- A second architecture or libc is a second build and a second test run

### Follow-on work
- None; `REQ-CFG-044` already states the build

## Conditions for revisiting

- A deployment on `arm64`, or on a musl distribution.
- A dependency the product needs that cannot be made AOT-safe.
- The next .NET LTS, whose support outlasts .NET 10's.
