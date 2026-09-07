package service

import (
	"fmt"

	"wg-agent/internal/model"
	"wg-agent/internal/store"
	"wg-agent/internal/validate"
)

// Peers implements the peer half of the contract. REQ-RES-002 makes peers a
// separate collection, and REQ-RES-020 makes the pair
// (interface_name, public_key) the identity.
type Peers struct {
	Interfaces Interfaces
}

// Peer is one peer as a response carries it. The preshared key is absent:
// REQ-RES-022 keeps it out of every response the way REQ-RES-013 keeps the
// interface private key out.
type Peer struct {
	InterfaceName string       `json:"interface_name"`
	PublicKey     string       `json:"public_key"`
	Spec          PeerSpecView `json:"spec"`
	Status        PeerStatus   `json:"status"`
}

// PeerSpecView is PeerSpec without the preshared key. Its presence is
// reported, which is the whole of what REQ-DIA-047 permits about a key.
type PeerSpecView struct {
	PresharedKeyPresent bool              `json:"preshared_key_present"`
	AllowedIPs          []string          `json:"allowed_ips"`
	Endpoint            string            `json:"endpoint,omitempty"`
	PersistentKeepalive int               `json:"persistent_keepalive"`
	Labels              map[string]string `json:"labels,omitempty"`
}

// PeerStatus is SPEC-01 section 4.3, less the traffic counters, which
// internal/service/runtime.go reads from the kernel.
type PeerStatus struct {
	Revision string             `json:"revision"`
	Warnings []validate.Finding `json:"warnings,omitempty"`
}

func peerView(p model.Peer) Peer {
	return Peer{
		InterfaceName: p.InterfaceName,
		PublicKey:     p.PublicKey,
		Spec: PeerSpecView{
			PresharedKeyPresent: p.Spec.PresharedKey != "",
			AllowedIPs:          p.Spec.AllowedIPs,
			Endpoint:            p.Spec.Endpoint,
			PersistentKeepalive: p.Spec.PersistentKeepalive,
			Labels:              p.Spec.Labels,
		},
		Status: PeerStatus{Revision: PeerRevision(p)},
	}
}

// Create adds a peer.
//
// REQ-API-071 refuses a public key the interface already describes: a repeated
// create is a mistake far more often than an intent to replace, and Update is
// the operation that replaces.
func (s Peers) Create(iface string, p model.Peer) (*Peer, error) {
	spec, peers, err := s.load(iface)
	if err != nil {
		return nil, err
	}
	for _, existing := range peers {
		if existing.PublicKey == p.PublicKey {
			return nil, reasonErr(ReasonPeerExists,
				"%s already describes a peer with that public key", iface)
		}
	}

	p.InterfaceName = iface
	if err := s.validatePeer(spec, peers, p); err != nil {
		return nil, err
	}
	if err := s.Interfaces.Store.Update(func(t *store.Txn) error {
		return t.PutPeer(iface, p)
	}); err != nil {
		return nil, fmt.Errorf("write desired state: %w", err)
	}
	return s.applyAndRead(iface, p.PublicKey)
}

// Update replaces a peer's spec. REQ-API-070 refuses one desired state does not
// describe.
func (s Peers) Update(iface string, p model.Peer) (*Peer, error) {
	spec, peers, err := s.load(iface)
	if err != nil {
		return nil, err
	}
	found := false
	for _, existing := range peers {
		if existing.PublicKey == p.PublicKey {
			found = true
			// A preshared key the caller omitted keeps the stored one: it is
			// write-only under REQ-RES-022, so the caller could not have read
			// it to send it back.
			if p.Spec.PresharedKey == "" {
				p.Spec.PresharedKey = existing.Spec.PresharedKey
			}
			break
		}
	}
	if !found {
		return nil, reasonErr(ReasonPeerNotFound,
			"%s does not describe a peer with that public key", iface)
	}

	p.InterfaceName = iface
	if err := s.validatePeer(spec, peers, p); err != nil {
		return nil, err
	}
	if err := s.Interfaces.Store.Update(func(t *store.Txn) error {
		return t.PutPeer(iface, p)
	}); err != nil {
		return nil, fmt.Errorf("write desired state: %w", err)
	}
	return s.applyAndRead(iface, p.PublicKey)
}

// Get returns one peer.
func (s Peers) Get(iface, publicKey string) (*Peer, error) {
	_, peers, err := s.load(iface)
	if err != nil {
		return nil, err
	}
	for _, p := range peers {
		if p.PublicKey == publicKey {
			v := peerView(p)
			return &v, nil
		}
	}
	return nil, reasonErr(ReasonPeerNotFound,
		"%s does not describe a peer with that public key", iface)
}

// List returns every peer desired state describes on the interface.
func (s Peers) List(iface string) ([]Peer, error) {
	_, peers, err := s.load(iface)
	if err != nil {
		return nil, err
	}
	out := make([]Peer, 0, len(peers))
	for _, p := range peers {
		out = append(out, peerView(p))
	}
	return out, nil
}

// Delete removes a peer. REQ-API-070 refuses one desired state does not
// describe, so a repeated delete reports that rather than succeeding twice.
func (s Peers) Delete(iface, publicKey string) error {
	if _, _, err := s.load(iface); err != nil {
		return err
	}

	var removed bool
	if err := s.Interfaces.Store.Update(func(t *store.Txn) error {
		var err error
		removed, err = t.RemovePeer(iface, publicKey)
		return err
	}); err != nil {
		return fmt.Errorf("write desired state: %w", err)
	}
	if !removed {
		return reasonErr(ReasonPeerNotFound,
			"%s does not describe a peer with that public key", iface)
	}

	// Step 4 of REQ-RCN-022 removes it from the kernel.
	if s.Interfaces.Engine != nil {
		s.Interfaces.Engine.Interface(iface)
	}
	return nil
}

// ── internals ───────────────────────────────────────────────────────────────

// load returns the interface spec and its peers, refusing an interface desired
// state does not describe. A peer on a foreign interface is not something the
// agent can store: REQ-RCN-030 leaves such a link alone.
func (s Peers) load(iface string) (model.InterfaceSpec, []model.Peer, error) {
	snap := s.Interfaces.Store.Snapshot()
	spec, ok, err := snap.Interface(iface)
	if err != nil {
		return spec, nil, err
	}
	if !ok {
		return spec, nil, reasonErr(ReasonInterfaceNotManaged,
			"desired state does not describe %q", iface)
	}
	peers, err := snap.Peers(iface)
	return spec, peers, err
}

func (s Peers) validatePeer(
	spec model.InterfaceSpec, existing []model.Peer, p model.Peer,
) error {
	host, err := validate.ReadHost(s.Interfaces.Device, s.Interfaces.Link)
	if err != nil {
		return err
	}
	v := validate.Validator{Host: host, Desired: s.Interfaces.Store.Snapshot()}
	return v.Peer(spec, existing, p).Err()
}

// applyAndRead reconciles the interface and returns the peer, so the response
// reflects the write — REQ-RCN-020 lists an API write among the triggers.
func (s Peers) applyAndRead(iface, publicKey string) (*Peer, error) {
	if s.Interfaces.Engine != nil {
		s.Interfaces.Engine.Interface(iface)
	}
	return s.Get(iface, publicKey)
}
