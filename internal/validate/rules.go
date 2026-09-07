package validate

import (
	"net"
	"net/netip"
	"regexp"

	"wg-agent/internal/model"
	"wg-agent/internal/platform"
)

// Reason codes, from the closed set of REQ-API-041. Each names the requirement
// that produces it, so a code and its rule cannot drift apart.
const (
	ReasonNameInvalid       = "INTERFACE_NAME_INVALID"          // REQ-VAL-010
	ReasonPublicKeyInvalid  = "PUBLIC_KEY_INVALID"              // REQ-VAL-011
	ReasonAllowedIPsDup     = "ALLOWED_IPS_DUPLICATE"           // REQ-VAL-012
	ReasonListenPortInUse   = "LISTEN_PORT_IN_USE"              // REQ-VAL-013
	ReasonAddressConflict   = "ADDRESS_CONFLICT"                // REQ-VAL-014
	ReasonInterfaceExists   = "INTERFACE_EXISTS"                // REQ-VAL-015
	ReasonAddressesRequired = "ADDRESSES_REQUIRED"              // REQ-VAL-016
	ReasonAllowedIPsReq     = "ALLOWED_IPS_REQUIRED"            // REQ-VAL-017
	ReasonIPv6NotSupported  = "IPV6_NOT_SUPPORTED"              // REQ-VAL-020
	ReasonNeedsUplink       = "FORWARD_POLICY_NEEDS_UPLINK"     // REQ-VAL-021
	ReasonPeerIfaceNotFound = "PEER_INTERFACE_NOT_FOUND"        // REQ-VAL-022
	ReasonOneSided          = "INTER_INTERFACE_ONE_SIDED"       // REQ-VAL-023
	ReasonAllowedIPsOverlap = "ALLOWED_IPS_OVERLAP"             // REQ-VAL-030
	ReasonAllowedIPsSubnet  = "ALLOWED_IPS_OUT_OF_SUBNET"       // REQ-VAL-031
	ReasonMTUOutOfRange     = "MTU_OUT_OF_RANGE"                // REQ-VAL-032
	ReasonEndpointNotIP     = "ENDPOINT_NOT_IP"                 // REQ-VAL-033
	ReasonPeerIfacesIgnored = "ALLOWED_PEER_INTERFACES_IGNORED" // REQ-VAL-034
	ReasonExternalNoNAT     = "EXTERNAL_WITHOUT_NAT"            // REQ-VAL-035
)

// namePattern is the expression REQ-VAL-010 fixes. The 15-character ceiling is
// IFNAMSIZ less the terminator, so a name this rejects is one the kernel would
// truncate.
var namePattern = regexp.MustCompile(`^[a-zA-Z][a-zA-Z0-9_-]{0,14}$`)

// MTU bounds of REQ-VAL-032, a warning rather than an error: the values outside
// are unusual rather than impossible.
const (
	mtuMin = 1280
	mtuMax = 1500
)

// ── errors ──────────────────────────────────────────────────────────────────

// REQ-VAL-010.
func (v Validator) checkName(r *Result, name string) {
	if !namePattern.MatchString(name) {
		r.errf(ReasonNameInvalid, "name",
			"%q does not match %s", name, namePattern.String())
	}
}

// REQ-VAL-016 and REQ-VAL-020 for the interface addresses, and REQ-VAL-014.
func (v Validator) checkAddresses(r *Result, name string, spec model.InterfaceSpec) {
	if len(spec.Addresses) == 0 {
		// REQ-VAL-016 — REQ-KEY-031 would have no address to substitute when
		// generating a client configuration.
		r.errf(ReasonAddressesRequired, "addresses",
			"an interface must carry at least one address")
		return
	}

	mine := make([]netip.Prefix, 0, len(spec.Addresses))
	for _, s := range spec.Addresses {
		p, err := netip.ParsePrefix(s)
		if err != nil {
			r.errf(ReasonAddressConflict, "addresses", "%q is not a CIDR: %v", s, err)
			continue
		}
		// REQ-VAL-020 — ADR-0005 confines v1 to IPv4, and silent omission is
		// prohibited as much as accepting the value without configuring it.
		if p.Addr().Is6() && !p.Addr().Is4In6() {
			r.errf(ReasonIPv6NotSupported, "addresses",
				"%q is IPv6; this version handles IPv4 only", s)
			continue
		}
		mine = append(mine, p)
	}

	// REQ-VAL-014 — a subnet held by a link the agent did not create collides
	// just as firmly, so foreign interfaces are included. The interface the
	// spec names is excluded, so validating an adopted spec does not match it
	// against the link it was read from.
	for _, p := range mine {
		for other, addrs := range v.Host.Addresses {
			if other == name {
				continue
			}
			for _, q := range addrs {
				if p.Overlaps(q) {
					r.errf(ReasonAddressConflict, "addresses",
						"%s overlaps %s on %s", p, q, other)
				}
			}
		}
	}
}

// REQ-VAL-013.
func (v Validator) checkListenPort(r *Result, name string, spec model.InterfaceSpec) {
	if spec.ListenPort == 0 {
		return
	}
	for other, port := range v.Host.Ports {
		if other == name || port != spec.ListenPort {
			continue
		}
		// Two WireGuard devices may hold one port while at most one is up; the
		// second bind is refused on the transition to up. Rejecting here is
		// what turns that into a message at write time.
		r.errf(ReasonListenPortInUse, "listen_port",
			"port %d is held by %s", spec.ListenPort, other)
	}
}

// REQ-VAL-015 — a create naming an existing WireGuard link, unless a deletion
// record names it.
func (v Validator) checkCreateCollision(r *Result, name string, op Op) {
	if op != Create {
		return
	}
	if _, exists := v.Host.Ports[name]; !exists {
		return
	}
	// The exemption covers the agent's own orphan under REQ-RCN-034: refusing
	// to recreate a link it failed to delete would leave a shell on the node
	// as the only recovery.
	if v.Desired != nil && v.Desired.DeletionRecord(name) {
		return
	}
	r.errf(ReasonInterfaceExists, "name",
		"a WireGuard link named %s already exists; adopt it with `wg-agent adopt %s`",
		name, name)
}

// REQ-VAL-021.
func (v Validator) checkForwardPolicy(r *Result, spec model.InterfaceSpec) {
	if spec.ForwardPolicy.External == model.Allow && !spec.NAT.EnableUplinkForwarding {
		r.errf(ReasonNeedsUplink, "forward_policy.external",
			"external = ALLOW requires nat.enable_uplink_forwarding")
	}
}

// REQ-VAL-022 — an entry naming an interface desired state does not describe.
//
// Membership of desired state is the test rather than presence on the host: the
// inter-interface policy of REQ-FWD-017 is a relationship between two
// interfaces the agent manages, and a foreign link is not one of them.
func (v Validator) checkPeerInterfaces(r *Result, spec model.InterfaceSpec) {
	if len(spec.ForwardPolicy.AllowedPeerInterfaces) == 0 {
		return
	}
	known := map[string]bool{}
	for _, n := range v.sortedNames() {
		known[n] = true
	}
	for _, n := range spec.ForwardPolicy.AllowedPeerInterfaces {
		if !known[n] {
			r.errf(ReasonPeerIfaceNotFound, "forward_policy.allowed_peer_interfaces",
				"%q is not an interface desired state describes", n)
		}
	}
}

// REQ-VAL-011, REQ-VAL-017 and REQ-VAL-020 for one peer.
func (v Validator) checkPeerErrors(r *Result, p model.Peer) {
	field := "peers[" + p.PublicKey + "]"

	// REQ-VAL-011 — base64 of exactly 32 bytes.
	if _, err := platform.ParseKey(p.PublicKey); err != nil {
		r.errf(ReasonPublicKeyInvalid, field+".public_key",
			"%q is not base64 of 32 bytes: %v", p.PublicKey, err)
	}
	if p.Spec.PresharedKey != "" {
		if _, err := platform.ParseKey(p.Spec.PresharedKey); err != nil {
			r.errf(ReasonPublicKeyInvalid, field+".preshared_key",
				"the preshared key is not base64 of 32 bytes: %v", err)
		}
	}

	// REQ-VAL-017 — a peer with no allowed_ips receives no traffic, so storing
	// it would describe an interface the operator did not mean to have.
	if len(p.Spec.AllowedIPs) == 0 {
		r.errf(ReasonAllowedIPsReq, field+".allowed_ips",
			"a peer must carry at least one allowed_ips entry")
		return
	}
	for _, s := range p.Spec.AllowedIPs {
		q, err := netip.ParsePrefix(s)
		if err != nil {
			r.errf(ReasonAllowedIPsDup, field+".allowed_ips",
				"%q is not a CIDR: %v", s, err)
			continue
		}
		if q.Addr().Is6() && !q.Addr().Is4In6() {
			r.errf(ReasonIPv6NotSupported, field+".allowed_ips",
				"%q is IPv6; this version handles IPv4 only", s)
		}
	}
}

// REQ-VAL-012 — two peers holding an identical entry. Cryptokey routing would
// be ambiguous: for two identical prefixes the later-configured peer silently
// displaces the earlier one.
func (v Validator) checkDuplicateAllowedIPs(r *Result, peers []model.Peer) {
	owner := map[netip.Prefix]string{}
	for _, p := range peers {
		for _, s := range p.Spec.AllowedIPs {
			q, err := netip.ParsePrefix(s)
			if err != nil {
				continue
			}
			q = q.Masked()
			if first, seen := owner[q]; seen && first != p.PublicKey {
				r.errf(ReasonAllowedIPsDup, "peers.allowed_ips",
					"%s is claimed by both %s and %s", q, first, p.PublicKey)
				continue
			}
			owner[q] = p.PublicKey
		}
	}
}

// ── warnings ────────────────────────────────────────────────────────────────

// REQ-VAL-032.
func (v Validator) checkMTU(r *Result, spec model.InterfaceSpec) {
	if spec.MTU == 0 {
		return
	}
	if spec.MTU < mtuMin || spec.MTU > mtuMax {
		r.warnf(ReasonMTUOutOfRange, "mtu",
			"%d falls outside the usual range %d to %d", spec.MTU, mtuMin, mtuMax)
	}
}

// REQ-VAL-023 — an ALLOW_LIST relationship declared in one direction only.
// Valid and stateful under REQ-FWD-017, but usually a forgotten reciprocal.
func (v Validator) checkOneSidedAllowList(r *Result, name string, spec model.InterfaceSpec) {
	if spec.ForwardPolicy.InterInterface != model.AllowList || v.Desired == nil {
		return
	}
	for _, other := range spec.ForwardPolicy.AllowedPeerInterfaces {
		os, ok, err := v.Desired.Interface(other)
		if err != nil || !ok {
			// REQ-VAL-022 already reports an unknown name as an error.
			continue
		}
		reciprocal := false
		for _, back := range os.ForwardPolicy.AllowedPeerInterfaces {
			if back == name {
				reciprocal = true
				break
			}
		}
		if !reciprocal {
			r.warnf(ReasonOneSided, "forward_policy.allowed_peer_interfaces",
				"%s permits %s, and %s does not permit %s in return",
				name, other, other, name)
		}
	}
}

// REQ-VAL-034 — the field is ignored in that combination.
func (v Validator) checkIgnoredPeerInterfaces(r *Result, spec model.InterfaceSpec) {
	if len(spec.ForwardPolicy.AllowedPeerInterfaces) == 0 {
		return
	}
	if spec.ForwardPolicy.InterInterface != model.AllowList {
		r.warnf(ReasonPeerIfacesIgnored, "forward_policy.allowed_peer_interfaces",
			"the list is ignored while inter_interface is %s",
			spec.ForwardPolicy.InterInterface)
	}
}

// REQ-VAL-035 — traffic leaves without source NAT, so return traffic almost
// certainly has no route back.
func (v Validator) checkExternalWithoutNAT(r *Result, spec model.InterfaceSpec) {
	if spec.ForwardPolicy.External == model.Allow && !spec.NAT.Enabled {
		r.warnf(ReasonExternalNoNAT, "nat.enabled",
			"external = ALLOW without source NAT; return traffic needs a route back")
	}
}

// REQ-VAL-030 — entries that overlap at differing prefix lengths. Longest-
// prefix matching makes this valid, and it usually indicates a mistake.
func (v Validator) checkOverlappingAllowedIPs(r *Result, peers []model.Peer) {
	type entry struct {
		prefix netip.Prefix
		peer   string
	}
	var all []entry
	for _, p := range peers {
		for _, s := range p.Spec.AllowedIPs {
			q, err := netip.ParsePrefix(s)
			if err != nil {
				continue
			}
			all = append(all, entry{prefix: q.Masked(), peer: p.PublicKey})
		}
	}
	for i := 0; i < len(all); i++ {
		for j := i + 1; j < len(all); j++ {
			a, b := all[i], all[j]
			// Identical prefixes are REQ-VAL-012's error, not this warning.
			if a.prefix.Bits() == b.prefix.Bits() {
				continue
			}
			if !a.prefix.Overlaps(b.prefix) {
				continue
			}
			r.warnf(ReasonAllowedIPsOverlap, "peers.allowed_ips",
				"%s on %s overlaps %s on %s at a different prefix length",
				a.prefix, a.peer, b.prefix, b.peer)
		}
	}
}

// REQ-VAL-031 — valid for site-to-site, usually a mistake otherwise.
func (v Validator) checkAllowedIPsOutOfSubnet(
	r *Result, spec model.InterfaceSpec, peers []model.Peer,
) {
	var subnets []netip.Prefix
	for _, s := range spec.Addresses {
		if p, err := netip.ParsePrefix(s); err == nil {
			subnets = append(subnets, p.Masked())
		}
	}
	if len(subnets) == 0 {
		return
	}

	for _, p := range peers {
		for _, s := range p.Spec.AllowedIPs {
			q, err := netip.ParsePrefix(s)
			if err != nil {
				continue
			}
			q = q.Masked()
			inside := false
			for _, sub := range subnets {
				// Containment, not overlap: a peer prefix wider than the
				// interface subnet reaches outside it.
				if sub.Overlaps(q) && q.Bits() >= sub.Bits() {
					inside = true
					break
				}
			}
			if !inside {
				r.warnf(ReasonAllowedIPsSubnet, "peers.allowed_ips",
					"%s on %s falls outside the interface subnet", q, p.PublicKey)
			}
		}
	}
}

// REQ-VAL-033 — the kernel stores only the resolved address, so a DNS change
// does not propagate.
func (v Validator) checkPeerWarnings(r *Result, p model.Peer) {
	if p.Spec.Endpoint == "" {
		return
	}
	field := "peers[" + p.PublicKey + "].endpoint"
	host, _, err := net.SplitHostPort(p.Spec.Endpoint)
	if err != nil {
		r.warnf(ReasonEndpointNotIP, field,
			"%q is not host:port: %v", p.Spec.Endpoint, err)
		return
	}
	if _, err := netip.ParseAddr(host); err != nil {
		r.warnf(ReasonEndpointNotIP, field,
			"%q is a hostname; the kernel stores only the address it resolves to now",
			p.Spec.Endpoint)
	}
}
