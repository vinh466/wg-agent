package service_test

import (
	"errors"
	"path/filepath"
	"strings"
	"testing"

	"wg-agent/internal/model"
	"wg-agent/internal/platform"
	"wg-agent/internal/platform/fake"
	"wg-agent/internal/service"
	"wg-agent/internal/store"
)

// openStore returns an exclusively held store under a temporary directory.
//
// The real store is used rather than a fake: it is a local file, so the test
// stays privilege-free and fast, and faking it would leave the atomic write and
// the lock untested.
func openStore(t *testing.T) (*store.Store, string) {
	t.Helper()
	path := filepath.Join(t.TempDir(), "state.db")
	st, err := store.Open(path)
	if err != nil {
		t.Fatalf("Open: %v", err)
	}
	t.Cleanup(func() { _ = st.Close() })
	return st, path
}

func adopter(n *fake.Node) service.Adopt {
	return service.Adopt{
		Device: n,
		Link:   fake.LinkView{N: n},
		HostFS: n,
		Now:    func() string { return "2026-09-06T00:00:00Z" },
		NewID:  func() string { return "instance-1" },
	}
}

// policy is a complete request: REQ-RCN-066 rejects one that omits a field.
func policy(name string) service.AdoptRequest {
	mr := false
	return service.AdoptRequest{
		Name: name,
		ForwardPolicy: &model.ForwardPolicySpec{
			IntraInterface: model.Allow,
			InterInterface: model.Deny,
			External:       model.Deny,
		},
		NAT:          &model.NatSpec{},
		ManageRoutes: &mr,
	}
}

func reasonOf(t *testing.T, err error) string {
	t.Helper()
	var e *service.Error
	if !errors.As(err, &e) {
		t.Fatalf("want a reason-carrying error, got %v", err)
	}
	return e.Reason
}

func TestAdopt_StoresKernelStateAndKeepsTheKey_REQ_RCN_061(t *testing.T) {
	n := fake.NewNode().AddInterface("wg0", "10.0.0.1/24", 51820)
	st, _ := openStore(t)

	res, err := adopter(n).Do(st, policy("wg0"), false)
	if err != nil {
		t.Fatalf("Do: %v", err)
	}

	// The interface key survives adoption, which is what leaves every existing
	// client configuration valid.
	want := n.Devices["wg0"].PrivateKey.Base64()
	if res.Name != "wg0" {
		t.Errorf("the result must name the interface it adopted, got %q", res.Name)
	}
	if res.Spec.PrivateKey != want || want == "" {
		t.Errorf("private key: want the kernel's, got %q", res.Spec.PrivateKey)
	}
	if res.Spec.ListenPort != 51820 || res.Spec.MTU != 1420 || !res.Spec.Enabled {
		t.Errorf("spec did not come from the kernel: %+v", res.Spec)
	}
	if len(res.Spec.Addresses) != 1 || res.Spec.Addresses[0] != "10.0.0.1/24" {
		t.Errorf("addresses: got %v", res.Spec.Addresses)
	}
	if st.Snapshot().Describes("wg0") != true {
		t.Error("the interface must be in desired state after adoption")
	}
}

func TestAdopt_StoresEveryPeerWithoutEndpoint_REQ_RCN_062(t *testing.T) {
	n := fake.NewNode().AddInterface("wg0", "10.0.0.1/24", 51820)
	n.AddPeer("wg0", "peer-a", "10.0.0.2/32", "", true)
	n.AddPeer("wg0", "peer-b", "10.0.0.3/32", "203.0.113.7:51820", false)
	st, _ := openStore(t)

	res, err := adopter(n).Do(st, policy("wg0"), false)
	if err != nil {
		t.Fatalf("Do: %v", err)
	}
	if len(res.Peers) != 2 {
		t.Fatalf("want both peers stored, got %d", len(res.Peers))
	}
	for _, p := range res.Peers {
		// REQ-RCN-063 — the kernel does not distinguish a learned endpoint from
		// a configured one, so adoption stores neither.
		if p.Spec.Endpoint != "" {
			t.Errorf("%s: endpoint must not be stored, got %q", p.PublicKey, p.Spec.Endpoint)
		}
		if p.InterfaceName != "wg0" {
			t.Errorf("%s: interface_name not set", p.PublicKey)
		}
	}
	if res.Peers[0].Spec.PresharedKey == "" {
		t.Error("the preshared key must be carried into desired state")
	}
}

func TestAdopt_RejectsRequestWithoutPolicy_REQ_RCN_066(t *testing.T) {
	n := fake.NewNode().AddInterface("wg0", "10.0.0.1/24", 51820)
	st, _ := openStore(t)

	for label, mutate := range map[string]func(*service.AdoptRequest){
		"forward policy": func(r *service.AdoptRequest) { r.ForwardPolicy = nil },
		"nat":            func(r *service.AdoptRequest) { r.NAT = nil },
		"manage routes":  func(r *service.AdoptRequest) { r.ManageRoutes = nil },
	} {
		req := policy("wg0")
		mutate(&req)
		_, err := adopter(n).Do(st, req, false)
		if err == nil {
			t.Fatalf("%s omitted: want a rejection", label)
		}
		if got := reasonOf(t, err); got != service.ReasonAdoptionFieldRequired {
			t.Errorf("%s omitted: want ADOPTION_FIELD_REQUIRED, got %s", label, got)
		}
	}
	if st.Snapshot().Describes("wg0") {
		t.Error("a rejected request must leave the store unchanged")
	}
}

func TestAdopt_RejectsMissingLink_REQ_RCN_067(t *testing.T) {
	n := fake.NewNode()
	st, _ := openStore(t)

	_, err := adopter(n).Do(st, policy("wg9"), false)
	if got := reasonOf(t, err); got != service.ReasonInterfaceNotFound {
		t.Errorf("want INTERFACE_NOT_FOUND, got %s", got)
	}
}

func TestAdopt_RejectsInterfaceAlreadyManaged_REQ_RCN_074(t *testing.T) {
	n := fake.NewNode().AddInterface("wg0", "10.0.0.1/24", 51820)
	st, _ := openStore(t)

	if _, err := adopter(n).Do(st, policy("wg0"), false); err != nil {
		t.Fatalf("first adoption: %v", err)
	}
	// A second adoption would otherwise replace the stored spec, discarding
	// whatever policy the first one recorded.
	_, err := adopter(n).Do(st, policy("wg0"), false)
	if got := reasonOf(t, err); got != service.ReasonInterfaceNotForeign {
		t.Errorf("want INTERFACE_NOT_FOREIGN, got %s", got)
	}
}

func TestAdopt_BlockedByFailFindingLeavesStoreUnchanged_REQ_RCN_064(t *testing.T) {
	n := fake.NewNode().AddInterface("wg0", "10.0.0.1/24", 51820).
		WithUnit("wg0", platform.UnitEnabled)
	st, _ := openStore(t)

	_, err := adopter(n).Do(st, policy("wg0"), false)
	if got := reasonOf(t, err); got != service.ReasonAdoptionBlocked {
		t.Fatalf("want ADOPTION_BLOCKED, got %s", got)
	}
	// REQ-API-067 — the findings travel with the refusal, so a caller learns
	// what blocked it without a second call.
	var e *service.Error
	_ = errors.As(err, &e)
	if len(e.Findings) == 0 {
		t.Error("the refusal must carry the findings")
	}
	if st.Snapshot().Describes("wg0") {
		t.Error("a blocked adoption must leave the store unchanged")
	}
}

func TestAdopt_DryRunWritesNothing_REQ_CLI_006(t *testing.T) {
	n := fake.NewNode().AddInterface("wg0", "10.0.0.1/24", 51820)
	st, _ := openStore(t)

	res, err := adopter(n).Do(st, policy("wg0"), true)
	if err != nil {
		t.Fatalf("Do: %v", err)
	}
	if !res.DryRun {
		t.Error("the result must report that nothing was written")
	}
	if res.Name != "wg0" || res.Spec.ListenPort != 51820 {
		t.Error("a preview must still return the spec that would be stored")
	}
	if st.Snapshot().Describes("wg0") {
		t.Error("a preview must not write")
	}
}

func TestAdopt_RecordsForwardingBaseline_REQ_RCN_070(t *testing.T) {
	n := fake.NewNode().AddInterface("wg0", "10.0.0.1/24", 51820).
		WithForwarding("wg0", "0")
	st, path := openStore(t)

	if _, err := adopter(n).Do(st, policy("wg0"), false); err != nil {
		t.Fatalf("Do: %v", err)
	}
	if !st.Snapshot().AdoptionRecord("wg0") {
		t.Fatal("adoption must retain an adoption record")
	}

	// The record has to survive a restart: it is what REQ-RCN-072 tests before
	// permitting a release, and what REQ-FWD-024 restores from.
	reread, err := store.Read(path)
	if err != nil {
		t.Fatal(err)
	}
	if !reread.AdoptionRecord("wg0") {
		t.Error("the adoption record must be on disk, not only in memory")
	}
}

func TestAdopt_RejectsExternalAllowWithoutUplink_REQ_VAL_021(t *testing.T) {
	n := fake.NewNode().AddInterface("wg0", "10.0.0.1/24", 51820)
	st, _ := openStore(t)

	req := policy("wg0")
	req.ForwardPolicy.External = model.Allow
	_, err := adopter(n).Do(st, req, false)
	if got := reasonOf(t, err); got != service.ReasonForwardPolicyUplink {
		t.Errorf("want FORWARD_POLICY_NEEDS_UPLINK, got %s", got)
	}
}

func TestAdopt_RejectsAllowListOnTheWrongAxis_REQ_FWD_002(t *testing.T) {
	n := fake.NewNode().AddInterface("wg0", "10.0.0.1/24", 51820)
	st, _ := openStore(t)

	req := policy("wg0")
	req.ForwardPolicy.IntraInterface = model.AllowList
	if _, err := adopter(n).Do(st, req, false); err == nil {
		t.Error("ALLOW_LIST applies to inter_interface alone")
	}
}

func TestRelease_ReturnsInterfaceToForeign_REQ_RCN_069(t *testing.T) {
	n := fake.NewNode().AddInterface("wg0", "10.0.0.1/24", 51820).
		WithForwarding("wg0", "0")
	n.AddPeer("wg0", "peer-a", "10.0.0.2/32", "", false)
	st, _ := openStore(t)

	if _, err := adopter(n).Do(st, policy("wg0"), false); err != nil {
		t.Fatalf("adopt: %v", err)
	}

	var restored []string
	res, err := service.Release{
		RestoreForwarding: func(iface, v string) error {
			restored = append(restored, iface+"="+v)
			return nil
		},
	}.Do(st, "wg0")
	if err != nil {
		t.Fatalf("release: %v", err)
	}

	snap := st.Snapshot()
	if snap.Describes("wg0") {
		t.Error("release must remove the interface from desired state")
	}
	if snap.DeletionRecord("wg0") {
		t.Error("release writes no deletion record, so the interface is FOREIGN not ORPHANED")
	}
	if snap.AdoptionRecord("wg0") {
		t.Error("REQ-RCN-071 clears the adoption record in the same transaction")
	}
	if res.PeersRemoved != 1 {
		t.Errorf("peers removed: want 1, got %d", res.PeersRemoved)
	}
	// REQ-FWD-024 restores the value REQ-RCN-070 recorded.
	if len(restored) != 1 || restored[0] != "wg0=0" {
		t.Errorf("forwarding restore: got %v", restored)
	}
}

func TestRelease_RefusesAnInterfaceWithNoAdoptionRecord_REQ_RCN_072(t *testing.T) {
	st, _ := openStore(t)

	_, err := service.Release{}.Do(st, "wg0")
	if got := reasonOf(t, err); got != service.ReasonInterfaceNotAdopted {
		t.Errorf("want INTERFACE_NOT_ADOPTED, got %s", got)
	}
}

func TestRelease_FailedRestoreLeavesTheInterfaceManaged_REQ_RCN_073(t *testing.T) {
	n := fake.NewNode().AddInterface("wg0", "10.0.0.1/24", 51820).
		WithForwarding("wg0", "0")
	st, _ := openStore(t)
	if _, err := adopter(n).Do(st, policy("wg0"), false); err != nil {
		t.Fatalf("adopt: %v", err)
	}

	_, err := service.Release{
		RestoreForwarding: func(string, string) error { return errors.New("read-only") },
	}.Do(st, "wg0")
	if err == nil {
		t.Fatal("a failed restore must fail the release")
	}
	if !strings.Contains(err.Error(), "restore forwarding") {
		t.Errorf("error should name the step that failed: %v", err)
	}
	// The restore happens before the transaction commits, so nothing is
	// half-released.
	if !st.Snapshot().Describes("wg0") {
		t.Error("the interface must still be managed after a failed release")
	}
	if !st.Snapshot().AdoptionRecord("wg0") {
		t.Error("the adoption record must survive a failed release")
	}
}

func TestAdopt_AdoptAfterReleaseIsPermitted_REQ_RES_017(t *testing.T) {
	n := fake.NewNode().AddInterface("wg0", "10.0.0.1/24", 51820)
	st, _ := openStore(t)

	if _, err := adopter(n).Do(st, policy("wg0"), false); err != nil {
		t.Fatalf("adopt: %v", err)
	}
	if _, err := (service.Release{}).Do(st, "wg0"); err != nil {
		t.Fatalf("release: %v", err)
	}
	// Ownership is decided from the store, so a released interface is FOREIGN
	// again and may be adopted a second time.
	if _, err := adopter(n).Do(st, policy("wg0"), false); err != nil {
		t.Fatalf("re-adopt: %v", err)
	}
}
