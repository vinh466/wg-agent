package model

// Ownership is the vocabulary of REQ-RES-017. Every value is decided from
// desired state and the deletion record, both of which the store holds —
// creation history is not among them, because the agent has no durable memory
// of it.
type Ownership string

const (
	Managed  Ownership = "MANAGED"
	Foreign  Ownership = "FOREIGN"
	Orphaned Ownership = "ORPHANED"
)

// OwnershipOf decides REQ-RES-017's three values from the two facts the store
// holds. Order matters: desired state wins, then the deletion record.
//
// It takes the two facts rather than the store that answers them, so the rule
// is one decision testable on its own and both the diagnostics report and the
// reconcile classification of REQ-RCN-036 reach the same verdict.
func OwnershipOf(describes, deletionRecord bool) Ownership {
	switch {
	case describes:
		return Managed
	case deletionRecord:
		return Orphaned
	default:
		return Foreign
	}
}
