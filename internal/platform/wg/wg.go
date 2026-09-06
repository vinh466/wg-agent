// Package wg adapts wgctrl-go to the platform.Device port.
//
// Device and peer configuration belong here. Creating the interface does not —
// see package link.
package wg

import (
	"fmt"
	"net/netip"

	"golang.zx2c4.com/wireguard/wgctrl"
	"golang.zx2c4.com/wireguard/wgctrl/wgtypes"

	"wg-agent/internal/platform"
)

// Wgctrl implements platform.Device against the running kernel.
type Wgctrl struct {
	c *wgctrl.Client
}

// New opens a handle on the kernel's WireGuard interface.
func New() (*Wgctrl, error) {
	c, err := wgctrl.New()
	if err != nil {
		return nil, fmt.Errorf("open wgctrl: %w", err)
	}
	return &Wgctrl{c: c}, nil
}

// Close releases the handle.
func (w *Wgctrl) Close() error { return w.c.Close() }

// Names lists the WireGuard devices the kernel holds.
func (w *Wgctrl) Names() ([]string, error) {
	devs, err := w.c.Devices()
	if err != nil {
		return nil, fmt.Errorf("list devices: %w", err)
	}
	names := make([]string, 0, len(devs))
	for _, d := range devs {
		names = append(names, d.Name)
	}
	return names, nil
}

// Snapshot returns one device and all of its peers in a single read. This is
// the call adoption depends on: REQ-RCN-061 needs the interface private key and
// REQ-RCN-062 needs every peer, and the kernel supplies both at once.
func (w *Wgctrl) Snapshot(name string) (platform.DeviceState, error) {
	d, err := w.c.Device(name)
	if err != nil {
		return platform.DeviceState{}, fmt.Errorf("read device %q: %w", name, err)
	}

	out := platform.DeviceState{
		Name:       d.Name,
		PrivateKey: keyOf(d.PrivateKey),
		ListenPort: d.ListenPort,
		Fwmark:     uint32(d.FirewallMark),
	}
	if (d.PublicKey != wgtypes.Key{}) {
		out.PublicKey = d.PublicKey.String()
	}

	for _, p := range d.Peers {
		peer := platform.PeerState{
			PublicKey:           p.PublicKey.String(),
			PresharedKey:        keyOf(p.PresharedKey),
			PersistentKeepalive: p.PersistentKeepaliveInterval,
		}
		if p.Endpoint != nil {
			peer.Endpoint = p.Endpoint.String()
		}
		for _, n := range p.AllowedIPs {
			ip, ok := netip.AddrFromSlice(n.IP)
			if !ok {
				continue
			}
			ones, _ := n.Mask.Size()
			peer.AllowedIPs = append(peer.AllowedIPs, netip.PrefixFrom(ip.Unmap(), ones))
		}
		out.Peers = append(out.Peers, peer)
	}
	return out, nil
}

// keyOf converts a wgtypes key. platform.KeyFromBytes owns the rule that all
// zeros means absent, so both this adapter and package fake reach it.
func keyOf(k wgtypes.Key) platform.Key { return platform.KeyFromBytes(k[:]) }
