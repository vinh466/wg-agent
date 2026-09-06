package link

import (
	"errors"
	"fmt"
	"net"
	"net/netip"
	"syscall"

	"github.com/vishvananda/netlink"
)

// Add creates a WireGuard link. netlink owns this: wgctrl configures a device
// and cannot bring one into existence.
func (n *Netlink) Add(name string) error {
	attrs := netlink.NewLinkAttrs()
	attrs.Name = name
	if err := netlink.LinkAdd(&netlink.Wireguard{LinkAttrs: attrs}); err != nil {
		return fmt.Errorf("create link %q: %w", name, err)
	}
	return nil
}

// Del removes a link.
func (n *Netlink) Del(name string) error {
	l, err := netlink.LinkByName(name)
	if err != nil {
		if isNotFound(err) {
			return nil
		}
		return fmt.Errorf("link %q: %w", name, err)
	}
	if err := netlink.LinkDel(l); err != nil {
		return fmt.Errorf("delete link %q: %w", name, err)
	}
	return nil
}

// SetUp raises the administrative flag, which is the one REQ-RES-019 reads.
func (n *Netlink) SetUp(name string) error { return n.setState(name, true) }

// SetDown lowers it, leaving the link present — the `enabled: false` of
// SPEC-01 section 3.2.
func (n *Netlink) SetDown(name string) error { return n.setState(name, false) }

func (n *Netlink) setState(name string, up bool) error {
	l, err := netlink.LinkByName(name)
	if err != nil {
		return fmt.Errorf("link %q: %w", name, err)
	}
	if up {
		err = netlink.LinkSetUp(l)
	} else {
		err = netlink.LinkSetDown(l)
	}
	if err != nil {
		return fmt.Errorf("set %q state: %w", name, err)
	}
	return nil
}

// SetMTU sets the link MTU.
func (n *Netlink) SetMTU(name string, mtu int) error {
	l, err := netlink.LinkByName(name)
	if err != nil {
		return fmt.Errorf("link %q: %w", name, err)
	}
	if err := netlink.LinkSetMTU(l, mtu); err != nil {
		return fmt.Errorf("set mtu of %q: %w", name, err)
	}
	return nil
}

// AddrAdd assigns an address. An address already present is not an error: a
// reconcile pass races nothing, but a concurrent writer could have added it,
// and the end state is what REQ-RES-003 cares about.
func (n *Netlink) AddrAdd(name string, p netip.Prefix) error {
	l, addr, err := linkAndAddr(name, p)
	if err != nil {
		return err
	}
	if err := netlink.AddrAdd(l, addr); err != nil {
		if errors.Is(err, unixEEXIST) {
			return nil
		}
		return fmt.Errorf("add %s to %q: %w", p, name, err)
	}
	return nil
}

// AddrDel removes an address, tolerating one already gone.
func (n *Netlink) AddrDel(name string, p netip.Prefix) error {
	l, addr, err := linkAndAddr(name, p)
	if err != nil {
		return err
	}
	if err := netlink.AddrDel(l, addr); err != nil {
		if isNotFound(err) {
			return nil
		}
		return fmt.Errorf("remove %s from %q: %w", p, name, err)
	}
	return nil
}

// Routes lists the routes whose output device is this interface.
//
// Only IPv4 is listed. ADR-0005 confines v1 to IPv4, so a v6 route on a managed
// link is outside what this version describes, and leaving it alone is more
// conservative than treating it as drift.
//
// Routes carrying protocol RTPROT_KERNEL are omitted. Adding an address to an
// up link makes the kernel install the subnet route for it, and step 8 of
// REQ-RCN-022 removes what the union of allowed_ips omits — a diff that saw
// that route would delete the interface's own subnet on every pass.
func (n *Netlink) Routes(name string) ([]netip.Prefix, error) {
	l, err := netlink.LinkByName(name)
	if err != nil {
		return nil, fmt.Errorf("link %q: %w", name, err)
	}
	rs, err := netlink.RouteList(l, netlink.FAMILY_V4)
	if err != nil {
		return nil, fmt.Errorf("routes of %q: %w", name, err)
	}
	out := make([]netip.Prefix, 0, len(rs))
	for _, r := range rs {
		if r.Dst == nil || int(r.Protocol) == syscall.RTPROT_KERNEL {
			continue
		}
		ip, ok := netip.AddrFromSlice(r.Dst.IP)
		if !ok {
			continue
		}
		ones, _ := r.Dst.Mask.Size()
		out = append(out, netip.PrefixFrom(ip.Unmap(), ones))
	}
	return out, nil
}

// RouteAdd adds a route through the interface.
func (n *Netlink) RouteAdd(name string, p netip.Prefix) error {
	l, err := netlink.LinkByName(name)
	if err != nil {
		return fmt.Errorf("link %q: %w", name, err)
	}
	dst, err := ipNet(p)
	if err != nil {
		return err
	}
	r := &netlink.Route{LinkIndex: l.Attrs().Index, Dst: dst, Scope: netlink.SCOPE_LINK}
	if err := netlink.RouteAdd(r); err != nil {
		if errors.Is(err, unixEEXIST) {
			return nil
		}
		return fmt.Errorf("add route %s via %q: %w", p, name, err)
	}
	return nil
}

// RouteDel removes one, tolerating a route already gone.
func (n *Netlink) RouteDel(name string, p netip.Prefix) error {
	l, err := netlink.LinkByName(name)
	if err != nil {
		return fmt.Errorf("link %q: %w", name, err)
	}
	dst, err := ipNet(p)
	if err != nil {
		return err
	}
	r := &netlink.Route{LinkIndex: l.Attrs().Index, Dst: dst, Scope: netlink.SCOPE_LINK}
	if err := netlink.RouteDel(r); err != nil {
		if isNotFound(err) {
			return nil
		}
		return fmt.Errorf("remove route %s via %q: %w", p, name, err)
	}
	return nil
}

func linkAndAddr(name string, p netip.Prefix) (netlink.Link, *netlink.Addr, error) {
	l, err := netlink.LinkByName(name)
	if err != nil {
		return nil, nil, fmt.Errorf("link %q: %w", name, err)
	}
	n, err := ipNet(p)
	if err != nil {
		return nil, nil, err
	}
	return l, &netlink.Addr{IPNet: n}, nil
}

func ipNet(p netip.Prefix) (*net.IPNet, error) {
	if !p.IsValid() {
		return nil, fmt.Errorf("invalid prefix %v", p)
	}
	ip := net.IP(p.Addr().AsSlice())
	bits := 32
	if p.Addr().Is6() {
		bits = 128
	}
	return &net.IPNet{IP: ip, Mask: net.CIDRMask(p.Bits(), bits)}, nil
}
