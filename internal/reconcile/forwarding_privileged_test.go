//go:build integration && privileged

// Privileged tier: `/proc/sys` is writable here and nowhere else, so this is
// the only tier that can verify step 9 of REQ-RCN-022. Run it with `make
// docker-test-privileged`.
//
// A privileged container is not isolated from the host, which is why this tier
// is separate. Every interface it touches is named `wgp*` and removed.
package reconcile_test

import (
	"os"
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

func sysctlPath(iface string) string {
	return filepath.Join("/proc/sys/net/ipv4/conf", iface, "forwarding")
}

func readForwarding(t *testing.T, iface string) string {
	t.Helper()
	b, err := os.ReadFile(sysctlPath(iface))
	if err != nil {
		t.Fatalf("read forwarding of %s: %v", iface, err)
	}
	return strings.TrimSpace(string(b))
}

// setForwarding seeds the value. Both container tiers inherit 1 from
// net.ipv4.conf.default.forwarding, so a test that did not seed 0 would assert
// nothing: the write step would be skipped as already-matching.
func setForwarding(t *testing.T, iface, v string) {
	t.Helper()
	if err := os.WriteFile(sysctlPath(iface), []byte(v+"\n"), 0o644); err != nil {
		t.Fatalf("seed forwarding of %s: %v", iface, err)
	}
}

func privilegedEngine(t *testing.T) (*reconcile.Engine, *store.Store) {
	t.Helper()
	dev, err := wg.New()
	if err != nil {
		t.Fatalf("open wgctrl: %v", err)
	}
	t.Cleanup(func() { _ = dev.Close() })

	s, err := store.Open(filepath.Join(t.TempDir(), "state.db"))
	if err != nil {
		t.Fatalf("open store: %v", err)
	}
	t.Cleanup(func() { _ = s.Close() })

	return &reconcile.Engine{
		Store:  s,
		Link:   link.New(),
		Device: dev,
		HostFS: hostfs.New(),
	}, s
}

func privSpec(priv string, policy model.ForwardPolicySpec, port int) model.InterfaceSpec {
	return model.InterfaceSpec{
		PrivateKey:    priv,
		ListenPort:    port,
		Addresses:     []string{"10.97.0.1/24"},
		MTU:           1420,
		ForwardPolicy: policy,
		Enabled:       true,
	}
}

// REQ-FWD-020 against a real /proc/sys: with intra_interface = ALLOW the value
// goes from 0 to 1. Without this, two peers of one interface are not routed to
// each other, which is what the flat-LAN-per-group pattern is.
func TestPrivileged_SetsForwardingWhenIntraAllows_REQ_FWD_020(t *testing.T) {
	const name = "wgp0"
	removeLink(t, name)
	if err := exec.Command("ip", "link", "add", name, "type", "wireguard").Run(); err != nil {
		t.Fatalf("create link: %v", err)
	}
	setForwarding(t, name, "0")

	e, s := privilegedEngine(t)
	priv, _ := genKey(t)
	put(t, s, name, privSpec(priv, model.DefaultForwardPolicy(), 51990), nil)

	if st := e.Interface(name); st.Condition.State != reconcile.Ready {
		t.Fatalf("condition = %+v, want READY", st.Condition)
	}
	if got := readForwarding(t, name); got != "1" {
		t.Errorf("forwarding = %q, want 1", got)
	}
}

// Both axes DENY: no requirement asks for the value to be driven to 0, and
// step 10 — the nftables table that would enforce a DENY — is deferred under
// B-04, so the seeded value stands.
func TestPrivileged_LeavesForwardingWhenBothAxesDeny_REQ_FWD_020(t *testing.T) {
	const name = "wgp1"
	removeLink(t, name)
	if err := exec.Command("ip", "link", "add", name, "type", "wireguard").Run(); err != nil {
		t.Fatalf("create link: %v", err)
	}
	setForwarding(t, name, "0")

	e, s := privilegedEngine(t)
	priv, _ := genKey(t)
	put(t, s, name, privSpec(priv, model.ForwardPolicySpec{
		IntraInterface: model.Deny,
		InterInterface: model.Deny,
		External:       model.Deny,
	}, 51989), nil)

	if st := e.Interface(name); st.Condition.State != reconcile.Ready {
		t.Fatalf("condition = %+v, want READY", st.Condition)
	}
	if got := readForwarding(t, name); got != "0" {
		t.Errorf("forwarding = %q, want the seeded 0 left alone", got)
	}
}

// REQ-FWD-022 against a real /proc/sys: an interface desired state does not
// describe keeps its value, even while another interface on the same host is
// reconciled.
func TestPrivileged_LeavesForeignForwardingAlone_REQ_FWD_022(t *testing.T) {
	const managed, foreign = "wgp2", "wgp3"
	removeLink(t, managed)
	removeLink(t, foreign)
	for _, n := range []string{managed, foreign} {
		if err := exec.Command("ip", "link", "add", n, "type", "wireguard").Run(); err != nil {
			t.Fatalf("create %s: %v", n, err)
		}
		setForwarding(t, n, "0")
	}

	e, s := privilegedEngine(t)
	priv, _ := genKey(t)
	put(t, s, managed, privSpec(priv, model.DefaultForwardPolicy(), 51988), nil)

	if err := e.Pass(); err != nil {
		t.Fatalf("pass: %v", err)
	}

	if got := readForwarding(t, managed); got != "1" {
		t.Errorf("managed forwarding = %q, want 1", got)
	}
	if got := readForwarding(t, foreign); got != "0" {
		t.Errorf("foreign forwarding = %q, want it untouched at 0", got)
	}
}

// REQ-FWD-024: release restores what adoption found, rather than leaving the
// value the agent set. Read against a real /proc/sys, this is the tier where
// the restore is observable at all.
func TestPrivileged_ReleaseRestoresTheAdoptionBaseline_REQ_FWD_024(t *testing.T) {
	const name = "wgp4"
	removeLink(t, name)

	priv, _ := genKey(t)
	mustRun(t, "ip", "link", "add", name, "type", "wireguard")
	wgSetKey(t, name, priv, "listen-port", "51987")
	mustRun(t, "ip", "addr", "add", "10.98.0.1/24", "dev", name)
	mustRun(t, "ip", "link", "set", name, "up")
	setForwarding(t, name, "0")

	path := filepath.Join(t.TempDir(), "state.db")
	adopt(t, path, name)

	// One pass sets it, because DefaultForwardPolicy has intra = ALLOW.
	e2, s2 := func() (*reconcile.Engine, *store.Store) {
		dev, err := wg.New()
		if err != nil {
			t.Fatalf("open wgctrl: %v", err)
		}
		t.Cleanup(func() { _ = dev.Close() })
		s, err := store.Open(path)
		if err != nil {
			t.Fatalf("open store: %v", err)
		}
		return &reconcile.Engine{
			Store: s, Link: link.New(), Device: dev, HostFS: hostfs.New(),
		}, s
	}()

	if st := e2.Interface(name); st.Condition.State != reconcile.Ready {
		t.Fatalf("pass: %+v", st.Condition)
	}
	if got := readForwarding(t, name); got != "1" {
		t.Fatalf("forwarding = %q after the pass, want 1", got)
	}
	if err := s2.Close(); err != nil {
		t.Fatalf("close store: %v", err)
	}

	// Release restores the pre-adoption value.
	s3, err := store.Open(path)
	if err != nil {
		t.Fatalf("reopen store: %v", err)
	}
	defer func() { _ = s3.Close() }()

	res, err := service.Release{
		RestoreForwarding: hostfs.New().SetForwardingSysctl,
	}.Do(s3, name)
	if err != nil {
		t.Fatalf("release: %v", err)
	}
	if res.ForwardingRestored != "0" {
		t.Errorf("restored %q, want the adoption baseline 0", res.ForwardingRestored)
	}
	if got := readForwarding(t, name); got != "0" {
		t.Errorf("forwarding = %q after release, want 0", got)
	}
	// REQ-RCN-069: the link is still running.
	if err := exec.Command("ip", "link", "show", name).Run(); err != nil {
		t.Errorf("release removed the link: %v", err)
	}
}
