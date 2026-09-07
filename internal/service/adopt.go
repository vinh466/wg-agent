package service

import (
	"errors"
	"fmt"

	"wg-agent/internal/model"
	"wg-agent/internal/platform"
	"wg-agent/internal/store"
	"wg-agent/internal/validate"
)

// Reason codes of REQ-API-041 that adoption and release produce.
const (
	ReasonInterfaceNotFound     = "INTERFACE_NOT_FOUND"
	ReasonInterfaceNotForeign   = "INTERFACE_NOT_FOREIGN"
	ReasonInterfaceNotAdopted   = "INTERFACE_NOT_ADOPTED"
	ReasonAdoptionFieldRequired = "ADOPTION_FIELD_REQUIRED"
	ReasonAdoptionBlocked       = "ADOPTION_BLOCKED"
	ReasonForwardPolicyUplink   = "FORWARD_POLICY_NEEDS_UPLINK"
	ReasonInvalidArgument       = "INVALID_ARGUMENT"
)

// Error carries a reason code alongside the message, so a caller can branch on
// the code rather than parse the text — the property REQ-API-041 exists for.
type Error struct {
	Reason   string
	Message  string
	Findings []Finding
}

func (e *Error) Error() string { return e.Reason + ": " + e.Message }

func reasonErr(reason, format string, a ...any) *Error {
	return &Error{Reason: reason, Message: fmt.Sprintf(format, a...)}
}

// AdoptRequest is the request of REQ-RCN-066. The three policy fields are
// pointers because the requirement rejects a request that omits any of them:
// the kernel holds no forward policy and no routing intent, so a default would
// be a guess applied to a live node.
type AdoptRequest struct {
	Name          string
	ForwardPolicy *model.ForwardPolicySpec
	NAT           *model.NatSpec
	ManageRoutes  *bool
}

// AdoptResult is what was written, or what would be written in a preview.
type AdoptResult struct {
	Name     string
	Spec     model.InterfaceSpec
	Peers    []model.Peer
	Findings []Finding
	// Warnings are the REQ-VAL-002 findings on the spec that was stored. They
	// do not block, and REQ-RES-033 puts them in status.warnings.
	Warnings []validate.Finding
	// DryRun is true when nothing was written — REQ-CLI-006 and REQ-API-068.
	DryRun bool
}

// Adopt brings an existing link under management, per section 6.3 of SPEC-03.
type Adopt struct {
	Device platform.Device
	Link   platform.Link
	HostFS platform.HostFS
	// Now and NewID are injected so a test can assert the identity fields
	// REQ-RES-018 and REQ-RCN-002 require without freezing a clock globally.
	Now   func() string
	NewID func() string
}

// Do performs the adoption. With dryRun the store is opened read-only and
// nothing is written, which is the preview REQ-CLI-006 requires.
//
// The order is the one the requirements imply: reject a malformed request
// before touching the kernel, reject a wrong target before reading it, and
// consult the readiness report before writing anything.
func (a Adopt) Do(st *store.Store, req AdoptRequest, dryRun bool) (*AdoptResult, error) {
	if err := validateRequest(req); err != nil {
		return nil, err
	}

	// REQ-RCN-067 — a name with no link on the host.
	names, err := a.Device.Names()
	if err != nil {
		return nil, fmt.Errorf("enumerate wireguard devices: %w", err)
	}
	if !contains(names, req.Name) {
		return nil, reasonErr(ReasonInterfaceNotFound,
			"no WireGuard interface named %q on this host", req.Name)
	}

	snap := st.Snapshot()

	// REQ-RCN-074 — adoption applies to a FOREIGN interface alone.
	if own := ownershipOf(req.Name, snap); own != model.Foreign {
		return nil, reasonErr(ReasonInterfaceNotForeign,
			"%s is %s; adoption applies to a FOREIGN interface", req.Name, own)
	}

	report, err := Readiness{
		Device: a.Device, Link: a.Link, HostFS: a.HostFS, Desired: snap,
	}.Build()
	if err != nil {
		return nil, fmt.Errorf("build the readiness report: %w", err)
	}
	entry, ok := interfaceOf(report, req.Name)
	if !ok {
		return nil, fmt.Errorf("the readiness report does not name %q", req.Name)
	}

	spec, peers, err := a.read(req)
	if err != nil {
		return nil, err
	}

	// REQ-VAL-001 on the spec adoption would store. REQ-RCN-064 defers to it
	// explicitly — "a FAIL finding that REQ-VAL-001 does not itself reject" —
	// so a condition validation names is reported with its own reason code
	// rather than as ADOPTION_BLOCKED, which says nothing about the cause.
	host, err := validate.ReadHost(a.Device, a.Link)
	if err != nil {
		return nil, err
	}
	vr := validate.Validator{Host: host, Desired: snap}.
		Interface(req.Name, spec, peers, validate.Adopt)
	if err := vr.Err(); err != nil {
		return nil, err
	}

	// REQ-RCN-064 — a FAIL finding refuses the adoption and leaves the store
	// unchanged. The findings travel with the error under REQ-API-067.
	if entry.Blocking() {
		return nil, &Error{
			Reason:   ReasonAdoptionBlocked,
			Message:  fmt.Sprintf("%s is not ready to adopt", req.Name),
			Findings: entry.Findings,
		}
	}

	result := &AdoptResult{
		Name: req.Name, Spec: spec, Peers: peers,
		Findings: entry.Findings, Warnings: vr.Warnings, DryRun: dryRun,
	}
	if dryRun {
		return result, nil
	}

	baseline, _ := a.HostFS.ForwardingSysctl(req.Name)
	instanceID, now := a.NewID(), a.Now()

	// REQ-RCN-065 — the interface, its peers and its adoption record in one
	// transaction, so a crash cannot leave a managed interface without the
	// record REQ-RCN-072 needs to release it.
	if err := st.Update(func(t *store.Txn) error {
		if err := t.PutInterface(req.Name, spec, instanceID, now); err != nil {
			return err
		}
		if err := t.PutPeers(req.Name, peers); err != nil {
			return err
		}
		t.PutAdoption(req.Name, baseline, now)
		return nil
	}); err != nil {
		return nil, fmt.Errorf("write desired state: %w", err)
	}
	return result, nil
}

// validateRequest implements REQ-RCN-066 and the one policy rule that applies
// before anything is read: REQ-VAL-021 rejects external = ALLOW without uplink
// forwarding, and it would reject the spec adoption is about to store.
func validateRequest(req AdoptRequest) error {
	switch {
	case req.ForwardPolicy == nil:
		return reasonErr(ReasonAdoptionFieldRequired,
			"forward policy is required: the kernel holds none, so adoption cannot infer it")
	case req.NAT == nil:
		return reasonErr(ReasonAdoptionFieldRequired, "nat is required")
	case req.ManageRoutes == nil:
		return reasonErr(ReasonAdoptionFieldRequired, "manage_routes is required")
	}

	fp := *req.ForwardPolicy
	for label, axis := range map[string]model.Axis{
		"intra_interface": fp.IntraInterface,
		"inter_interface": fp.InterInterface,
		"external":        fp.External,
	} {
		if !axis.Valid() {
			return reasonErr(ReasonInvalidArgument, "%s: %q is not an axis value", label, axis)
		}
	}
	if fp.IntraInterface == model.AllowList || fp.External == model.AllowList {
		return reasonErr(ReasonInvalidArgument,
			"ALLOW_LIST applies to inter_interface alone, per REQ-FWD-002")
	}
	if fp.External == model.Allow && !req.NAT.EnableUplinkForwarding {
		return reasonErr(ReasonForwardPolicyUplink,
			"external = ALLOW requires nat.enable_uplink_forwarding, per REQ-VAL-021")
	}
	return nil
}

// read builds the spec and peers adoption stores.
//
// REQ-RCN-061 takes the interface fields and the existing private key from the
// kernel, REQ-RCN-062 takes every peer, and REQ-RCN-063 stores no endpoint.
func (a Adopt) read(req AdoptRequest) (model.InterfaceSpec, []model.Peer, error) {
	ls, err := a.Link.State(req.Name)
	if err != nil {
		return model.InterfaceSpec{}, nil, err
	}
	ds, err := a.Device.Snapshot(req.Name)
	if err != nil {
		return model.InterfaceSpec{}, nil, err
	}

	spec := model.InterfaceSpec{
		PrivateKey: ds.PrivateKey.Base64(),
		ListenPort: ds.ListenPort,
		MTU:        ls.MTU,
		Fwmark:     ds.Fwmark,
		// REQ-RES-019 — the administrative flag, not the operational state the
		// kernel reports, which is unknown for a WireGuard link.
		Enabled:       ls.AdminUp,
		ManageRoutes:  *req.ManageRoutes,
		ForwardPolicy: *req.ForwardPolicy,
		NAT:           *req.NAT,
	}
	for _, p := range ls.Addresses {
		spec.Addresses = append(spec.Addresses, p.String())
	}

	peers := make([]model.Peer, 0, len(ds.Peers))
	for _, p := range ds.Peers {
		peer := model.Peer{
			InterfaceName: req.Name,
			PublicKey:     p.PublicKey,
			Spec: model.PeerSpec{
				PresharedKey:        p.PresharedKey.Base64(),
				PersistentKeepalive: int(p.PersistentKeepalive.Seconds()),
				// Endpoint is deliberately absent — REQ-RCN-063.
			},
		}
		for _, n := range p.AllowedIPs {
			peer.Spec.AllowedIPs = append(peer.Spec.AllowedIPs, n.String())
		}
		peers = append(peers, peer)
	}
	return spec, peers, nil
}

// Release stops managing an interface while leaving its link running —
// REQ-RCN-069.
type Release struct {
	// RestoreForwarding writes the recorded baseline back. REQ-FWD-024 requires
	// the restore and REQ-FWD-022 excepts it; a nil function skips it, which is
	// what a preview and a node without the sysctl node both need.
	RestoreForwarding func(iface, value string) error
}

// ReleaseResult reports what the release did.
type ReleaseResult struct {
	Name                string
	ForwardingRestored  string
	PeersRemoved        int
	AdoptionRecordClear bool
}

// Do performs the release.
func (r Release) Do(st *store.Store, name string) (*ReleaseResult, error) {
	out := &ReleaseResult{Name: name}

	err := st.Update(func(t *store.Txn) error {
		// REQ-RCN-072 — an interface with no adoption record leaves desired
		// state through DeleteInterface, not through release.
		if !t.AdoptionRecord(name) {
			if t.Describes(name) {
				return reasonErr(ReasonInterfaceNotAdopted,
					"%s was created by the agent; delete it rather than releasing it", name)
			}
			return reasonErr(ReasonInterfaceNotAdopted,
				"%s is not under management", name)
		}

		peers, err := t.Peers(name)
		if err != nil {
			return err
		}
		out.PeersRemoved = len(peers)

		// REQ-RCN-073 — restore the sysctl before the transaction commits, so a
		// failure leaves the interface managed rather than half-released.
		if baseline, ok := t.ForwardingBaseline(name); ok && baseline != "" && r.RestoreForwarding != nil {
			if err := r.RestoreForwarding(name, baseline); err != nil {
				return fmt.Errorf("restore forwarding sysctl of %s: %w", name, err)
			}
			out.ForwardingRestored = baseline
		}

		// REQ-RCN-071 and REQ-RCN-038 — spec, peers and adoption record go
		// together.
		t.RemoveInterface(name)
		out.AdoptionRecordClear = true
		return nil
	})
	if err != nil {
		var re *Error
		if errors.As(err, &re) {
			return nil, re
		}
		return nil, err
	}
	return out, nil
}

func contains(v []string, s string) bool {
	for _, x := range v {
		if x == s {
			return true
		}
	}
	return false
}

func interfaceOf(r Report, name string) (InterfaceReadiness, bool) {
	for _, i := range r.Interfaces {
		if i.Name == name {
			return i, true
		}
	}
	return InterfaceReadiness{}, false
}
