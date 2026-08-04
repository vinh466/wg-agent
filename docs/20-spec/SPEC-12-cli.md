---
id: SPEC-12
title: Command line surface
prefix: CLI
status: Accepted
version: 1.0
owner: Vinh Nguyen
created: 2026-08-05
updated: 2026-08-05
depends_on: [SPEC-05, SPEC-09, SPEC-10]
adrs: [ADR-0009, ADR-0010]
milestone: M2
---

# SPEC-12: Command line surface

## 1. Scope

The subcommands of the `wg-agent` binary: what each one does, which of them need a running
agent, and how tokens are issued.

**Not in this module:**
- Configuration keys and the install script → [SPEC-09](SPEC-09-config-deployment.md)
- Token semantics and roles → [SPEC-05](SPEC-05-security.md)
- Export and import behavior → [SPEC-10](SPEC-10-lifecycle.md)

## 2. Subcommands

> **REQ-CLI-001** — The binary MUST provide the subcommands listed below.

| Command | Needs a running agent | Purpose |
|---|---|---|
| `serve` | — | Run the agent; the systemd unit invokes this |
| `token add` | No | Issue a token with a role and a label |
| `token list` | No | List labels and roles, never token values |
| `token revoke` | No | Remove a token by label |
| `token regen` | No | Replace the value of an existing token, keeping label and role |
| `export` | No | Export desired state per `REQ-LIF-020` |
| `import` | No | Import desired state per `REQ-LIF-051` |
| `overview` | Yes | Print the node overview from `REQ-DIA-020` |
| `version` | No | Print version, commit and Go version |

> **REQ-CLI-002** — Token and state subcommands MUST operate on files directly rather than
> through the API.

Operating on files is what lets an operator issue the first token before the agent has ever
started, and recover a node whose agent refuses to start.

`export` and `import` arrive with [SPEC-10](SPEC-10-lifecycle.md), which owns their behavior
and sits a milestone later than the rest of this module. The commands the install script and
the MVP depend on are `serve`, `token`, `overview` and `version`.

> **REQ-CLI-003** — A subcommand needing a running agent MUST connect over the unix socket.

The unix socket grants `admin` under `REQ-SEC-077`, so the CLI never handles a token to read
the agent's own state.

## 3. Token issuance

> **REQ-CLI-010** — A generated token MUST be drawn from a cryptographically secure source
> with at least 256 bits of entropy.

> **REQ-CLI-011** — A generated token value MUST be printed exactly once, by the command that
> generates it.

> **REQ-CLI-012** — The agent MUST NOT provide any command that prints an existing token
> value.

> **REQ-CLI-013** — A command writing the token file MUST create it with mode `0600` and the
> ownership required by `REQ-SEC-082`.

> **REQ-CLI-014** — A command writing the token file MUST replace it atomically, leaving the
> previous contents intact on failure.

> **REQ-CLI-015** — `token regen` MUST keep the label and role of the entry it replaces.

> **REQ-CLI-016** — A command modifying the token file MUST signal the running agent to
> reload it, per `REQ-SEC-081`.

`REQ-CLI-011` and `REQ-CLI-012` together make the token file the only copy, which is what
makes mode `0600` under `REQ-CLI-013` meaningful. `REQ-CLI-016` is what turns revocation into
a real control rather than a note to restart the service later.

## 4. Output

> **REQ-CLI-020** — Every subcommand producing output MUST support `--output json` alongside
> its default human-readable form.

> **REQ-CLI-021** — A failing subcommand MUST exit with a non-zero status and write the
> reason to stderr.

> **REQ-CLI-022** — Human-readable output MUST NOT contain a private key, a preshared key or
> a token value, per `REQ-SEC-050`.

Machine-readable output exists because the install script parses the result of `token add`,
and because an operator scripting against a node should not parse a table.

## 5. Open questions

None.
