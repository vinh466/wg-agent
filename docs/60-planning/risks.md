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
| The API token leaks — from the host, or from the private network it crosses in clear | Full control of the node's WireGuard state | Token file at `0600`, rejected otherwise; listener on loopback unless the operator places it on a private network; a replacement honoured from the next request | `REQ-SEC-074`, `REQ-SEC-084`, `REQ-SEC-085`, `REQ-CFG-022` |
| A generated private key crosses the network in a response | Key disclosure to whoever reads the private network | The private network is the channel's protection under ADR-0015; TLS is measured and waits in B-08 | `REQ-KEY-011`, `REQ-KEY-042` |
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
| `wireguard-tools` changes the form of `wg show dump` or the behaviour of `wg syncconf` | The agent misreads status or applies a change wrongly | The integration tier runs the real tools; [ADR-0013](../10-decisions/ADR-0013-drive-wg-and-wg-quick.md) names the signal for revisiting |
| A hand edit to a file the agent created is replaced at the agent's next write | The operator's change is lost without notice | [SPEC-13](../20-spec/SPEC-13-applying-changes.md) section 5 states it; its open question asks whether to refuse instead |
| Forwarding or NAT is missing on a host | Peers reach the node but not each other or the networks beyond | SPEC-13 section 5 leaves both to the host; the operator guide of P3 shows the one-time setup |
