---
id: ADR-0011
title: Operator-initiated adoption of a pre-existing interface
status: Accepted
owner: Vinh Nguyen
created: 2026-09-05
updated: 2026-09-05
supersedes: []
superseded_by: null
affects: [SPEC-01, SPEC-03, SPEC-04, SPEC-07, SPEC-11, SPEC-12]
---

# ADR-0011: Operator-initiated adoption of a pre-existing interface

## Context

The common deployment is a node where WireGuard already runs. An interface exists, peers are
configured, traffic flows, and `wg-quick` or a hand-written script brought it up. The agent
arrives second.

`REQ-RCN-030` protects that interface: a link the agent never created is classified `FOREIGN`
and left alone. `FOREIGN` has no exit. An interface in that state can never be placed under
management, so the agent's value on such a node is zero until the operator dismantles what
already works.

Two facts constrain the design.

The kernel already holds everything the resource model needs. A single device dump returns the
interface private key, listen port and fwmark, and for every peer its public key, preshared
key, allowed IPs and keepalive interval. Addresses and MTU come from the same netlink layer the
agent already uses. Nothing has to be reconstructed or guessed.

The reconcile algorithm in `REQ-RCN-022` reaches step 4 for any interface named in desired
state. Step 4 removes every peer present in the kernel but absent from the store. An operator
who creates an interface whose name matches a link that already exists therefore destroys its
peers, and `REQ-RES-012` replaces the interface key at the same time. Nothing in the current
requirement set prevents this, warns about it, or allows it to be previewed.

## Alternatives considered

### A — Leave `FOREIGN` terminal; the operator deletes and recreates
- For: no new requirements; the protection in `REQ-RCN-030` stays absolute
- Against: deleting the link discards the interface key, so every existing client stops
  connecting until its configuration is reissued. On a node chosen for the agent *because* it
  already works, the cost of onboarding is an outage

### B — Adopt implicitly when a create names an existing link
- For: no new operation; the reconcile algorithm already behaves this way
- Against: this is the current accidental behavior, and it is data loss. It cannot be
  previewed, cannot be refused, and gives the operator no moment at which to notice. An
  interface arriving under management is a decision, not a side effect of a name collision

### C — Import from the `wg-quick` configuration file
- For: recovers operator intent that the kernel does not hold, such as `DNS` and `Table`
- Against: the file may not describe what is running. `SaveConfig` rewrites it, `wg set`
  changes the kernel without touching it, and a peer added by hand exists only in the kernel.
  It also puts an INI parser and a malformed-input failure mode on the data path

### D — Explicit operator-initiated adoption, reading kernel state
- For: the source read is the state the kernel is running; no parser; the operator chooses
  the moment; a pre-flight report can refuse when another manager is contending
- Against: a new operation, a new report and a CLI surface to build

## Decision

Adopt **D**.

Adoption is an explicit operation on a named interface. It reads the interface and its peers
from the kernel, writes them to desired state, and preserves the existing interface private key
so that established clients keep connecting. Adopting an interface is what moves it from
`FOREIGN` to `MANAGED`, because `REQ-RES-017` already defines `MANAGED` as presence in desired
state — the ownership transition needs no separate mechanism.

Implicit adoption stays forbidden. Creating an interface whose name matches an existing link is
rejected rather than silently taken over.

The `wg-quick` configuration file is read, but only to detect what the kernel cannot express —
`PostUp`, `PostDown`, `DNS`, `SaveConfig` and `Table`. It is never a source of field values, so
a malformed file degrades the report rather than blocking adoption.

`REQ-RCN-030` is amended rather than weakened. Its section is titled *Interfaces outside
desired state* and its preamble scopes itself to a link that desired state does not describe,
but the requirement text carries no such qualifier and prose is not normative. Adding the
qualifier makes the requirement say what its section already claims. A link nobody has asked
for remains untouchable.

## Consequences

### Positive
- A node that already runs WireGuard can be brought under management without an outage
- `INTERFACE_EXISTS` gains a producing requirement, having sat in the closed reason-code set
  of `REQ-API-041` with nothing emitting it
- The readiness report is the preview the reconcile path has never had. `REQ-DIA-005` already
  forbids diagnostics from altering state, so the guarantee is inherited rather than restated

### Negative — the price paid
- The agent now reads a file written by a tool [ADR-0002](ADR-0002-netlink-over-wg-quick.md)
  rejected. The coupling is confined to detection, and every field still comes from the kernel
- An adopted interface has its private key in the agent store as well as wherever it already
  lived, so there are two copies to protect
- `PostUp` rules cannot be reproduced. [ADR-0007](ADR-0007-no-shell-hooks.md) forbids running
  them and `REQ-SEC-041` forbids the mechanism that would. An operator who disables `wg-quick`
  after adoption loses those rules at the next boot, and the report can only warn
- Adoption cannot disable a competing unit, for the same reason. It refuses and names the unit;
  the operator acts

### Follow-on work
- SPEC-03 gains an adoption section and the `REQ-RCN-030` qualifier
- SPEC-07 gains the create-time collision rule, and its conflict checks widen to foreign links
- SPEC-11 gains the adoption readiness report
- SPEC-12 gains `doctor` and `adopt`

## Conditions for revisiting

Any one of the following:

- Adoption of an interface carrying a configuration the report cannot classify becomes common
  enough that a `wg-quick` parser earns its failure modes
- A supported path appears for reproducing `PostUp` semantics as typed fields, removing the
  warning this decision settles for
- Network namespaces arrive, making an adopted interface's uniqueness assumptions too narrow
