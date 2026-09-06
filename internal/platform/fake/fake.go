// Package fake implements the platform ports in memory.
//
// This is what keeps the rule that only internal/platform needs privilege: a
// test above that layer builds the node it wants here and runs as an ordinary
// user in milliseconds. A test that reaches for root above the adapters is a
// sign the seam has leaked.
package fake

import (
	"fmt"
	"net/netip"
	"sort"
	"time"

	"wg-agent/internal/platform"
)

// Node is an in-memory model of one host's WireGuard state.
type Node struct {
	Links      map[string]platform.LinkState
	Devices    map[string]platform.DeviceState
	Units      map[string]platform.UnitState
	Configs    map[string]platform.WgQuickConfig
	Managed    map[string]bool
	Deleted    map[string]bool
	Forwarding map[string]string
	RouteTable map[string][]netip.Prefix

	// SysctlReadOnly makes every forwarding write fail, which is what an
	// ordinary container does and what REQ-CFG-011 exists to carve out.
	SysctlReadOnly bool

	// Calls records every mutation in order. A test asserts on it to prove a
	// write did not happen, which is what REQ-RCN-012 and REQ-RCN-051 demand.
	Calls []string
}

// NewNode returns an empty node.
func NewNode() *Node {
	return &Node{
		Links:      map[string]platform.LinkState{},
		Devices:    map[string]platform.DeviceState{},
		Units:      map[string]platform.UnitState{},
		Configs:    map[string]platform.WgQuickConfig{},
		Managed:    map[string]bool{},
		Deleted:    map[string]bool{},
		Forwarding: map[string]string{},
		RouteTable: map[string][]netip.Prefix{},
	}
}

// testKey returns a distinct non-zero key. Zeros would read back as absent,
// because platform.KeyFromBytes follows the kernel in treating an all-zero key
// as no key at all.
func testKey(seed byte) platform.Key {
	b := make([]byte, 32)
	for i := range b {
		b[i] = seed
	}
	return platform.KeyFromBytes(b)
}

// AddInterface registers a WireGuard interface with one address and no peers.
func (n *Node) AddInterface(name, cidr string, port int) *Node {
	p, err := netip.ParsePrefix(cidr)
	if err != nil {
		panic(fmt.Sprintf("fake: bad cidr %q: %v", cidr, err))
	}
	n.Links[name] = platform.LinkState{
		Name: name, Type: "wireguard", MTU: 1420, AdminUp: true,
		Addresses: []netip.Prefix{p},
	}
	n.Devices[name] = platform.DeviceState{
		Name:       name,
		PrivateKey: testKey(1),
		PublicKey:  "pub-" + name,
		ListenPort: port,
	}
	return n
}

// AddPeer appends a peer to an interface.
func (n *Node) AddPeer(iface, publicKey, allowed string, endpoint string, psk bool) *Node {
	d := n.Devices[iface]
	p := platform.PeerState{PublicKey: publicKey, Endpoint: endpoint,
		PersistentKeepalive: 25 * time.Second}
	if psk {
		p.PresharedKey = testKey(2)
	}
	if allowed != "" {
		pre, err := netip.ParsePrefix(allowed)
		if err != nil {
			panic(fmt.Sprintf("fake: bad allowed-ips %q: %v", allowed, err))
		}
		p.AllowedIPs = []netip.Prefix{pre}
	}
	d.Peers = append(d.Peers, p)
	n.Devices[iface] = d
	return n
}

// WithAddress replaces an interface's address list.
func (n *Node) WithAddress(iface string, cidrs ...string) *Node {
	l := n.Links[iface]
	l.Addresses = nil
	for _, c := range cidrs {
		p, err := netip.ParsePrefix(c)
		if err != nil {
			panic(fmt.Sprintf("fake: bad cidr %q: %v", c, err))
		}
		l.Addresses = append(l.Addresses, p)
	}
	n.Links[iface] = l
	return n
}

// WithLinkType overrides the netlink type, for the not-a-WireGuard-link case.
func (n *Node) WithLinkType(iface, t string) *Node {
	l := n.Links[iface]
	l.Type = t
	n.Links[iface] = l
	return n
}

// WithUnit sets the wg-quick unit state for an interface.
func (n *Node) WithUnit(iface string, s platform.UnitState) *Node {
	n.Units[iface] = s
	return n
}

// WithConfig sets the wg-quick configuration findings for an interface.
func (n *Node) WithConfig(iface string, c platform.WgQuickConfig) *Node {
	n.Configs[iface] = c
	return n
}

// WithManaged marks an interface as described by desired state.
func (n *Node) WithManaged(iface string) *Node { n.Managed[iface] = true; return n }

// WithDeletionRecord marks an interface as named by a deletion record.
func (n *Node) WithDeletionRecord(iface string) *Node { n.Deleted[iface] = true; return n }

// ── platform.Device ─────────────────────────────────────────────────────────

func (n *Node) Names() ([]string, error) {
	out := make([]string, 0, len(n.Devices))
	for k := range n.Devices {
		out = append(out, k)
	}
	sort.Strings(out)
	return out, nil
}

func (n *Node) Snapshot(name string) (platform.DeviceState, error) {
	d, ok := n.Devices[name]
	if !ok {
		return platform.DeviceState{}, fmt.Errorf("no such device %q", name)
	}
	return d, nil
}

// ── platform.Link ───────────────────────────────────────────────────────────

// LinkView adapts Node to platform.Link, whose Names covers every interface
// rather than the WireGuard ones alone.
type LinkView struct{ N *Node }

func (l LinkView) Names() ([]string, error) {
	out := make([]string, 0, len(l.N.Links))
	for k := range l.N.Links {
		out = append(out, k)
	}
	sort.Strings(out)
	return out, nil
}

func (l LinkView) State(name string) (platform.LinkState, error) {
	s, ok := l.N.Links[name]
	if !ok {
		return platform.LinkState{}, fmt.Errorf("no such link %q", name)
	}
	return s, nil
}

// ── platform.HostFS ─────────────────────────────────────────────────────────

func (n *Node) WgQuickUnit(iface string) (platform.UnitState, error) {
	s, ok := n.Units[iface]
	if !ok {
		return platform.UnitAbsent, nil
	}
	return s, nil
}

func (n *Node) WgQuickConfig(iface string) (platform.WgQuickConfig, error) {
	return n.Configs[iface], nil
}

func (n *Node) ForwardingSysctl(iface string) (string, error) {
	v, ok := n.Forwarding[iface]
	if !ok {
		return "", nil
	}
	return v, nil
}

// SetForwardingSysctl records the write, and refuses when SysctlReadOnly is
// set — the ordinary container case, and what `ProtectKernelTunables=yes`
// produces without the carve-out of REQ-CFG-011.
func (n *Node) SetForwardingSysctl(iface, value string) error {
	if _, ok := n.Links[iface]; !ok {
		// The node goes with the link.
		return nil
	}
	if n.Forwarding[iface] == value {
		return nil
	}
	if n.SysctlReadOnly {
		return fmt.Errorf("write forwarding sysctl of %q: read-only file system", iface)
	}
	n.Forwarding[iface] = value
	n.Calls = append(n.Calls, "SetForwarding("+iface+","+value+")")
	return nil
}

// WithForwarding sets the interface's forwarding baseline.
func (n *Node) WithForwarding(iface, v string) *Node {
	n.Forwarding[iface] = v
	return n
}

// WithReadOnlySysctl makes every forwarding write fail.
func (n *Node) WithReadOnlySysctl() *Node { n.SysctlReadOnly = true; return n }

// ── platform.DesiredState ───────────────────────────────────────────────────

func (n *Node) Describes(name string) bool      { return n.Managed[name] }
func (n *Node) DeletionRecord(name string) bool { return n.Deleted[name] }

// Compile-time proof that the fake satisfies every port it claims.
var (
	_ platform.Device       = (*Node)(nil)
	_ platform.HostFS       = (*Node)(nil)
	_ platform.DesiredState = (*Node)(nil)
	_ platform.Link         = LinkView{}
)
