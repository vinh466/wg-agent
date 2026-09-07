package service

import (
	"fmt"
	"net/netip"
	"sort"
	"strings"

	"wg-agent/internal/model"
)

// RoutingMode is the closed set of REQ-KEY-030.
type RoutingMode string

const (
	// RoutingAuto derives AllowedIPs from forward_policy — REQ-KEY-031.
	RoutingAuto RoutingMode = "AUTO"
	// RoutingServerOnly reaches the interface address alone.
	RoutingServerOnly RoutingMode = "SERVER_ONLY"
	// RoutingLocalSubnet reaches the interface's own subnet.
	RoutingLocalSubnet RoutingMode = "LOCAL_SUBNET"
	// RoutingFullTunnel sends everything through the tunnel.
	RoutingFullTunnel RoutingMode = "FULL_TUNNEL"
)

// ReasonEndpointRequired is REQ-KEY-038's code.
const ReasonEndpointRequired = "ENDPOINT_REQUIRED"

// ClientConfigRequest asks for a `.conf` a WireGuard client can read.
type ClientConfigRequest struct {
	Interface string
	PublicKey string
	Mode      RoutingMode
	// Endpoint overrides node.endpoint — REQ-KEY-037 takes the request first.
	Endpoint string
	// DNS values go into the generated file and nowhere else — REQ-KEY-034.
	DNS []string
	// PrivateKey is written into the file when the caller has one to put
	// there. The agent never holds a peer private key: REQ-KEY-010 makes BYOK
	// the default, so this is the caller's own value passed through.
	PrivateKey string
}

// ClientConfig is what REQ-KEY-033 returns: the file, the derived list, and
// how the list was reached.
type ClientConfig struct {
	FileName           string   `json:"file_name"`
	Config             string   `json:"config"`
	DerivedAllowedIPs  []string `json:"derived_allowed_ips"`
	RoutingExplanation []string `json:"routing_explanation"`
}

// ClientConfigs generates a client configuration.
type ClientConfigs struct {
	Interfaces Interfaces
	// NodeEndpoint is node.endpoint from SPEC-09, the fallback REQ-KEY-037
	// names when the request omits one.
	NodeEndpoint string
}

// Generate produces the file.
//
// The generated AllowedIPs is configuration advice rather than enforcement: a
// client can widen it, and only the forward policy decides what actually
// passes. REQ-KEY-033 returns the explanation for that reason — the operator
// has to be able to see why the list is what it is.
func (c ClientConfigs) Generate(req ClientConfigRequest) (*ClientConfig, error) {
	snap := c.Interfaces.Store.Snapshot()
	spec, ok, err := snap.Interface(req.Interface)
	if err != nil {
		return nil, err
	}
	if !ok {
		return nil, reasonErr(ReasonInterfaceNotManaged,
			"desired state does not describe %q", req.Interface)
	}

	// REQ-KEY-037 and REQ-KEY-038 — the request first, then the configuration,
	// then a refusal. No address the agent could pick would be right: which of
	// several is routable depends on where the client sits.
	endpoint := req.Endpoint
	if endpoint == "" {
		endpoint = c.NodeEndpoint
	}
	if endpoint == "" {
		return nil, reasonErr(ReasonEndpointRequired,
			"neither the request nor node.endpoint supplies an endpoint for %q",
			req.Interface)
	}

	peers, err := snap.Peers(req.Interface)
	if err != nil {
		return nil, err
	}
	var peerSpec *model.PeerSpec
	for i := range peers {
		if peers[i].PublicKey == req.PublicKey {
			peerSpec = &peers[i].Spec
			break
		}
	}
	if peerSpec == nil {
		return nil, reasonErr(ReasonPeerNotFound,
			"%s does not describe a peer with that public key", req.Interface)
	}

	allowed, why, err := c.derive(req, spec, snap)
	if err != nil {
		return nil, err
	}

	serverPub := ""
	if ds, err := c.Interfaces.Device.Snapshot(req.Interface); err == nil {
		serverPub = ds.PublicKey
	}

	out := &ClientConfig{
		FileName:           req.Interface + "-client.conf",
		DerivedAllowedIPs:  allowed,
		RoutingExplanation: why,
	}
	out.Config = render(req, spec, serverPub, allowed, endpoint, peerSpec)
	return out, nil
}

// derive implements REQ-KEY-030 through REQ-KEY-032.
func (c ClientConfigs) derive(
	req ClientConfigRequest, spec model.InterfaceSpec, snap desiredReader,
) ([]string, []string, error) {
	mode := req.Mode
	if mode == "" {
		mode = RoutingAuto
	}

	addr, subnet, err := serverAddress(spec)
	if err != nil {
		return nil, nil, err
	}

	var out []netip.Prefix
	var why []string

	switch mode {
	case RoutingServerOnly:
		out = []netip.Prefix{netip.PrefixFrom(addr, addr.BitLen())}
		why = []string{"SERVER_ONLY: the interface address alone"}
	case RoutingLocalSubnet:
		out = []netip.Prefix{subnet}
		why = []string{"LOCAL_SUBNET: the interface's own subnet"}
	case RoutingFullTunnel:
		out = []netip.Prefix{netip.MustParsePrefix("0.0.0.0/0")}
		why = []string{"FULL_TUNNEL: everything through the tunnel"}
	case RoutingAuto:
		// REQ-KEY-031, in the order the requirement lists.
		out = append(out, netip.PrefixFrom(addr, addr.BitLen()))
		why = append(why, fmt.Sprintf("always: the interface address %s/%d",
			addr, addr.BitLen()))

		if spec.ForwardPolicy.IntraInterface == model.Allow {
			out = append(out, subnet)
			why = append(why, fmt.Sprintf(
				"intra_interface = ALLOW: the interface subnet %s", subnet))
		}
		switch spec.ForwardPolicy.InterInterface {
		case model.Allow:
			for _, name := range otherSubnets(snap, req.Interface, nil) {
				p, err := netip.ParsePrefix(name.cidr)
				if err != nil {
					continue
				}
				out = append(out, p.Masked())
				why = append(why, fmt.Sprintf(
					"inter_interface = ALLOW: the subnet of %s, %s", name.iface, p.Masked()))
			}
		case model.AllowList:
			allow := spec.ForwardPolicy.AllowedPeerInterfaces
			for _, name := range otherSubnets(snap, req.Interface, allow) {
				p, err := netip.ParsePrefix(name.cidr)
				if err != nil {
					continue
				}
				out = append(out, p.Masked())
				why = append(why, fmt.Sprintf(
					"inter_interface = ALLOW_LIST: the subnet of %s, %s", name.iface, p.Masked()))
			}
		}
		if spec.ForwardPolicy.External == model.Allow {
			out = append(out, netip.MustParsePrefix("0.0.0.0/0"))
			why = append(why, "external = ALLOW: 0.0.0.0/0")
		}
	default:
		return nil, nil, reasonErr(ReasonInvalidArgument,
			"%q is not a routing mode", mode)
	}

	// REQ-KEY-032 — a default route subsumes everything, so the narrower
	// entries are noise in the file and misleading in the explanation.
	if containsDefaultRoute(out) {
		out = []netip.Prefix{netip.MustParsePrefix("0.0.0.0/0")}
		why = append(why, "0.0.0.0/0 subsumes the narrower entries, which are omitted")
	}

	strs := make([]string, 0, len(out))
	seen := map[netip.Prefix]struct{}{}
	for _, p := range out {
		if _, dup := seen[p]; dup {
			continue
		}
		seen[p] = struct{}{}
		strs = append(strs, p.String())
	}
	return strs, why, nil
}

type ifaceSubnet struct {
	iface string
	cidr  string
}

// desiredReader is the part of the snapshot this file needs.
type desiredReader interface {
	Names() []string
	Interface(name string) (model.InterfaceSpec, bool, error)
}

// otherSubnets lists the first address of every other managed interface. When
// only is non-nil the result is confined to those names, which is the
// ALLOW_LIST arm of REQ-KEY-031.
func otherSubnets(snap desiredReader, self string, only []string) []ifaceSubnet {
	permitted := map[string]bool{}
	for _, n := range only {
		permitted[n] = true
	}

	names := append([]string(nil), snap.Names()...)
	sort.Strings(names)

	var out []ifaceSubnet
	for _, name := range names {
		if name == self {
			continue
		}
		if only != nil && !permitted[name] {
			continue
		}
		spec, ok, err := snap.Interface(name)
		if err != nil || !ok || len(spec.Addresses) == 0 {
			continue
		}
		out = append(out, ifaceSubnet{iface: name, cidr: spec.Addresses[0]})
	}
	return out
}

// serverAddress returns the interface's first address and its subnet.
// REQ-VAL-016 makes an interface with no address invalid, so reaching the error
// here means a store that lost one.
func serverAddress(spec model.InterfaceSpec) (netip.Addr, netip.Prefix, error) {
	if len(spec.Addresses) == 0 {
		return netip.Addr{}, netip.Prefix{}, reasonErr(ReasonInvalidArgument,
			"the interface carries no address to advertise")
	}
	p, err := netip.ParsePrefix(spec.Addresses[0])
	if err != nil {
		return netip.Addr{}, netip.Prefix{}, reasonErr(ReasonInvalidArgument,
			"the interface address %q is not a CIDR", spec.Addresses[0])
	}
	return p.Addr(), p.Masked(), nil
}

func containsDefaultRoute(ps []netip.Prefix) bool {
	for _, p := range ps {
		if p.Bits() == 0 && p.Addr().Is4() {
			return true
		}
	}
	return false
}

// render writes the wg-quick `.conf` of REQ-KEY-035.
//
// REQ-KEY-036 puts the Endpoint in, which is what makes the format claim true
// rather than nearly true: a `.conf` without one follows the format and reaches
// nothing.
func render(
	req ClientConfigRequest,
	spec model.InterfaceSpec,
	serverPublicKey string,
	allowed []string,
	endpoint string,
	peer *model.PeerSpec,
) string {
	var b strings.Builder

	b.WriteString("[Interface]\n")
	if req.PrivateKey != "" {
		b.WriteString("PrivateKey = " + req.PrivateKey + "\n")
	} else {
		b.WriteString("# PrivateKey = <the client's own key>\n")
	}
	// The client's address inside the tunnel is its first allowed_ips entry:
	// that is the address this peer answers on.
	if len(peer.AllowedIPs) > 0 {
		b.WriteString("Address = " + peer.AllowedIPs[0] + "\n")
	}
	if spec.MTU != 0 {
		fmt.Fprintf(&b, "MTU = %d\n", spec.MTU)
	}
	// REQ-KEY-034 — the values go here and nowhere else. The agent never
	// alters host DNS configuration.
	if len(req.DNS) > 0 {
		b.WriteString("DNS = " + strings.Join(req.DNS, ", ") + "\n")
	}

	b.WriteString("\n[Peer]\n")
	if serverPublicKey != "" {
		b.WriteString("PublicKey = " + serverPublicKey + "\n")
	}
	if peer.PresharedKey != "" {
		// The value belongs in the client's file: both ends need it. REQ-RES-022
		// keeps it out of an API response, which this is not.
		b.WriteString("PresharedKey = " + peer.PresharedKey + "\n")
	}
	b.WriteString("AllowedIPs = " + strings.Join(allowed, ", ") + "\n")
	b.WriteString("Endpoint = " + endpoint + "\n")
	if peer.PersistentKeepalive > 0 {
		fmt.Fprintf(&b, "PersistentKeepalive = %d\n", peer.PersistentKeepalive)
	}
	return b.String()
}
