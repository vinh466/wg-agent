package service

import (
	"time"

	"wg-agent/internal/reconcile"
)

// DefaultPeerOnlineThreshold is the value REQ-RES-025 names. Three times
// WireGuard's rekey-after-time of 120 seconds, so one missed rekey does not
// report a working peer as offline.
const DefaultPeerOnlineThreshold = 180 * time.Second

// PeerRuntime is SPEC-01 section 4.3, read from the kernel under REQ-RES-024.
//
// None of it is stored: REQ-RCN-050 keeps status and traffic counters out of
// desired state, so every field here comes from the device dump.
type PeerRuntime struct {
	PublicKey string `json:"public_key"`
	// HandshakeAgeSeconds is nil when no handshake has occurred, which is the
	// null SPEC-01 section 4.3 describes rather than a zero age.
	HandshakeAgeSeconds *int64 `json:"handshake_age_seconds"`
	Online              bool   `json:"online"`
	RxBytes             int64  `json:"rx_bytes"`
	TxBytes             int64  `json:"tx_bytes"`
	ResolvedEndpoint    string `json:"resolved_endpoint,omitempty"`
	ProtocolVersion     int    `json:"protocol_version"`
	Revision            string `json:"revision"`
}

// Runtime reads peer runtime state.
type Runtime struct {
	Interfaces Interfaces
	// OnlineThreshold is peer_online_threshold from SPEC-09. Zero means the
	// default of REQ-RES-025.
	OnlineThreshold time.Duration
	// Now is injectable so a test can fix the handshake age it asserts.
	Now func() time.Time
}

func (r Runtime) now() time.Time {
	if r.Now != nil {
		return r.Now()
	}
	return time.Now()
}

func (r Runtime) threshold() time.Duration {
	if r.OnlineThreshold > 0 {
		return r.OnlineThreshold
	}
	return DefaultPeerOnlineThreshold
}

// ListPeerStatus returns the runtime state of every peer the kernel holds on
// the interface.
//
// The kernel is the source rather than the store, which is REQ-RES-024: a peer
// another writer added is reported, and a counter the agent never set is
// reported at the value the kernel has.
func (r Runtime) ListPeerStatus(iface string) ([]PeerRuntime, error) {
	ds, err := r.Interfaces.Device.Snapshot(iface)
	if err != nil {
		return nil, reasonErr(ReasonInterfaceNotFound,
			"no WireGuard interface named %q on this host", iface)
	}

	stored, err := r.Interfaces.Store.Snapshot().Peers(iface)
	if err != nil {
		return nil, err
	}
	revisions := make(map[string]string, len(stored))
	for _, p := range stored {
		revisions[p.PublicKey] = PeerRevision(p)
	}

	now := r.now()
	out := make([]PeerRuntime, 0, len(ds.Peers))
	for _, p := range ds.Peers {
		entry := PeerRuntime{
			PublicKey:        p.PublicKey,
			RxBytes:          p.ReceiveBytes,
			TxBytes:          p.TransmitBytes,
			ResolvedEndpoint: p.Endpoint,
			ProtocolVersion:  p.ProtocolVersion,
			Revision:         revisions[p.PublicKey],
		}
		if !p.LastHandshake.IsZero() {
			age := int64(now.Sub(p.LastHandshake) / time.Second)
			if age < 0 {
				// A clock that moved backwards should not report a negative
				// age; the handshake is as recent as it gets.
				age = 0
			}
			entry.HandshakeAgeSeconds = &age
			// REQ-RES-025 — online is the handshake being recent, not the
			// counters moving: a silent peer with a fresh handshake is up.
			entry.Online = now.Sub(p.LastHandshake) < r.threshold()
		}
		out = append(out, entry)
	}
	return out, nil
}

// GetInterfaceStatus returns the status half of an interface, for a caller that
// wants it without the spec.
func (r Runtime) GetInterfaceStatus(iface string) (*InterfaceStatus, error) {
	got, err := r.Interfaces.Get(iface)
	if err != nil {
		return nil, err
	}
	return &got.Status, nil
}

// Health is what REQ-SEC-080 serves without authentication. It reports only
// that the agent is running, so it discloses nothing a probe should not see.
type Health struct {
	Status string `json:"status"`
}

// Version is the build identity `wg-agent version` prints.
type Version struct {
	Version   string `json:"version"`
	Commit    string `json:"commit"`
	GoVersion string `json:"go_version"`
}

// ReconcileResult is what an explicit Reconcile RPC returns — one of the
// triggers REQ-RCN-020 lists.
type ReconcileResult struct {
	Statuses []reconcile.Status `json:"statuses"`
}

// Reconcile runs a pass and returns what every interface became.
func (r Runtime) Reconcile() (*ReconcileResult, error) {
	if r.Interfaces.Engine == nil {
		return &ReconcileResult{}, nil
	}
	for _, name := range r.Interfaces.Store.Snapshot().Names() {
		r.Interfaces.Engine.Interface(name)
	}
	return &ReconcileResult{Statuses: r.Interfaces.Engine.Statuses()}, nil
}
