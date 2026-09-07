// Package validate implements SPEC-07.
//
// Every rule in the specification lives here and nowhere else, which is what
// stops the same check drifting apart in two call sites. A rule is a pure
// function of the spec, the peers, the host facts and desired state, so the
// whole module runs against package fake without privilege.
//
// REQ-VAL-001 blocks the write on an error; REQ-VAL-002 stores a warning in
// `status.warnings` and lets the write through.
package validate

import (
	"fmt"
	"net/netip"
	"sort"

	"wg-agent/internal/model"
	"wg-agent/internal/platform"
)

// Op is the operation being validated. REQ-VAL-015 applies to a create alone,
// and REQ-VAL-013 and REQ-VAL-014 exclude the interface the spec names, so the
// rules cannot be evaluated without knowing which is which.
type Op int

const (
	// Create is a new interface. REQ-VAL-015 refuses a name whose link exists.
	Create Op = iota
	// Update changes a stored spec. The link exists by definition.
	Update
	// Adopt reads an existing link into desired state under REQ-RCN-060. The
	// link existing is the premise, not a fault.
	Adopt
)

// Finding is one rule's output. The reason code comes from the closed set of
// REQ-API-041, which REQ-VAL-002 and REQ-RES-033 both rely on: a caller
// branches on the code rather than parsing the message.
type Finding struct {
	Reason  string `json:"reason"`
	Field   string `json:"field,omitempty"`
	Message string `json:"message"`
}

// Result separates the two severities SPEC-07 defines.
type Result struct {
	Errors   []Finding `json:"errors,omitempty"`
	Warnings []Finding `json:"warnings,omitempty"`
}

// Blocked reports whether REQ-VAL-001 refuses the write.
func (r Result) Blocked() bool { return len(r.Errors) > 0 }

// Err returns the first error as an error value, or nil. The reason code of the
// first finding is the one the API returns, so the order rules run in is the
// order this file fixes rather than map iteration.
func (r Result) Err() error {
	if len(r.Errors) == 0 {
		return nil
	}
	return &Error{Finding: r.Errors[0], All: r.Errors}
}

// Error carries a blocking finding out of the package.
type Error struct {
	Finding Finding
	All     []Finding
}

func (e *Error) Error() string {
	if e.Finding.Field == "" {
		return e.Finding.Reason + ": " + e.Finding.Message
	}
	return fmt.Sprintf("%s: %s: %s", e.Finding.Reason, e.Finding.Field, e.Finding.Message)
}

// Reason returns the code the API reports — REQ-VAL-001.
func (e *Error) Reason() string { return e.Finding.Reason }

func (r *Result) errf(reason, field, format string, args ...any) {
	r.Errors = append(r.Errors, Finding{
		Reason: reason, Field: field, Message: fmt.Sprintf(format, args...),
	})
}

func (r *Result) warnf(reason, field, format string, args ...any) {
	r.Warnings = append(r.Warnings, Finding{
		Reason: reason, Field: field, Message: fmt.Sprintf(format, args...),
	})
}

// Host is what the rules need to know about the node. REQ-VAL-013 and
// REQ-VAL-014 reach foreign interfaces as well as managed ones, so this covers
// every WireGuard interface the kernel holds.
type Host struct {
	// Ports maps interface name to its listen port.
	Ports map[string]int
	// Addresses maps interface name to the addresses its link carries.
	Addresses map[string][]netip.Prefix
}

// Desired is the part of the store the rules read.
type Desired interface {
	// Names lists the interfaces desired state describes — the set REQ-VAL-022
	// tests membership of.
	Names() []string
	// Interface returns one stored spec, which REQ-VAL-023 needs to see the
	// other side of an ALLOW_LIST relationship.
	Interface(name string) (model.InterfaceSpec, bool, error)
	// DeletionRecord is the REQ-VAL-015 exemption.
	DeletionRecord(name string) bool
}

// Validator holds the facts every rule shares.
type Validator struct {
	Host    Host
	Desired Desired
}

// ReadHost builds the host facts from the platform ports.
//
// Only WireGuard devices are listed. REQ-VAL-013 is about a port held by a
// WireGuard interface and REQ-VAL-014 about a subnet on one, so a bridge or an
// ethernet link carrying the same subnet is outside both.
func ReadHost(dev platform.Device, lnk platform.Link) (Host, error) {
	names, err := dev.Names()
	if err != nil {
		return Host{}, fmt.Errorf("enumerate wireguard devices: %w", err)
	}
	out := Host{
		Ports:     make(map[string]int, len(names)),
		Addresses: make(map[string][]netip.Prefix, len(names)),
	}
	for _, n := range names {
		ds, err := dev.Snapshot(n)
		if err != nil {
			return Host{}, err
		}
		out.Ports[n] = ds.ListenPort

		ls, err := lnk.State(n)
		if err != nil {
			// A device whose link vanished between the two reads is not a
			// collision with anything.
			continue
		}
		out.Addresses[n] = ls.Addresses
	}
	return out, nil
}

// Interface validates an interface spec together with its peers.
//
// The order is the order of section 3 of SPEC-07, then section 4: an error
// found first is the reason code the API returns, and a deterministic order is
// what makes that reproducible.
func (v Validator) Interface(name string, spec model.InterfaceSpec, peers []model.Peer, op Op) Result {
	var r Result

	// Errors.
	v.checkName(&r, name)
	v.checkAddresses(&r, name, spec)
	v.checkListenPort(&r, name, spec)
	v.checkCreateCollision(&r, name, op)
	v.checkForwardPolicy(&r, spec)
	v.checkPeerInterfaces(&r, spec)
	for _, p := range peers {
		v.checkPeerErrors(&r, p)
	}
	v.checkDuplicateAllowedIPs(&r, peers)

	// Warnings.
	v.checkMTU(&r, spec)
	v.checkOneSidedAllowList(&r, name, spec)
	v.checkIgnoredPeerInterfaces(&r, spec)
	v.checkExternalWithoutNAT(&r, spec)
	v.checkOverlappingAllowedIPs(&r, peers)
	v.checkAllowedIPsOutOfSubnet(&r, spec, peers)
	for _, p := range peers {
		v.checkPeerWarnings(&r, p)
	}
	return r
}

// Peer validates one peer against the interface that holds it and the peers
// already stored there. The interface rules are not repeated: a stored spec has
// already passed them.
func (v Validator) Peer(
	spec model.InterfaceSpec, existing []model.Peer, p model.Peer,
) Result {
	var r Result

	v.checkPeerErrors(&r, p)

	// REQ-VAL-012 and REQ-VAL-030 are about the set, so the new peer is
	// evaluated alongside the ones already there. A peer replacing itself is
	// not a duplicate of itself.
	set := make([]model.Peer, 0, len(existing)+1)
	for _, e := range existing {
		if e.PublicKey != p.PublicKey {
			set = append(set, e)
		}
	}
	set = append(set, p)

	v.checkDuplicateAllowedIPs(&r, set)
	v.checkOverlappingAllowedIPs(&r, set)
	v.checkAllowedIPsOutOfSubnet(&r, spec, []model.Peer{p})
	v.checkPeerWarnings(&r, p)
	return r
}

// sortedNames returns the desired-state names, tolerating a nil store.
func (v Validator) sortedNames() []string {
	if v.Desired == nil {
		return nil
	}
	out := append([]string(nil), v.Desired.Names()...)
	sort.Strings(out)
	return out
}
