package wg

import (
	"fmt"
	"net"

	"golang.zx2c4.com/wireguard/wgctrl/wgtypes"

	"wg-agent/internal/platform"
)

// Configure applies a delta to a WireGuard device.
//
// A nil pointer leaves the field as the kernel holds it, which is what lets
// REQ-RCN-011 enforce the agent-owned fields without disturbing anything else.
func (w *Wgctrl) Configure(name string, cfg platform.DeviceConfig) error {
	out := wgtypes.Config{ReplacePeers: cfg.ReplacePeers}

	if cfg.PrivateKey != nil {
		// Unlike a preshared key, an absent private key is refused rather than
		// written as zero: that would leave the device unable to handshake, and
		// no requirement asks for it.
		if !cfg.PrivateKey.Present() {
			return fmt.Errorf("configure %q: private key is absent", name)
		}
		k, err := parseKey(cfg.PrivateKey.Base64())
		if err != nil {
			return fmt.Errorf("private key: %w", err)
		}
		out.PrivateKey = &k
	}
	if cfg.ListenPort != nil {
		p := *cfg.ListenPort
		out.ListenPort = &p
	}
	if cfg.Fwmark != nil {
		m := int(*cfg.Fwmark)
		out.FirewallMark = &m
	}

	for _, p := range cfg.Peers {
		pc, err := peerConfig(p)
		if err != nil {
			return err
		}
		out.Peers = append(out.Peers, pc)
	}

	if err := w.c.ConfigureDevice(name, out); err != nil {
		return fmt.Errorf("configure %q: %w", name, err)
	}
	return nil
}

func peerConfig(p platform.PeerConfig) (wgtypes.PeerConfig, error) {
	key, err := parseKey(p.PublicKey)
	if err != nil {
		return wgtypes.PeerConfig{}, fmt.Errorf("peer public key %q: %w", p.PublicKey, err)
	}

	out := wgtypes.PeerConfig{
		PublicKey:         key,
		Remove:            p.Remove,
		UpdateOnly:        p.UpdateOnly,
		ReplaceAllowedIPs: p.ReplaceAllowedIPs,
	}
	if p.Remove {
		// The kernel ignores every other field when removing, and sending them
		// would only invite a mismatch between what was asked and what happened.
		return out, nil
	}

	if p.PresharedKey != nil {
		// An absent key clears the peer's preshared key: the kernel has no
		// "unset" message for one, so zero is the only way to say it.
		var k wgtypes.Key
		if p.PresharedKey.Present() {
			var err error
			if k, err = parseKey(p.PresharedKey.Base64()); err != nil {
				return wgtypes.PeerConfig{}, fmt.Errorf("preshared key: %w", err)
			}
		}
		out.PresharedKey = &k
	}
	for _, a := range p.AllowedIPs {
		bits := 32
		if a.Addr().Is6() {
			bits = 128
		}
		out.AllowedIPs = append(out.AllowedIPs, net.IPNet{
			IP:   net.IP(a.Addr().AsSlice()),
			Mask: net.CIDRMask(a.Bits(), bits),
		})
	}
	if p.Endpoint != nil {
		addr, err := net.ResolveUDPAddr("udp", *p.Endpoint)
		if err != nil {
			return wgtypes.PeerConfig{}, fmt.Errorf("endpoint %q: %w", *p.Endpoint, err)
		}
		out.Endpoint = addr
	}
	if p.PersistentKeepalive != nil {
		d := *p.PersistentKeepalive
		out.PersistentKeepaliveInterval = &d
	}
	return out, nil
}

func parseKey(b64 string) (wgtypes.Key, error) {
	if b64 == "" {
		return wgtypes.Key{}, fmt.Errorf("empty key")
	}
	return wgtypes.ParseKey(b64)
}
