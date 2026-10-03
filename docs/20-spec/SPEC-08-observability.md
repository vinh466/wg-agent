---
id: SPEC-08
title: Metrics, logs and audit
prefix: OBS
status: Implemented
version: 1.3
owner: Vinh Nguyen
created: 2026-08-03
updated: 2026-10-01
depends_on: [SPEC-01, SPEC-03]
adrs: []
milestone: P3 logs; Backlog B-01 metrics and audit
---

# SPEC-08: Metrics, logs and audit

## 1. Scope

Prometheus metrics, structured logging, audit logging.

**Not in this module:**
- On-demand diagnostics → [SPEC-11](SPEC-11-diagnostics.md)
- Audit log rotation → [SPEC-10](SPEC-10-lifecycle.md)

## 2. Metrics

> **REQ-OBS-001** — Metrics MUST be served on a listener separate from the API listener.

> **REQ-OBS-002** — The agent MUST expose the metrics listed below.

| Metric | Type | Labels |
|---|---|---|
| `wg_agent_build_info` | gauge | `version`, `commit`, `go_version` |
| `wg_agent_interfaces_total` | gauge | `ownership` |
| `wg_agent_interface_up` | gauge | `interface` |
| `wg_agent_interface_peers` | gauge | `interface` |
| `wg_agent_peers_online` | gauge | `interface` |
| `wg_agent_peer_last_handshake_seconds` | gauge | `interface`, `public_key` |
| `wg_agent_peer_rx_bytes_total` | counter | `interface`, `public_key` |
| `wg_agent_peer_tx_bytes_total` | counter | `interface`, `public_key` |
| `wg_agent_reconcile_duration_seconds` | histogram | `interface` |
| `wg_agent_reconcile_errors_total` | counter | `interface`, `reason` |
| `wg_agent_reconcile_drift_total` | counter | `interface`, `field` |
| `wg_agent_api_requests_total` | counter | `method`, `code` |

> **REQ-OBS-003** — The agent MUST support a flag that disables per-peer metrics, reducing
> output to per-interface aggregates.

The `public_key` label explodes cardinality at thousands of peers. Operational documentation
states the recommended threshold.

> **REQ-OBS-004** — The agent MUST increment `wg_agent_reconcile_drift_total`, labelled with
> the field name, whenever reconcile finds a difference in an agent-owned field.

This is the most operationally significant metric: it reveals that something outside the
agent is modifying WireGuard. Differences in kernel-owned fields are excluded by
`REQ-RCN-012`.

The `ownership` label on `wg_agent_interfaces_total` carries the orphan count without a
dedicated metric. Since `REQ-RCN-035` leaves orphan cleanup to an operator, a non-zero
`ownership="ORPHANED"` series is the alert that cleanup is outstanding.

## 3. Logs

> **REQ-OBS-010** — Logs MUST be emitted as structured JSON on stdout.

> **REQ-OBS-011** — Each log record MUST include `ts`, `level` and `msg`, plus `interface`,
> `peer`, `principal`, `request_id`, `error` and `reason` where applicable.

## 4. Audit log

> **REQ-OBS-020** — The agent MUST write an audit record for every state-changing operation.

> **REQ-OBS-021** — The audit log MUST be written to a file separate from the operational
> log.

> **REQ-OBS-022** — Each audit record MUST contain `ts`, `request_id`, `principal`, `action`,
> `resource`, `result`, `revision_before`, `revision_after` and `changed_fields`.

> **REQ-OBS-012** — The agent MUST assign a `request_id` to every request it accepts and use
> that same value in each log and audit record the request produces.

`request_id` is what joins the two records. `REQ-OBS-011` and `REQ-OBS-022` both name it, and
without a rule fixing where it comes from, a log line and the audit entry for the same call
could carry different values, which is the one thing the field exists to prevent.

> **REQ-OBS-023** — The audit log MUST record changed field **names** without the values of
> any private or preshared key.

Example record:

```json
{
  "ts": "2026-08-03T10:22:31Z",
  "request_id": "01J...",
  "principal": "token/vpn-controller",
  "action": "CreatePeer",
  "resource": "interface/wg0/peer/AbC...=",
  "result": "OK",
  "revision_before": "7",
  "revision_after": "8",
  "changed_fields": ["allowed_ips", "persistent_keepalive"]
}
```
