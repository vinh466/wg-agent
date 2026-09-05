// Package link adapts vishvananda/netlink to the platform.Link port.
//
// Interface lifecycle and addressing belong here and not to wgctrl, which
// cannot create a link — the boundary docs/00-overview/architecture.md fixes.
package link

import (
	"fmt"
	"net/netip"

	"github.com/vishvananda/netlink"

	"wg-agent/internal/platform"
)

// Netlink implements platform.Link against the running kernel.
type Netlink struct{}

// New returns an adapter over the host's netlink socket.
func New() *Netlink { return &Netlink{} }

// Names lists every interface the kernel holds.
func (n *Netlink) Names() ([]string, error) {
	links, err := netlink.LinkList()
	if err != nil {
		return nil, fmt.Errorf("list links: %w", err)
	}
	names := make([]string, 0, len(links))
	for _, l := range links {
		names = append(names, l.Attrs().Name)
	}
	return names, nil
}

// State returns one interface with its IPv4 addresses.
func (n *Netlink) State(name string) (platform.LinkState, error) {
	l, err := netlink.LinkByName(name)
	if err != nil {
		return platform.LinkState{}, fmt.Errorf("link %q: %w", name, err)
	}
	a := l.Attrs()

	// Both families are read. REQ-VAL-020 rejects IPv6, and the readiness
	// report has to see such an address in order to report it rather than drop
	// it silently — REQ-RCN-061 requires every address the link carries.
	addrs, err := netlink.AddrList(l, netlink.FAMILY_ALL)
	if err != nil {
		return platform.LinkState{}, fmt.Errorf("addresses of %q: %w", name, err)
	}

	out := platform.LinkState{
		Name:    a.Name,
		Type:    l.Type(),
		MTU:     a.MTU,
		AdminUp: a.Flags&netlinkFlagUp != 0,
	}
	for _, ad := range addrs {
		if ad.IPNet == nil {
			continue
		}
		p, err := prefixOf(ad)
		if err != nil {
			continue
		}
		out.Addresses = append(out.Addresses, p)
	}
	return out, nil
}

// netlinkFlagUp is net.FlagUp. It is named here so the intent of the mask reads
// clearly at the call site: REQ-RES-019 wants the administrative flag, not the
// operational state the kernel reports, which is unknown for a WireGuard link.
const netlinkFlagUp = 1 << 0

func prefixOf(a netlink.Addr) (netip.Prefix, error) {
	ip, ok := netip.AddrFromSlice(a.IP)
	if !ok {
		return netip.Prefix{}, fmt.Errorf("unparseable address %v", a.IP)
	}
	ones, _ := a.Mask.Size()
	return netip.PrefixFrom(ip.Unmap(), ones), nil
}
