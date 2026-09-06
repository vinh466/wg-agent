//go:build integration

// Integration tier: the engine drives a real kernel through the real adapters,
// over the real store. Nothing is faked, which is what makes this the test that
// would catch an adapter that compiles but does not work.
//
// Run inside a dedicated network namespace — `make docker-test` supplies one —
// never on a host whose interfaces matter.
package reconcile_test

import (
	"os/exec"
	"path/filepath"
	"strings"
	"testing"

	"wg-agent/internal/model"
	"wg-agent/internal/platform/link"
	"wg-agent/internal/platform/wg"
	"wg-agent/internal/reconcile"
	"wg-agent/internal/store"
)

func realEngine(t *testing.T) (*reconcile.Engine, *store.Store) {
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

	return &reconcile.Engine{Store: s, Link: link.New(), Device: dev}, s
}

func put(t *testing.T, s *store.Store, name string, spec model.InterfaceSpec, peers []model.Peer) {
	t.Helper()
	err := s.Update(func(tx *store.Txn) error {
		if err := tx.PutInterface(name, spec, "test-instance", "2026-01-01T00:00:00Z"); err != nil {
			return err
		}
		return tx.PutPeers(name, peers)
	})
	if err != nil {
		t.Fatalf("write desired state: %v", err)
	}
}

// removeLink drops an interface the test created, tolerating one already gone.
func removeLink(t *testing.T, name string) {
	t.Helper()
	t.Cleanup(func() { _ = exec.Command("ip", "link", "del", name).Run() })
}

func genKey(t *testing.T) (priv, pub string) {
	t.Helper()
	p, err := exec.Command("wg", "genkey").Output()
	if err != nil {
		t.Fatalf("wg genkey: %v", err)
	}
	priv = strings.TrimSpace(string(p))
	cmd := exec.Command("wg", "pubkey")
	cmd.Stdin = strings.NewReader(priv + "\n")
	q, err := cmd.Output()
	if err != nil {
		t.Fatalf("wg pubkey: %v", err)
	}
	return priv, strings.TrimSpace(string(q))
}

func show(t *testing.T, args ...string) string {
	t.Helper()
	out, err := exec.Command("wg", args...).Output()
	if err != nil {
		t.Fatalf("wg %v: %v", args, err)
	}
	return strings.TrimSpace(string(out))
}

// A pass creates the link, configures the device, assigns the address and
// brings it up — REQ-RCN-022 against a real kernel.
func TestIntegration_Reconcile_CreatesAndConfigures_REQ_RCN_022(t *testing.T) {
	removeLink(t, "wgr0")
	e, s := realEngine(t)

	priv, _ := genKey(t)
	_, peerPub := genKey(t)
	put(t, s, "wgr0", model.InterfaceSpec{
		PrivateKey: priv,
		ListenPort: 51999,
		Addresses:  []string{"10.90.0.1/24"},
		MTU:        1380,
		Enabled:    true,
	}, []model.Peer{{
		InterfaceName: "wgr0",
		PublicKey:     peerPub,
		Spec:          model.PeerSpec{AllowedIPs: []string{"10.90.0.2/32"}},
	}})

	st := e.Interface("wgr0")
	if st.Condition.State != reconcile.Ready {
		t.Fatalf("condition = %+v, want READY", st.Condition)
	}

	if got := show(t, "show", "wgr0", "listen-port"); got != "51999" {
		t.Errorf("listen-port = %q, want 51999", got)
	}
	if got := show(t, "show", "wgr0", "peers"); got != peerPub {
		t.Errorf("peers = %q, want %q", got, peerPub)
	}
	addr, err := exec.Command("ip", "-br", "addr", "show", "wgr0").Output()
	if err != nil {
		t.Fatalf("ip addr: %v", err)
	}
	if !strings.Contains(string(addr), "10.90.0.1/24") {
		t.Errorf("address missing: %s", addr)
	}

	// The administrative flag, not the operational state. A WireGuard link
	// reports its operational state as UNKNOWN even while up, which is the
	// observation REQ-RES-019 is built on, so `ip -br` column two says nothing.
	flags, err := exec.Command("ip", "link", "show", "wgr0").Output()
	if err != nil {
		t.Fatalf("ip link: %v", err)
	}
	if !strings.Contains(string(flags), ",UP,") {
		t.Errorf("link not administratively up: %s", flags)
	}
	mtu, _ := exec.Command("cat", "/sys/class/net/wgr0/mtu").Output()
	if strings.TrimSpace(string(mtu)) != "1380" {
		t.Errorf("mtu = %q, want 1380", strings.TrimSpace(string(mtu)))
	}
	if st.OperState != reconcile.OperUp {
		t.Errorf("oper_state = %q, want UP", st.OperState)
	}
}

// REQ-RES-003: a second pass over an unchanged interface leaves the kernel
// exactly as it was. Against a real kernel the sharpest evidence is that the
// peer's handshake state and endpoint survive.
func TestIntegration_Reconcile_SecondPassIsInert_REQ_RES_003(t *testing.T) {
	removeLink(t, "wgr1")
	e, s := realEngine(t)

	priv, _ := genKey(t)
	_, peerPub := genKey(t)
	put(t, s, "wgr1", model.InterfaceSpec{
		PrivateKey: priv,
		ListenPort: 51998,
		Addresses:  []string{"10.91.0.1/24"},
		MTU:        1420,
		Enabled:    true,
	}, []model.Peer{{
		InterfaceName: "wgr1",
		PublicKey:     peerPub,
		Spec:          model.PeerSpec{AllowedIPs: []string{"10.91.0.2/32"}},
	}})

	if st := e.Interface("wgr1"); st.Condition.State != reconcile.Ready {
		t.Fatalf("first pass: %+v", st.Condition)
	}

	// Stand in for an endpoint the kernel learned from a handshake.
	if err := exec.Command("wg", "set", "wgr1", "peer", peerPub,
		"endpoint", "203.0.113.40:51820").Run(); err != nil {
		t.Fatalf("seed endpoint: %v", err)
	}
	before := show(t, "show", "wgr1", "dump")

	if st := e.Interface("wgr1"); st.Condition.State != reconcile.Ready {
		t.Fatalf("second pass: %+v", st.Condition)
	}

	after := show(t, "show", "wgr1", "dump")
	if before != after {
		t.Errorf("second pass changed the device.\nbefore: %s\nafter:  %s", before, after)
	}
}

// REQ-RCN-051 against a real kernel: an endpoint the kernel holds survives a
// pass whose desired state carries none. This is the case REQ-RCN-063 leaves
// behind after adoption, and the one whole-list replacement would break.
func TestIntegration_Reconcile_PreservesLearnedEndpoint_REQ_RCN_051(t *testing.T) {
	removeLink(t, "wgr2")
	e, s := realEngine(t)

	priv, _ := genKey(t)
	_, peerPub := genKey(t)
	put(t, s, "wgr2", model.InterfaceSpec{
		PrivateKey: priv,
		ListenPort: 51997,
		Addresses:  []string{"10.92.0.1/24"},
		MTU:        1420,
		Enabled:    true,
	}, []model.Peer{{
		InterfaceName: "wgr2",
		PublicKey:     peerPub,
		Spec:          model.PeerSpec{AllowedIPs: []string{"10.92.0.2/32"}},
	}})
	e.Interface("wgr2")

	if err := exec.Command("wg", "set", "wgr2", "peer", peerPub,
		"endpoint", "203.0.113.41:51820").Run(); err != nil {
		t.Fatalf("seed endpoint: %v", err)
	}

	// Drift the peer's allowed_ips so the pass has a genuine reason to write.
	if err := exec.Command("wg", "set", "wgr2", "peer", peerPub,
		"allowed-ips", "10.92.0.99/32").Run(); err != nil {
		t.Fatalf("seed drift: %v", err)
	}

	if st := e.Interface("wgr2"); st.Condition.State != reconcile.Ready {
		t.Fatalf("condition = %+v", st.Condition)
	}

	if got := show(t, "show", "wgr2", "allowed-ips"); !strings.Contains(got, "10.92.0.2/32") {
		t.Errorf("allowed-ips = %q, want the spec's enforced", got)
	}
	got := show(t, "show", "wgr2", "endpoints")
	if !strings.Contains(got, "203.0.113.41:51820") {
		t.Errorf("endpoints = %q, want the learned endpoint preserved", got)
	}
}

// REQ-RCN-024 and step 7 before step 8: the kernel refuses a route whose output
// device is down, so a pass that routes must have raised the link first.
func TestIntegration_Reconcile_RoutesAfterLinkUp_REQ_RCN_024(t *testing.T) {
	removeLink(t, "wgr3")
	e, s := realEngine(t)

	priv, _ := genKey(t)
	_, peerPub := genKey(t)
	spec := model.InterfaceSpec{
		PrivateKey:   priv,
		ListenPort:   51996,
		Addresses:    []string{"10.93.0.1/24"},
		MTU:          1420,
		ManageRoutes: true,
		Enabled:      true,
	}
	put(t, s, "wgr3", spec, []model.Peer{{
		InterfaceName: "wgr3",
		PublicKey:     peerPub,
		Spec:          model.PeerSpec{AllowedIPs: []string{"10.230.0.0/24"}},
	}})

	st := e.Interface("wgr3")
	if st.Condition.State != reconcile.Ready {
		t.Fatalf("condition = %+v, want READY", st.Condition)
	}

	out, err := exec.Command("ip", "route", "show", "dev", "wgr3").Output()
	if err != nil {
		t.Fatalf("ip route: %v", err)
	}
	if !strings.Contains(string(out), "10.230.0.0/24") {
		t.Errorf("route missing: %s", out)
	}

	// The interface's own subnet route is the kernel's, not the agent's. A
	// second pass must leave it alone rather than delete it as drift.
	if !strings.Contains(string(out), "10.93.0.0/24") {
		t.Fatalf("kernel subnet route absent to begin with: %s", out)
	}
	if st := e.Interface("wgr3"); st.Condition.State != reconcile.Ready {
		t.Fatalf("second pass: %+v", st.Condition)
	}
	out2, err := exec.Command("ip", "route", "show", "dev", "wgr3").Output()
	if err != nil {
		t.Fatalf("ip route: %v", err)
	}
	if !strings.Contains(string(out2), "10.93.0.0/24") {
		t.Errorf("the second pass deleted the kernel's own subnet route: %s", out2)
	}
}

// REQ-RCN-036 over a real host: a WireGuard link desired state does not
// describe is classified and left untouched.
func TestIntegration_Reconcile_ClassifiesForeignLink_REQ_RCN_036(t *testing.T) {
	removeLink(t, "wgr4")
	if err := exec.Command("ip", "link", "add", "wgr4", "type", "wireguard").Run(); err != nil {
		t.Fatalf("create foreign link: %v", err)
	}
	if err := exec.Command("wg", "set", "wgr4", "listen-port", "51995").Run(); err != nil {
		t.Fatalf("configure foreign link: %v", err)
	}
	before := show(t, "show", "wgr4", "dump")

	e, _ := realEngine(t)
	if err := e.Pass(); err != nil {
		t.Fatalf("pass: %v", err)
	}

	st, ok := e.Status("wgr4")
	if !ok {
		t.Fatal("no status for the foreign link")
	}
	if st.Ownership != "FOREIGN" {
		t.Errorf("ownership = %q, want FOREIGN", st.Ownership)
	}
	if after := show(t, "show", "wgr4", "dump"); after != before {
		t.Errorf("a foreign link was modified.\nbefore: %s\nafter:  %s", before, after)
	}
}

// A link of the wrong type is DEGRADED and untouched — step 2 of REQ-RCN-022.
func TestIntegration_Reconcile_WrongLinkTypeUntouched_REQ_RCN_022(t *testing.T) {
	removeLink(t, "wgr5")
	if err := exec.Command("ip", "link", "add", "wgr5", "type", "dummy").Run(); err != nil {
		t.Skipf("dummy links unavailable: %v", err)
	}

	e, s := realEngine(t)
	priv, _ := genKey(t)
	put(t, s, "wgr5", model.InterfaceSpec{
		PrivateKey: priv,
		ListenPort: 51994,
		Addresses:  []string{"10.95.0.1/24"},
		Enabled:    true,
	}, nil)

	st := e.Interface("wgr5")
	if st.Condition.State != reconcile.Degraded {
		t.Fatalf("condition = %+v, want DEGRADED", st.Condition)
	}
	if st.Condition.Reason != reconcile.ReasonReconcileFailed {
		t.Errorf("reason = %q", st.Condition.Reason)
	}
	out, err := exec.Command("ip", "-br", "addr", "show", "wgr5").Output()
	if err != nil {
		t.Fatalf("ip addr: %v", err)
	}
	if strings.Contains(string(out), "10.95.0.1") {
		t.Errorf("the link was configured despite the wrong type: %s", out)
	}
}

// The store implements reconcile.Store. A compile-time assertion would live in
// the store, which does not import reconcile; asserting it here keeps the
// dependency pointing one way.
var _ reconcile.Store = (*store.Store)(nil)
