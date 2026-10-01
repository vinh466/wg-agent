---
id: ADR-0018
title: Distribution by a .deb the operator installs
status: Superseded
owner: Vinh Nguyen
created: 2026-09-30
updated: 2026-09-30
supersedes: [ADR-0010]
superseded_by: ADR-0019
affects: [SPEC-09, SPEC-12]
---

# ADR-0018: Distribution by a .deb the operator installs

## Context

[ADR-0010](ADR-0010-install-script-over-released-deb.md) distributes the agent through an install
script that downloads a released `.deb`, verifies it and installs it, so that someone who has
never built the project can install, update and remove it with one command.

By the operator's statement of 2026-09-30 the agent is an internal tool. Its only installer is
its operator, the repository is not published, and nothing is released for anyone to download.
The script, its checksum handling and the release pipeline that feeds it serve a reader who does
not exist.

## Alternatives considered

### A — The install script over a released `.deb`, as ADR-0010
- For: one command for a stranger
- Against: a release pipeline and a script to maintain, for no stranger

### B — A `.deb` built from the repository, installed with the package manager
- For: `apt install ./wg-agent_<version>.deb` installs and updates; `apt remove` and `apt purge`
  remove; the package's maintainer scripts carry every rule the script carried
- Against: the operator builds or copies the package to each node

### C — A binary and a unit file copied by hand
- Against: no dependency on `wireguard-tools`, no conffile, no maintainer script to protect the
  links on removal

## Decision

Adopt **B**.

- The `.deb` of [SPEC-09](../20-spec/SPEC-09-config-deployment.md) section 5 is the one artefact.
  It is installed, updated and removed through the package manager.
- Removal never deletes a WireGuard link, at any level — the rule ADR-0010 put in its script, which
  the package's maintainer scripts already carry (`REQ-CFG-027`, `REQ-CFG-028`).
- There is no install script, no release pipeline and no APT repository.

## Consequences

### Positive
- Nothing to maintain beyond the package itself
- Update and removal are the package manager's own, which an operator of Debian and Ubuntu
  already knows

### Negative — the price paid
- Getting the package onto a node — a copy, a shared directory, a private repository — is the
  operator's business

### Follow-on work
- None: the install script's requirements are struck from SPEC-09

## Conditions for revisiting

- The agent reaching people other than its operator.
- Enough nodes that copying a package to each becomes the chore the agent exists to remove.
