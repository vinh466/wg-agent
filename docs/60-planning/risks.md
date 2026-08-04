---
updated: 2026-08-04
---

# Known risks

Each risk carries a **concrete mitigation** and a link to the REQ that implements it. A risk
without a mitigation is an open question and belongs in
[open-questions.md](open-questions.md).

## Technical

| Risk | Impact | Mitigation | REQ |
|---|---|---|---|
| Reconcile overwrites a kernel-learned endpoint, breaking roaming | Roaming clients disconnect repeatedly with a symptom that is hard to trace | `endpoint` classified kernel-owned; reconcile does not enforce it | `REQ-RCN-013`, `REQ-RCN-051` |
| An operator edits WireGuard manually outside the agent | Reconcile reverts their change | Drift metric plus clear logging. Unmanaged interfaces are left alone | `REQ-OBS-004`, `REQ-RCN-030` |
| `ALLOW` does not guarantee passage because a host firewall blocks | Operator believes policy is open while traffic fails. **Hardest symptom to diagnose** | The `foreign_forward_chains` check names the culprit | `REQ-DIA-010` |
| Client-side `AllowedIPs` not widened after opening `inter_interface` | Silent loss of connectivity | `ClientRouting.AUTO` derives it and explains the result | `REQ-KEY-031`, `REQ-KEY-033` |
| Interface key rotation disconnects every peer | Widespread outage | Explicit warning in the response. Zero-downtime rotation is deferred, so the platform schedules the outage | `REQ-KEY-004` |
| Metric cardinality at thousands of peers | Prometheus overload | Flag disabling per-peer metrics | `REQ-OBS-003` |
| Store corruption | Loss of desired state | Transactional writes, startup verification, backup | `REQ-RCN-003`, `REQ-LIF-020` |
| Agent rollback meets a newer store schema | Data misinterpretation | Refuse to start with a clear message | `REQ-RCN-005`, `REQ-LIF-011` |
| Unbounded audit log growth | Disk exhaustion | logrotate in the package plus an internal limit | `REQ-LIF-030`, `REQ-LIF-031` |
| Enabling uplink forwarding reaches outside the agent's ownership | Effects beyond WireGuard | Only with `external = ALLOW` and an explicit flag, with WARN and audit | `REQ-FWD-023`, `REQ-FWD-032` |
| Hostname endpoints do not follow DNS changes | Peers disconnect silently | Validation warning | `REQ-VAL-033`, OQ-12 |
| An API token leaks from the host and any local process can then reach `admin` | Full control of the node's WireGuard state | Token file at `0600`, rejected otherwise; HTTP listener disabled by default and refuses a non-loopback bind | `REQ-SEC-074`, `REQ-SEC-070`, `REQ-CFG-022` |
| A private key reaches the log | Key disclosure | Type-level redaction plus output-scanning tests | `REQ-SEC-050`, `REQ-SEC-051` |

## Design

| Risk | Impact | Mitigation |
|---|---|---|
| The `intra_interface = ALLOW` default is not deny-all | Peers on one interface see each other unintentionally | Stated in the guides. The other two axes remain `DENY`, so nothing leaks beyond the interface |
| Pressure to add shell hooks for unforeseen needs | Turns the API into a code execution endpoint | [ADR-0007](../10-decisions/ADR-0007-no-shell-hooks.md) states the boundary and the conditions for revisiting |
| Scope creep into platform functionality | Loss of reusability, the failure mode that killed comparable projects | The out-of-scope table in [product.md](../00-overview/product.md) is an enforced boundary |
| Documentation drifting from the implementation | The spec stops being a source of truth | Bidirectional REQ ID traceability with automated CI checks |
| Documentation accumulating conversational narrative | The spec becomes a transcript rather than a specification | Banned-vocabulary and length-cap checks in `check-docs.sh`; rules in [CONTRIBUTING.md](../CONTRIBUTING.md) |

## Operational

| Risk | Impact | Mitigation |
|---|---|---|
| `ProtectKernelTunables` carve-out unsupported on some systemd versions | Hardening must be reduced | `REQ-CFG-013` verifies the combination per distribution in CI; `REQ-CFG-012` is the documented fallback |
| The host firewall blocks the WireGuard UDP port | The leading cause of "created but nothing connects" | The `port_bound` and `foreign_forward_chains` diagnostic checks |
| Scale targets sized to the intended deployment prove too small for an adopter | Reconcile latency degrades beyond the benchmarked range | `REQ-LIF-040` states the figures explicitly, and `REQ-OBS-003` disables per-peer metrics past them |
| A platform on another host has no supported path to the agent | Adopters build ad-hoc tunnels of varying quality | [ADR-0009](../10-decisions/ADR-0009-local-only-listeners.md) states the boundary; an M2 guide documents one recommended tunnel |
