//go:build integration

package service_test

import (
	"os/exec"
	"path/filepath"
	"strings"
	"testing"

	"wg-agent/internal/platform/hostfs"
	"wg-agent/internal/platform/link"
	"wg-agent/internal/platform/wg"
	"wg-agent/internal/service"
	"wg-agent/internal/store"
)

// wgPublicKey reads the interface public key straight from the kernel, which is
// what a client's configuration names. It is the value adoption must not
// change.
func wgPublicKey(t *testing.T, name string) string {
	t.Helper()
	out, err := exec.Command("wg", "show", name, "public-key").Output()
	if err != nil {
		t.Skipf("cannot read the public key of %s: %v", name, err)
	}
	return strings.TrimSpace(string(out))
}

func realAdopter(t *testing.T) (service.Adopt, *store.Store) {
	t.Helper()
	device, err := wg.New()
	if err != nil {
		t.Skipf("wgctrl unavailable: %v", err)
	}
	t.Cleanup(func() { _ = device.Close() })

	st, err := store.Open(filepath.Join(t.TempDir(), "state.db"))
	if err != nil {
		t.Fatalf("Open: %v", err)
	}
	t.Cleanup(func() { _ = st.Close() })

	return service.Adopt{
		Device: device,
		Link:   link.New(),
		HostFS: hostfs.NewAt(t.TempDir()),
		Now:    func() string { return "2026-09-06T00:00:00Z" },
		NewID:  func() string { return "instance-1" },
	}, st
}

func TestAdopt_LeavesTheRunningTunnelIntact_REQ_RCN_061(t *testing.T) {
	wgLink(t, "wgadopt0", "10.130.0.1/24", "51990")

	// A peer added by hand, which is the case adoption exists to preserve.
	keys := t.TempDir()
	sh := exec.Command("sh", "-c",
		"umask 077; wg genkey > "+keys+"/k; wg pubkey < "+keys+"/k > "+keys+"/pub; wg genpsk > "+keys+"/psk")
	if out, err := sh.CombinedOutput(); err != nil {
		t.Skipf("cannot generate peer keys (%v): %s", err, out)
	}
	pub, err := exec.Command("sh", "-c", "cat "+keys+"/pub").Output()
	if err != nil {
		t.Fatal(err)
	}
	peerKey := strings.TrimSpace(string(pub))
	if out, err := exec.Command("wg", "set", "wgadopt0", "peer", peerKey,
		"preshared-key", keys+"/psk", "allowed-ips", "10.130.0.2/32").CombinedOutput(); err != nil {
		t.Skipf("cannot add a peer (%v): %s", err, out)
	}

	before := wgPublicKey(t, "wgadopt0")
	a, st := realAdopter(t)

	res, err := a.Do(st, policy("wgadopt0"), false)
	if err != nil {
		t.Fatalf("adopt: %v", err)
	}

	// The property the whole design turns on: the interface key survives, so
	// every client configuration that names it stays valid.
	if after := wgPublicKey(t, "wgadopt0"); after != before {
		t.Fatalf("adoption changed the interface key: %q became %q", before, after)
	}

	// The kernel keeps its peer, and desired state now describes it.
	peers, err := exec.Command("wg", "show", "wgadopt0", "peers").Output()
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(peers), peerKey) {
		t.Error("adoption removed the peer from the kernel")
	}
	if len(res.Peers) != 1 || res.Peers[0].PublicKey != peerKey {
		t.Errorf("the peer was not carried into desired state: %+v", res.Peers)
	}
	if res.Peers[0].PresharedKey == "" {
		t.Error("the preshared key was not carried into desired state")
	}
	if !st.Snapshot().Describes("wgadopt0") || !st.Snapshot().AdoptionRecord("wgadopt0") {
		t.Error("adoption must leave both the spec and the adoption record")
	}
}

func TestRelease_LeavesTheLinkRunning_REQ_RCN_069(t *testing.T) {
	wgLink(t, "wgadopt1", "10.131.0.1/24", "51991")
	before := wgPublicKey(t, "wgadopt1")

	a, st := realAdopter(t)
	if _, err := a.Do(st, policy("wgadopt1"), false); err != nil {
		t.Fatalf("adopt: %v", err)
	}
	if _, err := (service.Release{}).Do(st, "wgadopt1"); err != nil {
		t.Fatalf("release: %v", err)
	}

	// The link is still there, still up, still holding its key. Releasing is
	// what REQ-RCN-032 is not: a way out of management without an outage.
	out, err := exec.Command("ip", "-br", "link", "show", "wgadopt1").Output()
	if err != nil {
		t.Fatalf("the link must survive release: %v", err)
	}
	if !strings.Contains(string(out), "UP") {
		t.Errorf("the link must still be up after release: %s", out)
	}
	if after := wgPublicKey(t, "wgadopt1"); after != before {
		t.Error("release must not touch the interface key")
	}

	snap := st.Snapshot()
	if snap.Describes("wgadopt1") || snap.AdoptionRecord("wgadopt1") {
		t.Error("release must clear both the spec and the adoption record")
	}
	if snap.DeletionRecord("wgadopt1") {
		t.Error("release writes no deletion record, so the link is FOREIGN not ORPHANED")
	}
}

func TestAdopt_StoreLockRefusesASecondWriter_REQ_RCN_007(t *testing.T) {
	path := filepath.Join(t.TempDir(), "state.db")
	first, err := store.Open(path)
	if err != nil {
		t.Fatalf("Open: %v", err)
	}
	defer first.Close()

	// This is what stops `wg-agent adopt` writing behind a running agent.
	if _, err := store.Open(path); err == nil {
		t.Fatal("a second writer must be refused while the first holds the lock")
	}
}
