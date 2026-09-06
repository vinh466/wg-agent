// Package reconcile drives desired state onto the kernel.
//
// It is the algorithm of REQ-RCN-022, written against the ports of
// internal/platform, so the whole of it runs against package fake without
// privilege. Steps 9 and 10 — the forwarding sysctl and nftables — are deferred
// under B-04 in docs/60-planning/backlog.md and are absent here.
package reconcile

import "time"

// ConditionState is the closed set of REQ-RES-032.
type ConditionState string

const (
	Ready       ConditionState = "READY"
	Progressing ConditionState = "PROGRESSING"
	Degraded    ConditionState = "DEGRADED"
)

// Reason codes this package produces. Both are members of the closed set
// REQ-API-041 enumerates.
const (
	// ReasonReconcileFailed is what REQ-RCN-040 requires a failed pass to
	// carry. Step 2 of REQ-RCN-022 uses it too: a link of the wrong type
	// stops reconciliation, and the closed set of REQ-API-041 holds no code
	// narrower than this one, so the specifics go in the message.
	ReasonReconcileFailed = "RECONCILE_FAILED"
	// ReasonStoreCorrupt reports desired state that cannot be applied as
	// written, as distinct from an application that failed.
	ReasonStoreCorrupt = "STORE_CORRUPT"
)

// Oper state values of REQ-RES-019.
const (
	OperUp     = "UP"
	OperDown   = "DOWN"
	OperAbsent = "ABSENT"
)

// Condition is REQ-RES-032: exactly one state, with a reason and a message.
type Condition struct {
	State   ConditionState `json:"state"`
	Reason  string         `json:"reason,omitempty"`
	Message string         `json:"message,omitempty"`
}

// Warning is one entry of REQ-RES-033.
type Warning struct {
	Reason  string `json:"reason"`
	Message string `json:"message"`
}

// Status is what step 11 of REQ-RCN-022 writes.
//
// REQ-RCN-050 keeps status out of the store, so the engine holds it in memory
// and the API reads it from there. A restart therefore starts with no status
// and produces one on the startup pass of REQ-RCN-020.
type Status struct {
	Name      string    `json:"name"`
	Ownership string    `json:"ownership"`
	OperState string    `json:"oper_state"`
	PeerCount int       `json:"peer_count"`
	Condition Condition `json:"condition"`
	Warnings  []Warning `json:"warnings,omitempty"`
	UpdatedAt time.Time `json:"updated_at"`
}

func (s *Status) ready() {
	s.Condition = Condition{State: Ready}
}

func (s *Status) degraded(reason, msg string) {
	s.Condition = Condition{State: Degraded, Reason: reason, Message: msg}
}

func (s *Status) warn(reason, msg string) {
	s.Warnings = append(s.Warnings, Warning{Reason: reason, Message: msg})
}
