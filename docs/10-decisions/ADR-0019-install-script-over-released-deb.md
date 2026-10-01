---
id: ADR-0019
title: Distribution by an install script over a released .deb
status: Accepted
owner: Vinh Nguyen
created: 2026-10-01
updated: 2026-10-01
supersedes: [ADR-0018]
superseded_by: null
affects: [SPEC-09]
---

# ADR-0019: Distribution by an install script over a released .deb

## Context

[ADR-0018](ADR-0018-deb-installed-by-the-operator.md) distributes the agent as a `.deb` the
operator builds or copies to each node, with no install script, no release pipeline and no APT
repository. It rested on one premise: the repository is not published and nothing is released for
anyone to download, so the install script of [ADR-0010](ADR-0010-install-script-over-released-deb.md)
served a reader who did not exist.

By the operator's decision of 2026-10-01 the repository is public. The reader ADR-0018 called
absent now exists: someone who has never built the project installs, updates and removes it. That is
ADR-0018's own stated condition for revisiting — "the agent reaching people other than its operator."

The `.deb` itself — its unit, conffile and maintainer scripts — is unchanged and keeps every rule of
SPEC-09 sections 4 and 5. What this decision settles is how a node obtains that package and what
update and removal cost.

## Alternatives considered

### A — A signed APT repository
- For: `apt update && apt upgrade` updates the agent natively, the most familiar path
- Against: a GPG signing key, its distribution and repository metadata hosting before the first
  install — the most infrastructure to stand up and keep, for a tool on a handful of nodes

### B — A script that installs a released `.deb`, as ADR-0010
- For: dpkg tracks files, handles the conffile and orders the service stop and start; the script
  reduces to fetching the artifact, verifying its checksum and calling the package manager; one
  command each for install, update and uninstall
- Against: `apt upgrade` does not reach it, so updates run through the script; a release pipeline
  builds and publishes the package

### C — The operator's own `.deb`, as ADR-0018
- Against: the premise it rested on no longer holds; a stranger has nothing to install

## Decision

Adopt **B**, re-adopting the shape of ADR-0010 for the public repository, within the current scope.

- A release pipeline builds the `.deb` and a `SHA256SUMS` and publishes them as a GitHub Release on
  a version tag (`REQ-CFG-051`). The build targets `linux-x64` alone under
  [ADR-0016](ADR-0016-dotnet-nativeaot-build.md); the `amd64`/`arm64` matrix of ADR-0010 does not
  return.
- An install script installs, updates and uninstalls (`REQ-CFG-052`), verifying the downloaded
  `.deb` against its published SHA256 before installing it (`REQ-CFG-053`).
- Uninstall removes the package through the package manager (`REQ-CFG-054`), so `REQ-CFG-027` and
  `REQ-CFG-028` govern the links: removal deletes no WireGuard link, and prints what it leaves. The
  `--remove-links` option of ADR-0010 does not return — `REQ-CFG-027` forbids it outright.
- Integrity is the SHA256 of `REQ-CFG-053`, fetched with the artifact over HTTPS from the release.
  No GPG signing key and no APT repository; alternative A is the route when `apt`-native updates are
  wanted.

## Consequences

### Positive
- One command each for install, update and uninstall, for a node that never built the project
- Removal, conffile handling and service ordering come from dpkg, not from the script
- The whole distribution surface is one `.deb` and its checksum per release

### Negative — the price paid
- `apt upgrade` does not update the agent; an operator who expects it finds nothing
- A release pipeline builds and publishes the `.deb` on every tag
- A checksum, not a signature, so the trust is in the HTTPS fetch from the release

### Follow-on work
- SPEC-09 gains the release and install requirements (`REQ-CFG-051` to `REQ-CFG-054`); the struck
  `REQ-CFG-029` to `REQ-CFG-036` stay struck, their capability revived under the new numbers
- The "Not planned" entries for the install script and release pipeline move out; the APT repository
  stays there

## Conditions for revisiting

- `apt`-native updates wanted often enough to justify a signing key and a repository — alternative A.
- The install script growing past the point where auditing it before a `curl` pipe stays reasonable.
- A second package format or architecture, at which point a repository serves both better.
