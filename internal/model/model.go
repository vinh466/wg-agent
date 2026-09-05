// Package model holds the resource model of SPEC-01 and the two policy shapes
// of SPEC-02.
//
// ADR-0003 makes the `.proto` the source of truth for these types, so this
// package is what the generated ones replace. It exists because the store needs
// somewhere to write a spec to before the contract is generated, and because a
// package below the service layer cannot import one above it.
package model

// Axis is a forward-policy axis value. SPEC-02 section 2 fixes the set;
// AxisAllowList applies to inter_interface alone.
type Axis string

const (
	Allow     Axis = "ALLOW"
	Deny      Axis = "DENY"
	AllowList Axis = "ALLOW_LIST"
)

// Valid reports whether v is one of the three values SPEC-02 defines.
func (a Axis) Valid() bool {
	switch a {
	case Allow, Deny, AllowList:
		return true
	}
	return false
}

// ForwardPolicySpec is SPEC-02 section 2. The defaults of REQ-FWD-001 are not
// applied here: REQ-RCN-066 requires adoption to take these from the request,
// because a default would be a guess applied to a live node.
type ForwardPolicySpec struct {
	IntraInterface        Axis     `json:"intra_interface"`
	InterInterface        Axis     `json:"inter_interface"`
	AllowedPeerInterfaces []string `json:"allowed_peer_interfaces,omitempty"`
	External              Axis     `json:"external"`
}

// DefaultForwardPolicy is REQ-FWD-001. It is the value a create uses, not one
// adoption may fall back to.
func DefaultForwardPolicy() ForwardPolicySpec {
	return ForwardPolicySpec{IntraInterface: Allow, InterInterface: Deny, External: Deny}
}

// NatSpec is SPEC-02 section 6.
type NatSpec struct {
	Enabled                bool   `json:"enabled"`
	MasqueradeOutInterface string `json:"masquerade_out_interface,omitempty"`
	EnableUplinkForwarding bool   `json:"enable_uplink_forwarding"`
}

// InterfaceSpec is SPEC-01 section 3.2.
//
// It carries no name: REQ-RES-034 keeps identity outside both spec and status,
// so that an interface returned with its spec absent — a FOREIGN one under
// REQ-RCN-031 — is still addressable.
//
// PrivateKey is write-only: REQ-RES-013 keeps it out of every response, and the
// readiness report of REQ-DIA-047 reports only that it is present.
type InterfaceSpec struct {
	PrivateKey    string            `json:"private_key,omitempty"`
	ListenPort    int               `json:"listen_port"`
	Addresses     []string          `json:"addresses"`
	MTU           int               `json:"mtu"`
	Fwmark        uint32            `json:"fwmark"`
	ManageRoutes  bool              `json:"manage_routes"`
	ForwardPolicy ForwardPolicySpec `json:"forward_policy"`
	NAT           NatSpec           `json:"nat"`
	Enabled       bool              `json:"enabled"`
	Labels        map[string]string `json:"labels,omitempty"`
}

// Peer pairs a peer's identity with its spec. REQ-RES-020 makes the pair
// (interface_name, public_key) the identity, and REQ-RES-034 keeps it out of
// the spec.
type Peer struct {
	InterfaceName string   `json:"interface_name"`
	PublicKey     string   `json:"public_key"`
	Spec          PeerSpec `json:"spec"`
}

// PeerSpec is SPEC-01 section 4.2.
//
// Endpoint is kernel-owned under REQ-RCN-013 and REQ-RCN-051. Adoption never
// stores one (REQ-RCN-063), so the field is present for a create or update that
// sets it deliberately.
type PeerSpec struct {
	PresharedKey        string            `json:"preshared_key,omitempty"`
	AllowedIPs          []string          `json:"allowed_ips"`
	Endpoint            string            `json:"endpoint,omitempty"`
	PersistentKeepalive int               `json:"persistent_keepalive"`
	Labels              map[string]string `json:"labels,omitempty"`
}
