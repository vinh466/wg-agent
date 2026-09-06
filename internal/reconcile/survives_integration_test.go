//go:build integration

package reconcile_test

import (
	"os/exec"
	"path/filepath"
	"strings"
	"testing"

	"wg-agent/internal/model"
	"wg-agent/internal/platform/hostfs"
	"wg-agent/internal/platform/link"
	"wg-agent/internal/platform/wg"
	"wg-agent/internal/reconcile"
	"wg-agent/internal/service"
	"wg-agent/internal/store"
)

// The migration this project exists for, end to end: an interface set up by
// hand is adopted, then vanishes the way a reboot makes it vanish, and a pass
// brings it back with the same key and the same peers.
//
// Until the engine existed the store was written and read by nothing, so an
// adopted interface did not survive a reboot — the hazard
// docs/50-guides/running-tests.md warns about. This test is what closes it.
func TestIntegration_AdoptedInterfaceReturnsAfterItsLinkIsGone_REQ_RCN_020(t *testing.T) {
	const name = "wgs0"
	removeLink(t, name)

	// A hand-made interface, the way wg-quick would leave one.
	priv, pub := genKey(t)
	_, peerPub := genKey(t)
	mustRun(t, "ip", "link", "add", name, "type", "wireguard")
	writeKey(t, priv)
	mustRun(t, "wg", "set", name, "private-key", "/tmp/wgs0.key", "listen-port", "51993")
	mustRun(t, "wg", "set", name, "peer", peerPub, "allowed-ips", "10.94.0.2/32")
	mustRun(t, "ip", "addr", "add", "10.94.0.1/24", "dev", name)
	mustRun(t, "ip", "link", "set", name, "up")

	path := filepath.Join(t.TempDir(), "state.db")

	// Adopt it. The store is opened and closed here, because REQ-RCN-006 gives
	// one holder the lock at a time and the engine below takes it next.
	adopt(t, path, name)

	// The link vanishes, as it does across a reboot.
	mustRun(t, "ip", "link", "del", name)
	if _, err := exec.Command("ip", "link", "show", name).Output(); err == nil {
		t.Fatal("the link is still present")
	}

	// One pass, which is what `wg-agent serve` runs on startup.
	dev, err := wg.New()
	if err != nil {
		t.Fatalf("open wgctrl: %v", err)
	}
	defer func() { _ = dev.Close() }()

	s, err := store.Open(path)
	if err != nil {
		t.Fatalf("open store: %v", err)
	}
	defer func() { _ = s.Close() }()

	e := &reconcile.Engine{Store: s, Link: link.New(), Device: dev}
	if err := e.Pass(); err != nil {
		t.Fatalf("pass: %v", err)
	}

	st, ok := e.Status(name)
	if !ok || st.Condition.State != reconcile.Ready {
		t.Fatalf("status = %+v, ok = %v; want READY", st, ok)
	}

	// Same key, so every client configuration written before adoption is still
	// valid — the property REQ-RCN-061 exists for.
	if got := show(t, "show", name, "public-key"); got != pub {
		t.Errorf("public key = %q, want %q", got, pub)
	}
	if got := show(t, "show", name, "listen-port"); got != "51993" {
		t.Errorf("listen-port = %q, want 51993", got)
	}
	if got := show(t, "show", name, "peers"); got != peerPub {
		t.Errorf("peers = %q, want the adopted peer %q", got, peerPub)
	}
	addr, err := exec.Command("ip", "-br", "addr", "show", name).Output()
	if err != nil {
		t.Fatalf("ip addr: %v", err)
	}
	if !strings.Contains(string(addr), "10.94.0.1/24") {
		t.Errorf("address not restored: %s", addr)
	}
	flags, err := exec.Command("ip", "link", "show", name).Output()
	if err != nil {
		t.Fatalf("ip link: %v", err)
	}
	if !strings.Contains(string(flags), ",UP,") {
		t.Errorf("link not brought back up: %s", flags)
	}
}

func adopt(t *testing.T, path, name string) {
	t.Helper()
	s, err := store.Open(path)
	if err != nil {
		t.Fatalf("open store: %v", err)
	}
	defer func() { _ = s.Close() }()

	dev, err := wg.New()
	if err != nil {
		t.Fatalf("open wgctrl: %v", err)
	}
	defer func() { _ = dev.Close() }()

	a := service.Adopt{
		Device: dev,
		Link:   link.New(),
		HostFS: hostfs.New(),
		Now:    func() string { return "2026-01-01T00:00:00Z" },
		NewID:  func() string { return "test-instance" },
	}
	policy := model.DefaultForwardPolicy()
	manageRoutes := false
	if _, err := a.Do(s, service.AdoptRequest{
		Name:          name,
		ForwardPolicy: &policy,
		NAT:           &model.NatSpec{},
		ManageRoutes:  &manageRoutes,
	}, false); err != nil {
		t.Fatalf("adopt: %v", err)
	}
}

func mustRun(t *testing.T, name string, args ...string) {
	t.Helper()
	if out, err := exec.Command(name, args...).CombinedOutput(); err != nil {
		t.Fatalf("%s %v: %v: %s", name, args, err, out)
	}
}

func writeKey(t *testing.T, priv string) {
	t.Helper()
	if err := exec.Command("sh", "-c",
		"printf '%s\n' "+priv+" > /tmp/wgs0.key").Run(); err != nil {
		t.Fatalf("write key: %v", err)
	}
}
