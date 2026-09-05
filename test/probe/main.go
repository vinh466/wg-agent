// Probe: verifies the library boundary asserted in docs/00-overview/architecture.md
// and the adoption premise of ADR-0011 / REQ-RCN-061 against a real kernel.
package main

import (
	"fmt"
	"net"
	"os"
	"time"

	"github.com/vishvananda/netlink"
	"golang.zx2c4.com/wireguard/wgctrl"
	"golang.zx2c4.com/wireguard/wgctrl/wgtypes"
)

func main() {
	if err := run(); err != nil {
		fmt.Fprintln(os.Stderr, "FAIL:", err)
		os.Exit(1)
	}
	fmt.Println("\nALL PROBES PASSED")
}

func run() error {
	const name = "wg0"

	// --- netlink owns link lifecycle. wgctrl cannot do this. ---
	la := netlink.NewLinkAttrs()
	la.Name = name
	link := &netlink.Wireguard{LinkAttrs: la}

	if err := netlink.LinkAdd(link); err != nil {
		return fmt.Errorf("netlink.LinkAdd: %w", err)
	}
	fmt.Println("[1] netlink.LinkAdd(&netlink.Wireguard{})   OK")
	defer func() { _ = netlink.LinkDel(link) }()

	if err := netlink.LinkSetMTU(link, 1420); err != nil {
		return fmt.Errorf("netlink.LinkSetMTU: %w", err)
	}
	addr, err := netlink.ParseAddr("10.99.0.1/24")
	if err != nil {
		return err
	}
	if err := netlink.AddrAdd(link, addr); err != nil {
		return fmt.Errorf("netlink.AddrAdd: %w", err)
	}
	if err := netlink.LinkSetUp(link); err != nil {
		return fmt.Errorf("netlink.LinkSetUp: %w", err)
	}
	fmt.Println("[2] netlink SetMTU / AddrAdd / LinkSetUp    OK")

	// --- wgctrl owns device and peer configuration. ---
	c, err := wgctrl.New()
	if err != nil {
		return fmt.Errorf("wgctrl.New: %w", err)
	}
	defer c.Close()

	priv, err := wgtypes.GeneratePrivateKey()
	if err != nil {
		return err
	}
	peerKey, err := wgtypes.GeneratePrivateKey()
	if err != nil {
		return err
	}
	psk, err := wgtypes.GenerateKey()
	if err != nil {
		return err
	}
	port, fwmark := 51820, 0x1234
	ka := 25 * time.Second
	_, allowed, err := net.ParseCIDR("10.99.0.2/32")
	if err != nil {
		return err
	}

	if err := c.ConfigureDevice(name, wgtypes.Config{
		PrivateKey:   &priv,
		ListenPort:   &port,
		FirewallMark: &fwmark,
		Peers: []wgtypes.PeerConfig{{
			PublicKey:                   peerKey.PublicKey(),
			PresharedKey:                &psk,
			AllowedIPs:                  []net.IPNet{*allowed},
			PersistentKeepaliveInterval: &ka,
		}},
	}); err != nil {
		return fmt.Errorf("wgctrl.ConfigureDevice: %w", err)
	}
	fmt.Println("[3] wgctrl.ConfigureDevice + PeerConfig     OK")

	// --- The adoption read. Everything REQ-RCN-061/062 needs, in one call. ---
	d, err := c.Device(name)
	if err != nil {
		return fmt.Errorf("wgctrl.Device: %w", err)
	}
	var zero wgtypes.Key
	fmt.Printf("[4] wgctrl.Device snapshot                  OK\n")
	fmt.Printf("      private_key present : %v\n", d.PrivateKey != zero)
	fmt.Printf("      public_key present  : %v\n", d.PublicKey != zero)
	fmt.Printf("      listen_port         : %d\n", d.ListenPort)
	fmt.Printf("      fwmark              : %#x\n", d.FirewallMark)
	fmt.Printf("      peers               : %d\n", len(d.Peers))

	if d.PrivateKey != priv {
		return fmt.Errorf("private key did not round-trip: REQ-RCN-061 is not implementable this way")
	}
	fmt.Println("[5] REQ-RCN-061: private key round-trips    OK")

	for _, p := range d.Peers {
		if p.PresharedKey == zero {
			return fmt.Errorf("preshared key absent from the device read: REQ-RCN-062 loses it")
		}
		fmt.Printf("[6] REQ-RCN-062: peer readable              OK\n")
		fmt.Printf("      preshared_key present : %v\n", p.PresharedKey != zero)
		fmt.Printf("      allowed_ips           : %v\n", p.AllowedIPs)
		fmt.Printf("      keepalive             : %v\n", p.PersistentKeepaliveInterval)
		fmt.Printf("      endpoint              : %v  (kernel-owned, REQ-RCN-063)\n", p.Endpoint)
	}

	// --- netlink read-back for the rest of the adoption source table. ---
	fresh, err := netlink.LinkByName(name)
	if err != nil {
		return fmt.Errorf("netlink.LinkByName: %w", err)
	}
	addrs, err := netlink.AddrList(fresh, netlink.FAMILY_V4)
	if err != nil {
		return fmt.Errorf("netlink.AddrList: %w", err)
	}
	fmt.Printf("[7] netlink read-back                       OK\n")
	fmt.Printf("      mtu       : %d\n", fresh.Attrs().MTU)
	fmt.Printf("      addresses : %v\n", addrs)
	fmt.Printf("      oper_state: %v\n", fresh.Attrs().OperState)
	fmt.Printf("      type      : %s\n", fresh.Type())

	if fresh.Type() != "wireguard" {
		return fmt.Errorf("link type is %q, so the REQ-DIA-042 not-a-wireguard-link check needs another source", fresh.Type())
	}
	fmt.Println("[8] link type is discoverable as wireguard  OK")

	if err := netlink.LinkDel(link); err != nil {
		return fmt.Errorf("netlink.LinkDel: %w", err)
	}
	fmt.Println("[9] netlink.LinkDel                         OK")
	return nil
}
