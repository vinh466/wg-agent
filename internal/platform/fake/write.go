package fake

import (
	"fmt"
	"net/netip"
	"strings"

	"wg-agent/internal/platform"
)

// The write side of the fake. It mutates the in-memory node, so a test can
// drive a whole reconcile pass and then assert what the node became — which is
// the property that keeps the reconcile algorithm testable without privilege.
//
// Calls records every mutation in order, so a test can also assert what was
// *not* done: REQ-RCN-012 and REQ-RCN-051 are both statements about writes that
// must not happen.

// ── platform.Device write side ──────────────────────────────────────────────

func (n *Node) Configure(name string, cfg platform.DeviceConfig) error {
	d, ok := n.Devices[name]
	if !ok {
		return fmt.Errorf("no such device %q", name)
	}
	n.Calls = append(n.Calls, "Configure("+name+")")

	if cfg.PrivateKey != nil {
		d.PrivateKey = *cfg.PrivateKey
		// The kernel derives the public key on receipt, so the fake does too.
		// Leaving it empty would let a test above the port see a device the
		// kernel could not be in: one with a private key and no public one.
		if pub, err := d.PrivateKey.PublicKey(); err == nil {
			d.PublicKey = pub.Base64()
		} else {
			d.PublicKey = ""
		}
		n.Calls = append(n.Calls, "SetPrivateKey("+name+")")
	}
	if cfg.ListenPort != nil {
		d.ListenPort = *cfg.ListenPort
		n.Calls = append(n.Calls, fmt.Sprintf("SetListenPort(%s,%d)", name, *cfg.ListenPort))
	}
	if cfg.Fwmark != nil {
		d.Fwmark = *cfg.Fwmark
		n.Calls = append(n.Calls, fmt.Sprintf("SetFwmark(%s,%#x)", name, *cfg.Fwmark))
	}

	if cfg.ReplacePeers {
		d.Peers = nil
		n.Calls = append(n.Calls, "ReplacePeers("+name+")")
	}

	for _, pc := range cfg.Peers {
		idx := -1
		for i := range d.Peers {
			if d.Peers[i].PublicKey == pc.PublicKey {
				idx = i
				break
			}
		}

		if pc.Remove {
			if idx >= 0 {
				d.Peers = append(d.Peers[:idx], d.Peers[idx+1:]...)
			}
			n.Calls = append(n.Calls, "RemovePeer("+name+","+short(pc.PublicKey)+")")
			continue
		}
		if idx < 0 && pc.UpdateOnly {
			// The kernel would not create it either.
			continue
		}
		if idx < 0 {
			d.Peers = append(d.Peers, platform.PeerState{PublicKey: pc.PublicKey})
			idx = len(d.Peers) - 1
			n.Calls = append(n.Calls, "AddPeer("+name+","+short(pc.PublicKey)+")")
		} else {
			n.Calls = append(n.Calls, "UpdatePeer("+name+","+short(pc.PublicKey)+")")
		}

		p := &d.Peers[idx]
		if pc.PresharedKey != nil {
			p.PresharedKey = *pc.PresharedKey
		}
		if pc.ReplaceAllowedIPs || len(pc.AllowedIPs) > 0 {
			p.AllowedIPs = append([]netip.Prefix(nil), pc.AllowedIPs...)
		}
		if pc.Endpoint != nil {
			p.Endpoint = *pc.Endpoint
			n.Calls = append(n.Calls, "SetEndpoint("+name+","+short(pc.PublicKey)+")")
		}
		if pc.PersistentKeepalive != nil {
			p.PersistentKeepalive = *pc.PersistentKeepalive
		}
	}

	n.Devices[name] = d
	return nil
}

// ── platform.Link write side ────────────────────────────────────────────────

func (l LinkView) Add(name string) error {
	if _, ok := l.N.Links[name]; ok {
		return fmt.Errorf("link %q exists", name)
	}
	l.N.Links[name] = platform.LinkState{Name: name, Type: "wireguard", MTU: 1420}
	l.N.Devices[name] = platform.DeviceState{Name: name}
	l.N.Calls = append(l.N.Calls, "LinkAdd("+name+")")
	return nil
}

func (l LinkView) Del(name string) error {
	delete(l.N.Links, name)
	delete(l.N.Devices, name)
	delete(l.N.RouteTable, name)
	l.N.Calls = append(l.N.Calls, "LinkDel("+name+")")
	return nil
}

func (l LinkView) SetUp(name string) error   { return l.setState(name, true) }
func (l LinkView) SetDown(name string) error { return l.setState(name, false) }

func (l LinkView) setState(name string, up bool) error {
	s, ok := l.N.Links[name]
	if !ok {
		return fmt.Errorf("no such link %q", name)
	}
	s.AdminUp = up
	l.N.Links[name] = s
	verb := "LinkSetDown("
	if up {
		verb = "LinkSetUp("
	}
	l.N.Calls = append(l.N.Calls, verb+name+")")
	return nil
}

func (l LinkView) SetMTU(name string, mtu int) error {
	s, ok := l.N.Links[name]
	if !ok {
		return fmt.Errorf("no such link %q", name)
	}
	s.MTU = mtu
	l.N.Links[name] = s
	l.N.Calls = append(l.N.Calls, fmt.Sprintf("SetMTU(%s,%d)", name, mtu))
	return nil
}

func (l LinkView) AddrAdd(name string, p netip.Prefix) error {
	s, ok := l.N.Links[name]
	if !ok {
		return fmt.Errorf("no such link %q", name)
	}
	for _, a := range s.Addresses {
		if a == p {
			return nil
		}
	}
	s.Addresses = append(s.Addresses, p)
	l.N.Links[name] = s
	l.N.Calls = append(l.N.Calls, "AddrAdd("+name+","+p.String()+")")
	return nil
}

func (l LinkView) AddrDel(name string, p netip.Prefix) error {
	s, ok := l.N.Links[name]
	if !ok {
		return fmt.Errorf("no such link %q", name)
	}
	out := s.Addresses[:0]
	for _, a := range s.Addresses {
		if a != p {
			out = append(out, a)
		}
	}
	s.Addresses = append([]netip.Prefix(nil), out...)
	l.N.Links[name] = s
	l.N.Calls = append(l.N.Calls, "AddrDel("+name+","+p.String()+")")
	return nil
}

func (l LinkView) Routes(name string) ([]netip.Prefix, error) {
	if _, ok := l.N.Links[name]; !ok {
		return nil, fmt.Errorf("no such link %q", name)
	}
	return append([]netip.Prefix(nil), l.N.RouteTable[name]...), nil
}

func (l LinkView) RouteAdd(name string, p netip.Prefix) error {
	for _, r := range l.N.RouteTable[name] {
		if r == p {
			return nil
		}
	}
	l.N.RouteTable[name] = append(l.N.RouteTable[name], p)
	l.N.Calls = append(l.N.Calls, "RouteAdd("+name+","+p.String()+")")
	return nil
}

func (l LinkView) RouteDel(name string, p netip.Prefix) error {
	rs := l.N.RouteTable[name]
	out := rs[:0]
	for _, r := range rs {
		if r != p {
			out = append(out, r)
		}
	}
	l.N.RouteTable[name] = append([]netip.Prefix(nil), out...)
	l.N.Calls = append(l.N.Calls, "RouteDel("+name+","+p.String()+")")
	return nil
}

// Did reports whether any recorded call contains the substring.
func (n *Node) Did(substr string) bool {
	for _, c := range n.Calls {
		if strings.Contains(c, substr) {
			return true
		}
	}
	return false
}

// ResetCalls clears the record, so a test can assert what a second pass did.
func (n *Node) ResetCalls() { n.Calls = nil }

func short(k string) string {
	if len(k) <= 8 {
		return k
	}
	return k[:8]
}

// ── platform.LinkEvents ─────────────────────────────────────────────────────

// Events is an in-memory subscription. A test pushes an event and asserts that
// the runner reconciled, without a netlink socket.
type Events struct {
	Ch chan platform.LinkEvent
	// Err, when set, is what Subscribe returns. It covers the case a runner has
	// to survive: a subscription that cannot be established leaves the periodic
	// timer as the only trigger rather than stopping the agent.
	Err error
}

// NewEvents returns a subscription with a buffered channel.
func NewEvents() *Events {
	return &Events{Ch: make(chan platform.LinkEvent, 8)}
}

func (e *Events) Subscribe(done <-chan struct{}) (<-chan platform.LinkEvent, error) {
	if e.Err != nil {
		return nil, e.Err
	}
	return e.Ch, nil
}

// Send delivers one event.
func (e *Events) Send(name string, deleted bool) {
	e.Ch <- platform.LinkEvent{Name: name, Deleted: deleted}
}
