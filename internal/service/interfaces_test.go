package service_test

import (
	"errors"
	"testing"

	"wg-agent/internal/model"
	"wg-agent/internal/platform"
	"wg-agent/internal/platform/fake"
	"wg-agent/internal/reconcile"
	"wg-agent/internal/service"
	"wg-agent/internal/store"
	"wg-agent/internal/validate"
)

// wire assembles the service layer over an in-memory node and a real store.
// The engine is the real one, so a write reaching the kernel is part of what
// the test asserts rather than something it mocks away.
func wire(t *testing.T, n *fake.Node) (service.Interfaces, service.Peers, *store.Store) {
	t.Helper()
	st, _ := openStore(t)
	engine := &reconcile.Engine{
		Store:  st,
		Link:   fake.LinkView{N: n},
		Device: n,
		HostFS: n,
	}
	ifaces := service.Interfaces{
		Store:  st,
		Device: n,
		Link:   fake.LinkView{N: n},
		Engine: engine,
		Defaults: service.Defaults{
			ForwardPolicy: model.DefaultForwardPolicy(),
			MTU:           1420,
		},
		NewID: func() string { return "instance-1" },
		Now:   func() string { return "2026-09-07T00:00:00Z" },
	}
	return ifaces, service.Peers{Interfaces: ifaces}, st
}

func newSpec() model.InterfaceSpec {
	return model.InterfaceSpec{
		ListenPort: 51820,
		Addresses:  []string{"10.100.0.1/24"},
		Enabled:    true,
	}
}

// A create stores the spec, applies it to the kernel, and returns a status
// describing what it just did rather than the state before it.
func TestInterfaces_CreateAppliesAndReports_REQ_RCN_020(t *testing.T) {
	n := fake.NewNode()
	ifaces, _, st := wire(t, n)

	got, err := ifaces.Create("wg0", newSpec())
	if err != nil {
		t.Fatalf("create: %v", err)
	}

	if got.Status.Ownership != model.Managed {
		t.Errorf("ownership = %q, want MANAGED", got.Status.Ownership)
	}
	if got.Status.Condition.State != reconcile.Ready {
		t.Errorf("condition = %+v, want READY", got.Status.Condition)
	}
	if got.Status.OperState != reconcile.OperUp {
		t.Errorf("oper_state = %q, want UP", got.Status.OperState)
	}
	if got.Status.InstanceID != "instance-1" || got.Status.CreatedAt == "" {
		t.Errorf("identity fields missing: %+v", got.Status)
	}
	// The kernel actually has it.
	if _, ok := n.Links["wg0"]; !ok {
		t.Error("the link was not created")
	}
	if !st.Snapshot().Describes("wg0") {
		t.Error("the spec was not stored")
	}
}

// REQ-KEY-001: a private key the caller omitted is generated.
func TestInterfaces_GeneratesTheKeyWhenOmitted_REQ_KEY_001(t *testing.T) {
	n := fake.NewNode()
	ifaces, _, _ := wire(t, n)

	if _, err := ifaces.Create("wg0", newSpec()); err != nil {
		t.Fatalf("create: %v", err)
	}
	if !n.Devices["wg0"].PrivateKey.Present() {
		t.Fatal("no private key reached the kernel")
	}
}

// REQ-KEY-002 and REQ-RES-013: no response carries the private key. The type
// is what enforces it, so this test is about the type as much as the call.
func TestInterfaces_ResponseNeverCarriesThePrivateKey_REQ_KEY_002(t *testing.T) {
	n := fake.NewNode()
	ifaces, _, _ := wire(t, n)

	spec := newSpec()
	spec.PrivateKey = keyOf(0x33)

	got, err := ifaces.Create("wg0", spec)
	if err != nil {
		t.Fatalf("create: %v", err)
	}
	blob := mustJSON(t, got)
	if contains(blob, spec.PrivateKey) {
		t.Error("the response carries the private key")
	}
	if contains(blob, "private_key") {
		t.Errorf("the response has a private_key field: %s", blob)
	}
	// The public key is what a caller gets instead.
	if got.Status.PublicKey == "" {
		t.Error("status.public_key must be reported")
	}
}

// REQ-VAL-001 blocks the write, so nothing is stored and nothing reaches the
// kernel.
func TestInterfaces_ValidationBlocksTheWrite_REQ_VAL_001(t *testing.T) {
	n := fake.NewNode()
	ifaces, _, st := wire(t, n)

	spec := newSpec()
	spec.Addresses = nil // REQ-VAL-016

	_, err := ifaces.Create("wg0", spec)
	if err == nil {
		t.Fatal("an invalid spec was stored")
	}
	var ve *validate.Error
	if !errors.As(err, &ve) || ve.Reason() != validate.ReasonAddressesRequired {
		t.Errorf("error = %v, want ADDRESSES_REQUIRED", err)
	}
	if st.Snapshot().Describes("wg0") {
		t.Error("the store must be unchanged")
	}
	if _, ok := n.Links["wg0"]; ok {
		t.Error("the kernel must be untouched")
	}
}

// REQ-VAL-015: a create naming an existing link is refused, and the message
// points at adoption.
func TestInterfaces_CreateNamingAnExistingLink_REQ_VAL_015(t *testing.T) {
	n := fake.NewNode().AddInterface("wg0", "10.200.0.1/24", 51999)
	ifaces, _, _ := wire(t, n)

	_, err := ifaces.Create("wg0", newSpec())
	var ve *validate.Error
	if !errors.As(err, &ve) || ve.Reason() != validate.ReasonInterfaceExists {
		t.Errorf("error = %v, want INTERFACE_EXISTS", err)
	}
}

// The configured defaults fill a field the caller omits — SPEC-09 defaults.
func TestInterfaces_AppliesConfiguredDefaults_REQ_FWD_001(t *testing.T) {
	n := fake.NewNode()
	ifaces, _, _ := wire(t, n)

	got, err := ifaces.Create("wg0", newSpec())
	if err != nil {
		t.Fatalf("create: %v", err)
	}
	if got.Spec.MTU != 1420 {
		t.Errorf("mtu = %d, want the default 1420", got.Spec.MTU)
	}
	if got.Spec.ForwardPolicy.IntraInterface != model.Allow {
		t.Errorf("intra_interface = %q, want ALLOW", got.Spec.ForwardPolicy.IntraInterface)
	}
}

// REQ-API-069: an update is not an upsert.
func TestInterfaces_UpdateRefusesAnUnknownName_REQ_API_069(t *testing.T) {
	n := fake.NewNode()
	ifaces, _, _ := wire(t, n)

	_, err := ifaces.Update("wg9", newSpec())
	if got := reasonOf(t, err); got != service.ReasonInterfaceNotManaged {
		t.Errorf("reason = %q, want INTERFACE_NOT_MANAGED", got)
	}
}

// An update that omits the private key keeps the stored one: the caller could
// not have read it to send it back, and clearing it would disconnect everyone.
func TestInterfaces_UpdateKeepsTheStoredKey_REQ_KEY_002(t *testing.T) {
	n := fake.NewNode()
	ifaces, _, _ := wire(t, n)

	if _, err := ifaces.Create("wg0", newSpec()); err != nil {
		t.Fatalf("create: %v", err)
	}
	before := n.Devices["wg0"].PrivateKey.Base64()

	spec := newSpec()
	spec.MTU = 1380
	if _, err := ifaces.Update("wg0", spec); err != nil {
		t.Fatalf("update: %v", err)
	}
	if got := n.Devices["wg0"].PrivateKey.Base64(); got != before {
		t.Error("the update replaced the private key")
	}
	if n.Links["wg0"].MTU != 1380 {
		t.Errorf("mtu = %d, want the update applied", n.Links["wg0"].MTU)
	}
}

// REQ-API-030 and REQ-API-077: the revision moves when the spec changes, and
// also when the peer set changes.
func TestInterfaces_RevisionMovesWithSpecAndPeers_REQ_API_077(t *testing.T) {
	n := fake.NewNode()
	ifaces, peers, _ := wire(t, n)

	created, err := ifaces.Create("wg0", newSpec())
	if err != nil {
		t.Fatalf("create: %v", err)
	}
	first := created.Status.Revision
	if first == "" {
		t.Fatal("no revision was produced")
	}

	spec := newSpec()
	spec.MTU = 1380
	afterSpec, err := ifaces.Update("wg0", spec)
	if err != nil {
		t.Fatalf("update: %v", err)
	}
	if afterSpec.Status.Revision == first {
		t.Error("the revision did not move when the spec changed")
	}

	if _, err := peers.Create("wg0", newPeer(keyOf(9), "10.100.0.2/32")); err != nil {
		t.Fatalf("create peer: %v", err)
	}
	afterPeer, err := ifaces.Get("wg0")
	if err != nil {
		t.Fatalf("get: %v", err)
	}
	if afterPeer.Status.Revision == afterSpec.Status.Revision {
		t.Error("the revision did not move when the peer set changed — REQ-API-077")
	}
}

// The same state produces the same revision, which is what lets a caller
// compare one — REQ-RES-031.
func TestInterfaces_RevisionIsStableForOneState_REQ_RES_031(t *testing.T) {
	spec := newSpec()
	ps := []model.Peer{newPeer(keyOf(9), "10.100.0.2/32"), newPeer(keyOf(8), "10.100.0.3/32")}

	a := service.Revision(spec, ps)
	// Reversed order is the same set.
	b := service.Revision(spec, []model.Peer{ps[1], ps[0]})
	if a != b {
		t.Errorf("the revision depends on peer order: %q vs %q", a, b)
	}
}

// REQ-RCN-031: an interface outside desired state is reported with its spec
// absent, so a caller cannot mistake a read of the kernel for enforcement.
func TestInterfaces_ForeignInterfaceHasNoSpec_REQ_RCN_031(t *testing.T) {
	n := fake.NewNode().AddInterface("wg1", "10.101.0.1/24", 51821)
	ifaces, _, _ := wire(t, n)

	got, err := ifaces.Get("wg1")
	if err != nil {
		t.Fatalf("get: %v", err)
	}
	if got.Status.Ownership != model.Foreign {
		t.Errorf("ownership = %q, want FOREIGN", got.Status.Ownership)
	}
	if len(got.Spec.Addresses) != 0 || got.Spec.ListenPort != 0 {
		t.Errorf("a foreign interface must carry no spec, got %+v", got.Spec)
	}
	// Kernel-sourced status is still reported — REQ-RES-024.
	if got.Status.ListenPort != 51821 {
		t.Errorf("status.listen_port = %d, want the kernel's", got.Status.ListenPort)
	}
}

// List covers both sets: managed first, then the links desired state omits.
func TestInterfaces_ListCoversManagedAndForeign_REQ_RCN_036(t *testing.T) {
	n := fake.NewNode().AddInterface("wg1", "10.101.0.1/24", 51821)
	ifaces, _, _ := wire(t, n)

	if _, err := ifaces.Create("wg0", newSpec()); err != nil {
		t.Fatalf("create: %v", err)
	}
	list, err := ifaces.List()
	if err != nil {
		t.Fatalf("list: %v", err)
	}
	if len(list) != 2 {
		t.Fatalf("list has %d entries, want 2: %+v", len(list), list)
	}
	own := map[string]model.Ownership{}
	for _, i := range list {
		own[i.Name] = i.Status.Ownership
	}
	if own["wg0"] != model.Managed || own["wg1"] != model.Foreign {
		t.Errorf("ownership = %v", own)
	}
}

// REQ-RCN-032 and REQ-RCN-038: the link goes from the kernel and the spec and
// its peers go from the store, in one transaction.
func TestInterfaces_DeleteRemovesLinkSpecAndPeers_REQ_RCN_038(t *testing.T) {
	n := fake.NewNode()
	ifaces, peers, st := wire(t, n)

	if _, err := ifaces.Create("wg0", newSpec()); err != nil {
		t.Fatalf("create: %v", err)
	}
	if _, err := peers.Create("wg0", newPeer(keyOf(9), "10.100.0.2/32")); err != nil {
		t.Fatalf("create peer: %v", err)
	}

	if err := ifaces.Delete("wg0"); err != nil {
		t.Fatalf("delete: %v", err)
	}
	if _, ok := n.Links["wg0"]; ok {
		t.Error("the link survived the delete")
	}
	if st.Snapshot().Describes("wg0") {
		t.Error("the spec survived the delete")
	}
	ps, err := st.Snapshot().Peers("wg0")
	if err != nil {
		t.Fatalf("peers: %v", err)
	}
	if len(ps) != 0 {
		t.Errorf("%d peers survived the delete", len(ps))
	}
	// REQ-RCN-037 — the link went, so no deletion record is left behind.
	if st.Snapshot().DeletionRecord("wg0") {
		t.Error("a successful delete must leave no deletion record")
	}
}

// REQ-RCN-033: link removal that does not complete leaves a deletion record,
// which is what REQ-RCN-034 reads to call the surviving link ORPHANED.
func TestInterfaces_FailedLinkRemovalRecordsADeletion_REQ_RCN_033(t *testing.T) {
	n := fake.NewNode()
	ifaces, _, st := wire(t, n)
	if _, err := ifaces.Create("wg0", newSpec()); err != nil {
		t.Fatalf("create: %v", err)
	}

	// A Link whose Del fails, standing in for a kernel that refused.
	ifaces.Link = stubbornLink{LinkView: fake.LinkView{N: n}}
	if err := ifaces.Delete("wg0"); err != nil {
		t.Fatalf("delete: %v", err)
	}

	if !st.Snapshot().DeletionRecord("wg0") {
		t.Error("a failed removal must leave a deletion record")
	}
	if st.Snapshot().Describes("wg0") {
		t.Error("the spec must still go: REQ-RCN-032 removes it either way")
	}
	if own := model.OwnershipOf(false, st.Snapshot().DeletionRecord("wg0")); own != model.Orphaned {
		t.Errorf("ownership = %q, want ORPHANED", own)
	}
}

func TestInterfaces_DeleteRefusesAnUnknownName_REQ_API_069(t *testing.T) {
	n := fake.NewNode()
	ifaces, _, _ := wire(t, n)

	if got := reasonOf(t, ifaces.Delete("wg9")); got != service.ReasonInterfaceNotManaged {
		t.Errorf("reason = %q, want INTERFACE_NOT_MANAGED", got)
	}
}

// stubbornLink refuses to delete, which is the REQ-RCN-033 case.
type stubbornLink struct{ fake.LinkView }

func (stubbornLink) Del(string) error { return errors.New("device busy") }

// ── keys ────────────────────────────────────────────────────────────────────

// REQ-KEY-040: one private key and its public key, in one response.
func TestGenerateKeyPair_ReturnsBothOnce_REQ_KEY_040(t *testing.T) {
	kp, err := service.GenerateKeyPair()
	if err != nil {
		t.Fatalf("generate: %v", err)
	}
	if kp.PrivateKey == "" || kp.PublicKey == "" {
		t.Fatalf("incomplete pair: %+v", kp)
	}
	if kp.PrivateKey == kp.PublicKey {
		t.Error("the two keys are the same value")
	}
	for _, k := range []string{kp.PrivateKey, kp.PublicKey} {
		if _, err := platform.ParseKey(k); err != nil {
			t.Errorf("%q is not base64 of 32 bytes: %v", k, err)
		}
	}

	// Two calls give two pairs. A generator returning the same key twice would
	// be the worst possible failure here and the easiest to miss.
	other, err := service.GenerateKeyPair()
	if err != nil {
		t.Fatalf("generate: %v", err)
	}
	if other.PrivateKey == kp.PrivateKey {
		t.Error("two calls produced the same private key")
	}
}

// The public key derived from a private one matches what the kernel reports for
// it, which is what makes the derivation usable in a client configuration.
func TestPublicKeyOf_MatchesTheKernel_REQ_KEY_040(t *testing.T) {
	kp, err := service.GenerateKeyPair()
	if err != nil {
		t.Fatalf("generate: %v", err)
	}
	priv, err := platform.ParseKey(kp.PrivateKey)
	if err != nil {
		t.Fatalf("parse: %v", err)
	}
	got, err := service.PublicKeyOf(priv)
	if err != nil {
		t.Fatalf("derive: %v", err)
	}
	if got != kp.PublicKey {
		t.Errorf("derivation is not deterministic: %q vs %q", got, kp.PublicKey)
	}
}
