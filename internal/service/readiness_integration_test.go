//go:build integration

// Integration tier: the report is built over the real adapters against a real
// WireGuard interface. Run inside a dedicated network namespace — `make
// docker-test` supplies one — never on a host whose interfaces matter.
package service_test

import (
	"os/exec"
	"testing"

	"wg-agent/internal/platform/hostfs"
	"wg-agent/internal/platform/link"
	"wg-agent/internal/platform/wg"
	"wg-agent/internal/service"
	"wg-agent/internal/store"
)

// wgLink creates a WireGuard interface for the duration of a test.
//
// REQ-SEC-041 forbids a child process in a production path; this is a test
// helper, which the requirement permits explicitly. The product code under test
// reaches the kernel through netlink and wgctrl only.
func wgLink(t *testing.T, name, cidr string, port string) {
	t.Helper()
	wgLinkState(t, name, cidr, port, true)
}

// wgLinkState creates a WireGuard interface, optionally leaving it down.
//
// The kernel permits two devices to hold one listen port while at most one is
// up, and refuses the second bind on the transition to up. A collision between
// two existing links is therefore only reachable with one of them down, which
// is exactly the case REQ-VAL-013 has to catch before adoption brings it up.
func wgLinkState(t *testing.T, name, cidr string, port string, up bool) {
	t.Helper()
	run := func(args ...string) {
		t.Helper()
		if out, err := exec.Command(args[0], args[1:]...).CombinedOutput(); err != nil {
			t.Skipf("cannot set up %s (%v): %s", name, err, out)
		}
	}
	run("ip", "link", "add", name, "type", "wireguard")
	t.Cleanup(func() { _ = exec.Command("ip", "link", "del", name).Run() })
	run("ip", "addr", "add", cidr, "dev", name)
	if up {
		run("ip", "link", "set", name, "up")
	}

	keyfile := t.TempDir() + "/key"
	sh := exec.Command("sh", "-c", "umask 077; wg genkey > "+keyfile)
	if out, err := sh.CombinedOutput(); err != nil {
		t.Skipf("cannot generate a key (%v): %s", err, out)
	}
	run("wg", "set", name, "listen-port", port, "private-key", keyfile)
}

func realReport(t *testing.T) service.Report {
	t.Helper()
	device, err := wg.New()
	if err != nil {
		t.Skipf("wgctrl unavailable: %v", err)
	}
	t.Cleanup(func() { _ = device.Close() })

	desired, err := store.Read(t.TempDir() + "/absent.db")
	if err != nil {
		t.Fatal(err)
	}
	r, err := service.Readiness{
		Device:  device,
		Link:    link.New(),
		HostFS:  hostfs.NewAt(t.TempDir()),
		Desired: desired,
	}.Build()
	if err != nil {
		t.Fatalf("Build against the real kernel: %v", err)
	}
	return r
}

func TestReadiness_RealInterfaceIsAdoptable_REQ_DIA_040(t *testing.T) {
	wgLink(t, "wgtest0", "10.123.0.1/24", "51999")

	i := only(t, realReport(t), "wgtest0")
	if i.Ownership != service.Foreign {
		t.Fatalf("a link no store describes is FOREIGN, got %s", i.Ownership)
	}
	if i.Spec == nil {
		t.Fatal("a foreign interface must carry the spec adoption would read")
	}
	// The premise of REQ-RCN-061: the kernel hands the interface key back, so
	// adoption can preserve it and leave every client configuration valid.
	if !i.Spec.PrivateKeyPresent {
		t.Error("the kernel must report the interface private key as present")
	}
	if i.Spec.ListenPort != 51999 {
		t.Errorf("listen port: want 51999, got %d", i.Spec.ListenPort)
	}
	if i.Spec.MTU == 0 {
		t.Error("mtu must come from netlink")
	}
	// REQ-RES-019: derived from the administrative flag, because a WireGuard
	// link reports its operational state as unknown even while up.
	if !i.Spec.Enabled {
		t.Error("an interface set up must report enabled")
	}
	if len(i.Spec.Addresses) != 1 || i.Spec.Addresses[0] != "10.123.0.1/24" {
		t.Errorf("addresses: want [10.123.0.1/24], got %v", i.Spec.Addresses)
	}
	if i.Blocking() {
		t.Errorf("a clean interface must not block: %+v", i.Findings)
	}
}

func TestReadiness_RealPeerIsReadBack_REQ_RCN_062(t *testing.T) {
	wgLink(t, "wgtest1", "10.124.0.1/24", "51998")

	// A peer with a preshared key, which REQ-RCN-062 has to carry into desired
	// state and REQ-DIA-047 has to keep out of the report.
	keys := t.TempDir()
	sh := exec.Command("sh", "-c",
		"umask 077; wg genkey > "+keys+"/peer; wg pubkey < "+keys+"/peer > "+keys+"/pub; wg genpsk > "+keys+"/psk")
	if out, err := sh.CombinedOutput(); err != nil {
		t.Skipf("cannot generate peer keys (%v): %s", err, out)
	}
	pub, err := exec.Command("sh", "-c", "cat "+keys+"/pub").Output()
	if err != nil {
		t.Fatal(err)
	}
	pubkey := string(pub[:len(pub)-1])
	set := exec.Command("wg", "set", "wgtest1", "peer", pubkey,
		"preshared-key", keys+"/psk", "allowed-ips", "10.124.0.2/32",
		"persistent-keepalive", "25")
	if out, err := set.CombinedOutput(); err != nil {
		t.Skipf("cannot add a peer (%v): %s", err, out)
	}

	i := only(t, realReport(t), "wgtest1")
	if len(i.Spec.Peers) != 1 {
		t.Fatalf("want 1 peer read back, got %d", len(i.Spec.Peers))
	}
	p := i.Spec.Peers[0]
	if p.PublicKey != pubkey {
		t.Errorf("public key: want %q, got %q", pubkey, p.PublicKey)
	}
	if !p.PresharedKeyPresent {
		t.Error("the preshared key must be reported present, so adoption can carry it")
	}
	if len(p.AllowedIPs) != 1 || p.AllowedIPs[0] != "10.124.0.2/32" {
		t.Errorf("allowed ips: got %v", p.AllowedIPs)
	}
	if p.PersistentKeepalive != 25 {
		t.Errorf("keepalive: want 25, got %d", p.PersistentKeepalive)
	}
	if p.EndpointPresent {
		t.Error("a peer that has never handshaked holds no endpoint")
	}
}

func TestReadiness_RealPortCollisionFails_REQ_VAL_013(t *testing.T) {
	// The second interface stays down: the kernel refuses the second bind on
	// the transition to up, so this is the reachable shape of the collision and
	// the one adoption would otherwise walk into at apply time.
	wgLink(t, "wgtest2", "10.125.0.1/24", "51997")
	wgLinkState(t, "wgtest3", "10.126.0.1/24", "51997", false)

	i := only(t, realReport(t), "wgtest2")
	if !i.Blocking() {
		t.Fatalf("two host interfaces on one port must block: %+v", i.Findings)
	}
	found := false
	for _, f := range i.Findings {
		if f.HintCode == service.HintPortCollision {
			found = true
		}
	}
	if !found {
		t.Error("want the port-collision finding")
	}
}
