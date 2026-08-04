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
| Interface key rotation disconnects every peer | Widespread outage | Explicit warning in the response; documented parallel-run procedure | `REQ-KEY-004`, OQ-02 |
| Metric cardinality at thousands of peers | Prometheus overload | Flag disabling per-peer metrics | `REQ-OBS-003` |
| Store corruption | Loss of desired state | Transactional writes, startup verification, backup | `REQ-RCN-003`, `REQ-LIF-020` |
| Agent rollback meets a newer store schema | Data misinterpretation | Refuse to start with a clear message | `REQ-RCN-005`, `REQ-LIF-011` |
| Unbounded audit log growth | Disk exhaustion | logrotate in the package plus an internal limit | `REQ-LIF-030`, `REQ-LIF-031` |
| Enabling uplink forwarding reaches outside the agent's ownership | Effects beyond WireGuard | Only with `external = ALLOW` and an explicit flag, with WARN and audit | `REQ-FWD-023`, `REQ-FWD-032` |
| Hostname endpoints do not follow DNS changes | Peers disconnect silently | Validation warning | `REQ-VAL-033`, OQ-12 |
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
| `ProtectKernelTunables` carve-out unsupported on some systemd versions | Hardening must be reduced | OQ-05: real verification during M2 with a documented fallback |
| The host firewall blocks the WireGuard UDP port | The leading cause of "created but nothing connects" | The `port_bound` and `foreign_forward_chains` diagnostic checks |
| No concrete scale targets | No basis for deciding test coverage | `REQ-LIF-040` sets figures; OQ-04 verifies them by benchmark |
