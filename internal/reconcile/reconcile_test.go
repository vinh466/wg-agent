package reconcile_test

import (
	"encoding/base64"
	"strings"
	"sync"
	"testing"

	"wg-agent/internal/model"
	"wg-agent/internal/platform"
	"wg-agent/internal/platform/fake"
	"wg-agent/internal/reconcile"
)

// memStore is desired state in memory. The real store is exercised in
// store_test.go; here the subject is the algorithm, so the store is reduced to
// the six questions reconcile.Store asks.
type memStore struct {
	ifaces    map[string]model.InterfaceSpec
	peers     map[string][]model.Peer
	deletions map[string]bool
	cleared   []string
}

func newStore() *memStore {
	return &memStore{
		ifaces:    map[string]model.InterfaceSpec{},
		peers:     map[string][]model.Peer{},
		deletions: map[string]bool{},
	}
}

func (m *memStore) Names() []string {
	out := make([]string, 0, len(m.ifaces))
	for k := range m.ifaces {
		out = append(out, k)
	}
	return out
}

func (m *memStore) Interface(n string) (model.InterfaceSpec, bool, error) {
	s, ok := m.ifaces[n]
	return s, ok, nil
}
func (m *memStore) Peers(n string) ([]model.Peer, error) { return m.peers[n], nil }
func (m *memStore) Describes(n string) bool              { _, ok := m.ifaces[n]; return ok }
func (m *memStore) DeletionRecord(n string) bool         { return m.deletions[n] }

func (m *memStore) DeletionNames() []string {
	out := make([]string, 0, len(m.deletions))
	for k := range m.deletions {
		out = append(out, k)
	}
	return out
}

func (m *memStore) ClearDeletion(n string) error {
	delete(m.deletions, n)
	m.cleared = append(m.cleared, n)
	return nil
}

// key returns a distinct valid 32-byte key in the base64 form REQ-RES-027
// fixes, so a test can tell two keys apart.
func key(seed byte) string {
	b := make([]byte, 32)
	for i := range b {
		b[i] = seed
	}
	return base64.StdEncoding.EncodeToString(b)
}

func engine(n *fake.Node, s *memStore) *reconcile.Engine {
	return &reconcile.Engine{Store: s, Link: fake.LinkView{N: n}, Device: n}
}

// baseSpec is an interface a pass should be able to bring to READY.
func baseSpec() model.InterfaceSpec {
	return model.InterfaceSpec{
		PrivateKey: key(1),
		ListenPort: 51820,
		Addresses:  []string{"10.100.0.1/24"},
		MTU:        1420,
		Enabled:    true,
	}
}

func peer(pub, allowed string) model.Peer {
	return model.Peer{
		InterfaceName: "wg0",
		PublicKey:     pub,
		Spec:          model.PeerSpec{AllowedIPs: []string{allowed}},
	}
}

func index(calls []string, substr string) int {
	for i, c := range calls {
		if strings.Contains(c, substr) {
			return i
		}
	}
	return -1
}

// ── the algorithm ───────────────────────────────────────────────────────────

// Step 1 of REQ-RCN-022: a link desired state describes but the host lacks is
// created rather than reported as an error.
func TestReconcile_CreatesAbsentLink_REQ_RCN_022(t *testing.T) {
	n := fake.NewNode()
	s := newStore()
	s.ifaces["wg0"] = baseSpec()

	st := engine(n, s).Interface("wg0")

	if st.Condition.State != reconcile.Ready {
		t.Fatalf("condition = %+v, want READY", st.Condition)
	}
	if _, ok := n.Links["wg0"]; !ok {
		t.Fatal("link was not created")
	}
	if !n.Did("LinkAdd(wg0)") {
		t.Errorf("no LinkAdd recorded; calls = %v", n.Calls)
	}
	if got := n.Devices["wg0"].PrivateKey.Base64(); got != key(1) {
		t.Errorf("private key not applied")
	}
	if got := n.Devices["wg0"].ListenPort; got != 51820 {
		t.Errorf("listen port = %d, want 51820", got)
	}
	if got := n.Links["wg0"].Addresses; len(got) != 1 || got[0].String() != "10.100.0.1/24" {
		t.Errorf("addresses = %v, want [10.100.0.1/24]", got)
	}
	if !n.Links["wg0"].AdminUp {
		t.Error("link was not brought up")
	}
	if st.OperState != reconcile.OperUp {
		t.Errorf("oper_state = %q, want UP", st.OperState)
	}
}

// REQ-RES-003: repeating a write leaves the same state. A second pass over an
// interface already in the state its spec asks for writes nothing at all, which
// is what keeps the 30-second timer of REQ-RCN-020 quiet.
func TestReconcile_SecondPassWritesNothing_REQ_RES_003(t *testing.T) {
	n := fake.NewNode()
	s := newStore()
	s.ifaces["wg0"] = baseSpec()
	s.peers["wg0"] = []model.Peer{peer(key(9), "10.100.0.2/32")}

	e := engine(n, s)
	if st := e.Interface("wg0"); st.Condition.State != reconcile.Ready {
		t.Fatalf("first pass: %+v", st.Condition)
	}
	n.ResetCalls()

	if st := e.Interface("wg0"); st.Condition.State != reconcile.Ready {
		t.Fatalf("second pass: %+v", st.Condition)
	}
	if len(n.Calls) != 0 {
		t.Errorf("second pass wrote %v, want nothing", n.Calls)
	}
}

// Step 2: a link of the wrong type is marked DEGRADED and left untouched.
func TestReconcile_NotWireguardLinkUntouched_REQ_RCN_022(t *testing.T) {
	n := fake.NewNode()
	n.AddInterface("wg0", "10.100.0.1/24", 51820).WithLinkType("wg0", "dummy")
	s := newStore()
	s.ifaces["wg0"] = baseSpec()

	n.ResetCalls()
	st := engine(n, s).Interface("wg0")

	if st.Condition.State != reconcile.Degraded {
		t.Fatalf("condition = %+v, want DEGRADED", st.Condition)
	}
	if st.Condition.Reason != reconcile.ReasonReconcileFailed {
		t.Errorf("reason = %q", st.Condition.Reason)
	}
	if len(n.Calls) != 0 {
		t.Errorf("link was touched: %v", n.Calls)
	}
}

// REQ-RCN-011: every agent-owned field is enforced on each pass. Drift in each
// is corrected.
func TestReconcile_EnforcesAgentOwnedFields_REQ_RCN_011(t *testing.T) {
	n := fake.NewNode()
	n.AddInterface("wg0", "10.100.9.9/24", 111)
	d := n.Devices["wg0"]
	d.Fwmark = 7
	n.Devices["wg0"] = d
	l := n.Links["wg0"]
	l.MTU = 1280
	n.Links["wg0"] = l

	s := newStore()
	spec := baseSpec()
	spec.Fwmark = 0x1234
	s.ifaces["wg0"] = spec

	if st := engine(n, s).Interface("wg0"); st.Condition.State != reconcile.Ready {
		t.Fatalf("condition = %+v", st.Condition)
	}

	got := n.Devices["wg0"]
	if got.PrivateKey.Base64() != key(1) {
		t.Error("private_key not enforced")
	}
	if got.ListenPort != 51820 {
		t.Errorf("listen_port = %d, want 51820", got.ListenPort)
	}
	if got.Fwmark != 0x1234 {
		t.Errorf("fwmark = %#x, want 0x1234", got.Fwmark)
	}
	if n.Links["wg0"].MTU != 1420 {
		t.Errorf("mtu = %d, want 1420", n.Links["wg0"].MTU)
	}
	addrs := n.Links["wg0"].Addresses
	if len(addrs) != 1 || addrs[0].String() != "10.100.0.1/24" {
		t.Errorf("addresses = %v, want the spec's alone", addrs)
	}
}

// REQ-RCN-012 and REQ-RCN-051: the endpoint is kernel-owned. A peer whose
// kernel endpoint differs from an empty spec is not drift, and reconcile never
// writes the field on a peer that already exists.
func TestReconcile_NeverOverwritesLearnedEndpoint_REQ_RCN_051(t *testing.T) {
	n := fake.NewNode()
	n.AddInterface("wg0", "10.100.0.1/24", 51820)
	n.AddPeer("wg0", key(9), "10.100.0.2/32", "203.0.113.9:51820", false)
	d := n.Devices["wg0"]
	d.PrivateKey = platform.KeyFromBytes(mustKey(key(1)))
	d.Peers[0].PersistentKeepalive = 0
	n.Devices["wg0"] = d

	s := newStore()
	s.ifaces["wg0"] = baseSpec()
	// The stored peer carries no endpoint, which is what REQ-RCN-063 leaves
	// behind after adoption.
	s.peers["wg0"] = []model.Peer{peer(key(9), "10.100.0.2/32")}

	n.ResetCalls()
	if st := engine(n, s).Interface("wg0"); st.Condition.State != reconcile.Ready {
		t.Fatalf("condition = %+v", st.Condition)
	}

	if got := n.Devices["wg0"].Peers[0].Endpoint; got != "203.0.113.9:51820" {
		t.Errorf("endpoint = %q, want the learned one preserved", got)
	}
	if n.Did("SetEndpoint") {
		t.Errorf("reconcile wrote an endpoint: %v", n.Calls)
	}
	if n.Did("RemovePeer") {
		t.Errorf("reconcile removed a peer it should have kept: %v", n.Calls)
	}
}

// REQ-RCN-013: an endpoint in the spec is applied at peer creation.
func TestReconcile_AppliesEndpointAtCreation_REQ_RCN_013(t *testing.T) {
	n := fake.NewNode()
	s := newStore()
	s.ifaces["wg0"] = baseSpec()
	p := peer(key(9), "10.100.0.2/32")
	p.Spec.Endpoint = "198.51.100.7:51820"
	s.peers["wg0"] = []model.Peer{p}

	if st := engine(n, s).Interface("wg0"); st.Condition.State != reconcile.Ready {
		t.Fatalf("condition = %+v", st.Condition)
	}
	if got := n.Devices["wg0"].Peers[0].Endpoint; got != "198.51.100.7:51820" {
		t.Errorf("endpoint = %q, want the spec's at creation", got)
	}
}

// REQ-RCN-023: reconcile uses deltas, never whole-list replacement. Replacement
// would clear the endpoint of every peer it kept, which is the roaming failure
// REQ-RCN-051 forbids.
func TestReconcile_NoWholeListReplacement_REQ_RCN_023(t *testing.T) {
	n := fake.NewNode()
	n.AddInterface("wg0", "10.100.0.1/24", 51820)
	n.AddPeer("wg0", key(8), "10.100.0.8/32", "", false)
	n.AddPeer("wg0", key(9), "10.100.0.9/32", "", false)

	s := newStore()
	s.ifaces["wg0"] = baseSpec()
	// Drop key(8), keep key(9), add key(7).
	s.peers["wg0"] = []model.Peer{
		peer(key(9), "10.100.0.9/32"),
		peer(key(7), "10.100.0.7/32"),
	}

	n.ResetCalls()
	engine(n, s).Interface("wg0")

	if n.Did("ReplacePeers") {
		t.Errorf("reconcile replaced the peer list: %v", n.Calls)
	}
	if !n.Did("RemovePeer(wg0," + key(8)[:8] + ")") {
		t.Errorf("peer absent from desired state was not removed: %v", n.Calls)
	}
	if !n.Did("AddPeer(wg0," + key(7)[:8] + ")") {
		t.Errorf("peer missing from the kernel was not added: %v", n.Calls)
	}
	if n.Did("RemovePeer(wg0," + key(9)[:8] + ")") {
		t.Errorf("peer present in both was removed: %v", n.Calls)
	}
	if got := len(n.Devices["wg0"].Peers); got != 2 {
		t.Errorf("peer count = %d, want 2", got)
	}
}

// Step 7 precedes step 8. The kernel refuses a route whose output device is
// down, so an order that routed first could never succeed on a fresh link.
func TestReconcile_BringsLinkUpBeforeRouting_REQ_RCN_024(t *testing.T) {
	n := fake.NewNode()
	s := newStore()
	spec := baseSpec()
	spec.ManageRoutes = true
	s.ifaces["wg0"] = spec
	s.peers["wg0"] = []model.Peer{peer(key(9), "10.200.0.0/24")}

	if st := engine(n, s).Interface("wg0"); st.Condition.State != reconcile.Ready {
		t.Fatalf("condition = %+v", st.Condition)
	}

	up := index(n.Calls, "LinkSetUp(wg0)")
	route := index(n.Calls, "RouteAdd(wg0")
	if up < 0 || route < 0 {
		t.Fatalf("missing calls: up=%d route=%d in %v", up, route, n.Calls)
	}
	if up > route {
		t.Errorf("routed before bringing the link up: %v", n.Calls)
	}
	if got := n.RouteTable["wg0"]; len(got) != 1 || got[0].String() != "10.200.0.0/24" {
		t.Errorf("routes = %v, want the union of allowed_ips", got)
	}
}

// REQ-RCN-024: a down link holds no routes, so a pass over one attempts none.
func TestReconcile_SkipsRoutesWhileDown_REQ_RCN_024(t *testing.T) {
	n := fake.NewNode()
	s := newStore()
	spec := baseSpec()
	spec.ManageRoutes = true
	spec.Enabled = false
	s.ifaces["wg0"] = spec
	s.peers["wg0"] = []model.Peer{peer(key(9), "10.200.0.0/24")}

	st := engine(n, s).Interface("wg0")

	if st.Condition.State != reconcile.Ready {
		t.Fatalf("condition = %+v, want READY", st.Condition)
	}
	if n.Did("RouteAdd") {
		t.Errorf("routed a down link: %v", n.Calls)
	}
	if st.OperState != reconcile.OperDown {
		t.Errorf("oper_state = %q, want DOWN", st.OperState)
	}
}

// REQ-RES-019: oper_state comes from the administrative flag, because a
// WireGuard link reports its operational state as unknown even while up.
func TestReconcile_OperStateFromAdminFlag_REQ_RES_019(t *testing.T) {
	n := fake.NewNode()
	n.AddInterface("wg0", "10.100.0.1/24", 51820)
	l := n.Links["wg0"]
	l.AdminUp = false
	n.Links["wg0"] = l

	s := newStore()
	spec := baseSpec()
	spec.Enabled = false
	s.ifaces["wg0"] = spec

	if got := engine(n, s).Interface("wg0").OperState; got != reconcile.OperDown {
		t.Errorf("oper_state = %q, want DOWN", got)
	}

	spec.Enabled = true
	s.ifaces["wg0"] = spec
	if got := engine(n, s).Interface("wg0").OperState; got != reconcile.OperUp {
		t.Errorf("oper_state = %q, want UP", got)
	}
}

// REQ-RCN-036: a pass classifies every WireGuard link desired state omits.
func TestReconcile_ClassifiesLinksOutsideDesiredState_REQ_RCN_036(t *testing.T) {
	n := fake.NewNode()
	n.AddInterface("wg0", "10.100.0.1/24", 51820)
	n.AddInterface("wg1", "10.101.0.1/24", 51821)
	n.AddInterface("wg2", "10.102.0.1/24", 51822)

	s := newStore()
	s.ifaces["wg0"] = baseSpec()
	s.deletions["wg1"] = true

	e := engine(n, s)
	if err := e.Pass(); err != nil {
		t.Fatalf("pass: %v", err)
	}

	want := map[string]string{"wg0": "MANAGED", "wg1": "ORPHANED", "wg2": "FOREIGN"}
	for _, st := range e.Statuses() {
		if want[st.Name] != st.Ownership {
			t.Errorf("%s ownership = %q, want %q", st.Name, st.Ownership, want[st.Name])
		}
	}
	if len(e.Statuses()) != 3 {
		t.Errorf("statuses = %d, want 3", len(e.Statuses()))
	}
}

// REQ-RCN-030 and REQ-RCN-035: neither a foreign link nor an orphan is touched.
func TestReconcile_LeavesForeignAndOrphanedUntouched_REQ_RCN_035(t *testing.T) {
	n := fake.NewNode()
	n.AddInterface("wg1", "10.101.0.1/24", 51821)
	n.AddInterface("wg2", "10.102.0.1/24", 51822)

	s := newStore()
	s.deletions["wg1"] = true

	n.ResetCalls()
	if err := engine(n, s).Pass(); err != nil {
		t.Fatalf("pass: %v", err)
	}
	if len(n.Calls) != 0 {
		t.Errorf("a link outside desired state was written to: %v", n.Calls)
	}
}

// REQ-RCN-037: the deletion record goes once its link is absent.
func TestReconcile_ClearsDeletionRecordOnceLinkAbsent_REQ_RCN_037(t *testing.T) {
	n := fake.NewNode()
	n.AddInterface("wg1", "10.101.0.1/24", 51821)

	s := newStore()
	s.deletions["wg1"] = true
	s.deletions["wg9"] = true // link already gone

	if err := engine(n, s).Pass(); err != nil {
		t.Fatalf("pass: %v", err)
	}
	if len(s.cleared) != 1 || s.cleared[0] != "wg9" {
		t.Errorf("cleared = %v, want [wg9] alone", s.cleared)
	}
	if !s.deletions["wg1"] {
		t.Error("cleared the record of a link that still exists")
	}
}

// REQ-RCN-040: a failed application retains desired state and reports
// DEGRADED with RECONCILE_FAILED.
func TestReconcile_FailureIsDegraded_REQ_RCN_040(t *testing.T) {
	n := fake.NewNode()
	s := newStore()
	spec := baseSpec()
	spec.Addresses = []string{"not-a-prefix"}
	s.ifaces["wg0"] = spec

	e := engine(n, s)
	err := e.Pass()
	if err == nil {
		t.Fatal("pass reported success")
	}

	st, ok := e.Status("wg0")
	if !ok {
		t.Fatal("no status written")
	}
	if st.Condition.State != reconcile.Degraded {
		t.Errorf("condition = %+v, want DEGRADED", st.Condition)
	}
	if _, still, _ := s.Interface("wg0"); !still {
		t.Error("desired state was discarded on failure")
	}
}

// One interface failing does not deny the others a pass.
func TestReconcile_OneFailureDoesNotStopOthers_REQ_RCN_040(t *testing.T) {
	n := fake.NewNode()
	s := newStore()
	bad := baseSpec()
	bad.Addresses = []string{"not-a-prefix"}
	s.ifaces["wg0"] = bad
	s.ifaces["wg1"] = baseSpec()

	e := engine(n, s)
	if err := e.Pass(); err == nil {
		t.Fatal("pass reported success")
	}
	st, ok := e.Status("wg1")
	if !ok || st.Condition.State != reconcile.Ready {
		t.Errorf("wg1 status = %+v, ok = %v; want READY", st.Condition, ok)
	}
}

// REQ-RCN-042: operations on one interface serialise. Run under -race, a
// missing lock corrupts the fake's maps and the detector reports it.
func TestReconcile_SerializesPerInterface_REQ_RCN_042(t *testing.T) {
	n := fake.NewNode()
	s := newStore()
	s.ifaces["wg0"] = baseSpec()
	s.peers["wg0"] = []model.Peer{peer(key(9), "10.100.0.2/32")}
	e := engine(n, s)

	var wg sync.WaitGroup
	for i := 0; i < 16; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			e.Interface("wg0")
		}()
	}
	wg.Wait()

	if got := len(n.Devices["wg0"].Peers); got != 1 {
		t.Errorf("peer count = %d after 16 concurrent passes, want 1", got)
	}
	if st, _ := e.Status("wg0"); st.Condition.State != reconcile.Ready {
		t.Errorf("condition = %+v, want READY", st.Condition)
	}
}

// A store that lost the private key does not cost the kernel its own: clearing
// it would disconnect every established client.
func TestReconcile_MissingPrivateKeyWarnsRatherThanClears_REQ_RES_033(t *testing.T) {
	n := fake.NewNode()
	n.AddInterface("wg0", "10.100.0.1/24", 51820)
	d := n.Devices["wg0"]
	d.PrivateKey = platform.KeyFromBytes(mustKey(key(5)))
	n.Devices["wg0"] = d

	s := newStore()
	spec := baseSpec()
	spec.PrivateKey = ""
	s.ifaces["wg0"] = spec

	st := engine(n, s).Interface("wg0")

	if len(st.Warnings) != 1 {
		t.Fatalf("warnings = %v, want one", st.Warnings)
	}
	if st.Condition.State != reconcile.Ready {
		t.Errorf("condition = %+v; a warning is not a condition", st.Condition)
	}
	if got := n.Devices["wg0"].PrivateKey.Base64(); got != key(5) {
		t.Error("the kernel's private key was cleared")
	}
}

// SPEC-01 defines peer_count as the count in the kernel, so a pass reads it
// back after a write rather than assuming the desired set took effect.
func TestReconcile_PeerCountComesFromTheKernel_REQ_RES_032(t *testing.T) {
	n := fake.NewNode()
	n.AddInterface("wg0", "10.100.0.1/24", 51820)
	d := n.Devices["wg0"]
	d.PrivateKey = platform.KeyFromBytes(mustKey(key(1)))
	n.Devices["wg0"] = d
	n.AddPeer("wg0", key(8), "10.100.0.8/32", "", false)
	n.AddPeer("wg0", key(9), "10.100.0.9/32", "", false)

	s := newStore()
	s.ifaces["wg0"] = baseSpec()
	// Desired state names one of the two, so the other goes.
	s.peers["wg0"] = []model.Peer{peer(key(9), "10.100.0.9/32")}

	st := engine(n, s).Interface("wg0")

	if st.Condition.State != reconcile.Ready {
		t.Fatalf("condition = %+v", st.Condition)
	}
	if st.PeerCount != 1 {
		t.Errorf("peer_count = %d, want 1", st.PeerCount)
	}
	if got := len(n.Devices["wg0"].Peers); got != 1 {
		t.Errorf("kernel holds %d peers, want 1", got)
	}
}

// ── step 9, the forwarding sysctl ───────────────────────────────────────────

// REQ-FWD-020: with intra_interface = ALLOW, forwarding is set on the
// interface. Without it two peers of one interface are not routed to each
// other at all, which is the whole point of the flat-LAN pattern.
func TestReconcile_SetsForwardingWhenIntraAllows_REQ_FWD_020(t *testing.T) {
	n := fake.NewNode()
	s := newStore()
	spec := baseSpec()
	spec.ForwardPolicy = model.DefaultForwardPolicy() // intra ALLOW, inter DENY
	s.ifaces["wg0"] = spec

	e := &reconcile.Engine{Store: s, Link: fake.LinkView{N: n}, Device: n, HostFS: n}
	if st := e.Interface("wg0"); st.Condition.State != reconcile.Ready {
		t.Fatalf("condition = %+v", st.Condition)
	}
	if got := n.Forwarding["wg0"]; got != "1" {
		t.Errorf("forwarding = %q, want 1", got)
	}
}

// The condition of REQ-FWD-020, in each direction.
func TestReconcile_ForwardingFollowsThePolicy_REQ_FWD_020(t *testing.T) {
	cases := []struct {
		name        string
		intra, inte model.Axis
		want        string
	}{
		{"intra allows", model.Allow, model.Deny, "1"},
		{"inter allows", model.Deny, model.Allow, "1"},
		{"inter allow-list", model.Deny, model.AllowList, "1"},
		{"both deny", model.Deny, model.Deny, ""},
		// An unset policy is an invalid spec. Forwarding is not opened for one:
		// `inter != DENY` read literally would, which is the reading the engine
		// deliberately does not take.
		{"unset", "", "", ""},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			n := fake.NewNode()
			s := newStore()
			spec := baseSpec()
			spec.ForwardPolicy = model.ForwardPolicySpec{
				IntraInterface: c.intra, InterInterface: c.inte, External: model.Deny,
			}
			s.ifaces["wg0"] = spec

			e := &reconcile.Engine{Store: s, Link: fake.LinkView{N: n}, Device: n, HostFS: n}
			if st := e.Interface("wg0"); st.Condition.State != reconcile.Ready {
				t.Fatalf("condition = %+v", st.Condition)
			}
			if got := n.Forwarding["wg0"]; got != c.want {
				t.Errorf("forwarding = %q, want %q", got, c.want)
			}
		})
	}
}

// REQ-FWD-022: an interface desired state does not describe keeps its value.
func TestReconcile_LeavesForeignForwardingAlone_REQ_FWD_022(t *testing.T) {
	n := fake.NewNode()
	n.AddInterface("wg1", "10.101.0.1/24", 51821).WithForwarding("wg1", "0")
	s := newStore()

	e := &reconcile.Engine{Store: s, Link: fake.LinkView{N: n}, Device: n, HostFS: n}
	if err := e.Pass(); err != nil {
		t.Fatalf("pass: %v", err)
	}
	if got := n.Forwarding["wg1"]; got != "0" {
		t.Errorf("forwarding of a foreign interface = %q, want it untouched at 0", got)
	}
}

// A sysctl that cannot be written is a reconcile failure carrying
// SYSCTL_WRITE_DENIED. REQ-FWD-025 would surface it at startup instead and is
// deferred, so this is where it appears.
func TestReconcile_ReadOnlySysctlIsDegraded_REQ_FWD_020(t *testing.T) {
	n := fake.NewNode().WithReadOnlySysctl()
	s := newStore()
	spec := baseSpec()
	spec.ForwardPolicy = model.DefaultForwardPolicy()
	s.ifaces["wg0"] = spec

	e := &reconcile.Engine{Store: s, Link: fake.LinkView{N: n}, Device: n, HostFS: n}
	st := e.Interface("wg0")

	if st.Condition.State != reconcile.Degraded {
		t.Fatalf("condition = %+v, want DEGRADED", st.Condition)
	}
	if st.Condition.Reason != reconcile.ReasonSysctlWriteDenied {
		t.Errorf("reason = %q, want SYSCTL_WRITE_DENIED", st.Condition.Reason)
	}
	// Everything before step 9 still happened: the interface is usable as a
	// tunnel even though forwarding between its peers is not configured.
	if !n.Links["wg0"].AdminUp {
		t.Error("the link was not brought up")
	}
}

// A second pass writes nothing when the value already matches, which is what
// keeps a read-only /proc/sys from failing an otherwise converged interface.
func TestReconcile_ForwardingWriteIsSkippedWhenItMatches_REQ_RES_003(t *testing.T) {
	n := fake.NewNode()
	s := newStore()
	spec := baseSpec()
	spec.ForwardPolicy = model.DefaultForwardPolicy()
	s.ifaces["wg0"] = spec

	e := &reconcile.Engine{Store: s, Link: fake.LinkView{N: n}, Device: n, HostFS: n}
	e.Interface("wg0")
	n.ResetCalls()

	if st := e.Interface("wg0"); st.Condition.State != reconcile.Ready {
		t.Fatalf("second pass: %+v", st.Condition)
	}
	if n.Did("SetForwarding") {
		t.Errorf("second pass rewrote the sysctl: %v", n.Calls)
	}
}

// REQ-RES-017 decides ownership from desired state and the deletion record
// alone, never from creation history.
func TestOwnership_DecidedFromStoreAlone_REQ_RES_017(t *testing.T) {
	cases := []struct {
		describes, deleted bool
		want               model.Ownership
	}{
		{true, false, model.Managed},
		{true, true, model.Managed},
		{false, true, model.Orphaned},
		{false, false, model.Foreign},
	}
	for _, c := range cases {
		if got := model.OwnershipOf(c.describes, c.deleted); got != c.want {
			t.Errorf("OwnershipOf(%v,%v) = %q, want %q", c.describes, c.deleted, got, c.want)
		}
	}
}

func mustKey(b64 string) []byte {
	b, err := base64.StdEncoding.DecodeString(b64)
	if err != nil {
		panic(err)
	}
	return b
}
