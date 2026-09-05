package store_test

import (
	"errors"
	"os"
	"path/filepath"
	"testing"

	"wg-agent/internal/model"
	"wg-agent/internal/store"
)

func openAt(t *testing.T, path string) *store.Store {
	t.Helper()
	st, err := store.Open(path)
	if err != nil {
		t.Fatalf("Open: %v", err)
	}
	t.Cleanup(func() { _ = st.Close() })
	return st
}

// spec builds an interface spec. It carries no name: REQ-RES-034 keeps
// identity outside the spec, so the store takes it as a separate argument.
func spec() model.InterfaceSpec {
	return model.InterfaceSpec{
		ListenPort: 51820, Addresses: []string{"10.0.0.1/24"},
		MTU: 1420, ForwardPolicy: model.DefaultForwardPolicy(),
	}
}

func TestStore_SecondOpenIsRefusedWhileTheFirstHolds_REQ_RCN_007(t *testing.T) {
	path := filepath.Join(t.TempDir(), "state.db")
	first := openAt(t, path)

	// This is what stops a command writing behind a running agent: the agent
	// holds the lock of REQ-RCN-006 for as long as it serves.
	_, err := store.Open(path)
	if !errors.Is(err, store.ErrLocked) {
		t.Fatalf("want ErrLocked while another holder has the store, got %v", err)
	}

	if err := first.Close(); err != nil {
		t.Fatalf("Close: %v", err)
	}
	second, err := store.Open(path)
	if err != nil {
		t.Fatalf("the lock must be released on Close, got %v", err)
	}
	_ = second.Close()
}

func TestStore_WriteIsAtomicAndPrivate_REQ_RCN_004(t *testing.T) {
	path := filepath.Join(t.TempDir(), "state.db")
	st := openAt(t, path)

	if err := st.Update(func(tx *store.Txn) error {
		return tx.PutInterface("wg0", spec(), "instance-1", "2026-09-06T00:00:00Z")
	}); err != nil {
		t.Fatalf("Update: %v", err)
	}

	info, err := os.Stat(path)
	if err != nil {
		t.Fatal(err)
	}
	if perm := info.Mode().Perm(); perm != 0o600 {
		t.Errorf("store mode: want 0600, got %o", perm)
	}

	// No temporary file may be left behind, or a later reader could find two
	// candidates for the same state.
	entries, err := os.ReadDir(filepath.Dir(path))
	if err != nil {
		t.Fatal(err)
	}
	for _, e := range entries {
		switch e.Name() {
		case "state.db", ".state.db.lock":
		default:
			t.Errorf("unexpected leftover file %q", e.Name())
		}
	}
}

func TestStore_FailedTransactionWritesNothing_REQ_RCN_003(t *testing.T) {
	path := filepath.Join(t.TempDir(), "state.db")
	st := openAt(t, path)

	sentinel := errors.New("no")
	err := st.Update(func(tx *store.Txn) error {
		if err := tx.PutInterface("wg0", spec(), "instance-1", "now"); err != nil {
			return err
		}
		return sentinel
	})
	if !errors.Is(err, sentinel) {
		t.Fatalf("want the callback's error, got %v", err)
	}

	if st.Snapshot().Describes("wg0") {
		t.Error("a failed transaction must not change the in-memory state either")
	}
	if _, err := os.Stat(path); !errors.Is(err, os.ErrNotExist) {
		t.Error("a failed transaction must not create the store")
	}
}

func TestStore_RemoveInterfaceClearsPeersAndAdoption_REQ_RCN_071(t *testing.T) {
	path := filepath.Join(t.TempDir(), "state.db")
	st := openAt(t, path)

	if err := st.Update(func(tx *store.Txn) error {
		if err := tx.PutInterface("wg0", spec(), "instance-1", "now"); err != nil {
			return err
		}
		if err := tx.PutPeers("wg0", []model.Peer{{
			InterfaceName: "wg0", PublicKey: "peer-a",
			Spec: model.PeerSpec{AllowedIPs: []string{"10.0.0.2/32"}},
		}}); err != nil {
			return err
		}
		tx.PutAdoption("wg0", "0", "now")
		return nil
	}); err != nil {
		t.Fatalf("Update: %v", err)
	}

	if err := st.Update(func(tx *store.Txn) error {
		tx.RemoveInterface("wg0")
		return nil
	}); err != nil {
		t.Fatalf("Update: %v", err)
	}

	snap := st.Snapshot()
	if snap.Describes("wg0") || snap.AdoptionRecord("wg0") {
		t.Error("removing an interface must clear its spec and adoption record together")
	}
	// A second read from disk proves the peers went with them.
	reread, err := store.Read(path)
	if err != nil {
		t.Fatal(err)
	}
	if reread.Describes("wg0") {
		t.Error("the removal must be on disk")
	}
}

func TestStore_PeersRoundTrip_REQ_RCN_002(t *testing.T) {
	path := filepath.Join(t.TempDir(), "state.db")
	st := openAt(t, path)

	want := []model.Peer{
		{InterfaceName: "wg0", PublicKey: "peer-a", Spec: model.PeerSpec{
			AllowedIPs: []string{"10.0.0.2/32"}, PresharedKey: "psk", PersistentKeepalive: 25}},
		{InterfaceName: "wg0", PublicKey: "peer-b", Spec: model.PeerSpec{
			AllowedIPs: []string{"10.0.0.3/32"}}},
	}
	if err := st.Update(func(tx *store.Txn) error { return tx.PutPeers("wg0", want) }); err != nil {
		t.Fatalf("Update: %v", err)
	}

	var got []model.Peer
	if err := st.Update(func(tx *store.Txn) error {
		var err error
		got, err = tx.Peers("wg0")
		return err
	}); err != nil {
		t.Fatalf("Update: %v", err)
	}
	if len(got) != 2 || got[0].PublicKey != "peer-a" || got[0].Spec.PresharedKey != "psk" {
		t.Errorf("peers did not round-trip: %+v", got)
	}
	if got[1].Spec.PersistentKeepalive != 0 {
		t.Errorf("keepalive default: want 0, got %d", got[1].Spec.PersistentKeepalive)
	}
}
