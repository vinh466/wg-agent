---
id: ADR-0010
title: Distribution by install script over a released .deb
status: Superseded
owner: Vinh Nguyen
created: 2026-08-05
updated: 2026-08-05
supersedes: []
superseded_by: ADR-0018
affects: [SPEC-09, SPEC-12]
---

# ADR-0010: Distribution by install script over a released .deb

## Context

The repository is public and the target hosts run Debian or Ubuntu. Installation, update and
removal each need to be a single short command.

`REQ-CFG-021` already specifies the contents of a `.deb`: the systemd unit, a `sysusers.d`
entry, a `tmpfiles.d` entry, logrotate configuration and a sample configuration file. What no
decision covers is how an operator obtains that package, and what removal does to WireGuard
links the agent created.

The second half matters more than it appears. `REQ-LIF-001` states that the data plane runs
independently of the agent: the kernel keeps forwarding traffic while the agent is stopped.
Removing the control plane therefore has a choice to make about the tunnels that survive it.

## Alternatives considered

### A — A signed APT repository
- For: `apt update && apt upgrade` works natively; the most familiar path for an operator
- Against: requires a GPG signing key, key distribution, and repository metadata hosting
  before the first user installs anything; the largest infrastructure cost of the three

### B — A script that installs a tarball
- For: no packaging toolchain at all
- Against: the script has to reimplement what dpkg already does — tracking installed files so
  removal leaves none behind, and preserving operator edits to configuration across updates.
  Removal is the hardest part to get right and the easiest to get silently wrong

### C — A script that installs a released `.deb`
- For: dpkg tracks files, handles conffiles and orders service stop and start; the script
  reduces to fetching an artifact, verifying it and calling the package manager
- Against: `apt upgrade` does not reach it, so updates run through the script; a release
  pipeline has to build and publish the package

## Decision

Adopt **C**, with removal that never deletes a WireGuard link by default.

The script fetches the `.deb` for the host architecture from the repository's releases,
verifies its checksum, and installs it. `update` repeats that sequence. `uninstall` calls the
package manager and leaves every link in place, listing what remains; `uninstall
--remove-links` deletes them in one step for an operator who wants the host clean.

Leaving links alone by default follows the rule the agent already applies to itself:
`REQ-RCN-030` forbids touching a link the agent did not create and `REQ-RCN-035` forbids
deleting an orphan automatically. An uninstall that silently drops live tunnels would
contradict both, and it would turn a control-plane operation into a network outage.

## Consequences

### Positive
- Removal, conffile handling and service ordering come from dpkg rather than from a script
- A single artifact per architecture, checksummed, is the whole distribution surface
- Removing the agent never interrupts traffic unless an operator asks for that

### Negative — the price paid
- `apt upgrade` does not update the agent; an operator who expects it finds nothing
- A release pipeline has to build and publish `.deb` files for `amd64` and `arm64`
- After `uninstall` without `--remove-links`, links persist with nothing managing them; the
  printed list is the only record an operator gets

### Follow-on work
- SPEC-09 gains the maintainer-script, conffile and install-script requirements
- SPEC-12 defines the CLI the script calls for token generation

## Conditions for revisiting

Any one of the following:

- Adopters ask for `apt`-native updates often enough to justify a signing key
- A second package format is required, at which point a repository serves both better
- The install script grows past the point where auditing it before `curl` piping stays
  reasonable
