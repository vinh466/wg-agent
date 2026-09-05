package store_test

import (
	"errors"
	"os"
	"path/filepath"
	"testing"

	"wg-agent/internal/store"
)

func TestStore_AbsentFileIsEmptyDesiredState_REQ_CLI_004(t *testing.T) {
	// doctor runs on a node where the agent has never started, so an absent
	// store is the ordinary case and must not be an error. Every WireGuard link
	// is then absent from desired state, which is what makes it FOREIGN.
	s, err := store.Read(filepath.Join(t.TempDir(), "state.db"))
	if err != nil {
		t.Fatalf("an absent store must read as empty, got %v", err)
	}
	if s.Describes("wg0") || s.DeletionRecord("wg0") || s.AdoptionRecord("wg0") {
		t.Error("an empty store must describe nothing")
	}
}

func TestStore_RefusesNewerSchema_REQ_RCN_005(t *testing.T) {
	path := filepath.Join(t.TempDir(), "state.db")
	if err := os.WriteFile(path, []byte(`{"schema":9999}`), 0o600); err != nil {
		t.Fatal(err)
	}
	_, err := store.Read(path)
	if !errors.Is(err, store.ErrSchemaTooNew) {
		t.Fatalf("want ErrSchemaTooNew rather than a misread, got %v", err)
	}
}

func TestStore_OwnershipFactsReadBack_REQ_RES_017(t *testing.T) {
	path := filepath.Join(t.TempDir(), "state.db")
	body := `{
	  "schema": 1,
	  "interfaces": {"wg0": {"instance_id": "a", "created_at": "2026-09-05T00:00:00Z"}},
	  "deletions":  {"wg1": {"recorded_at": "2026-09-05T00:00:00Z"}},
	  "adoptions":  {"wg0": {"adopted_at": "2026-09-05T00:00:00Z", "forwarding_baseline": "0"}}
	}`
	if err := os.WriteFile(path, []byte(body), 0o600); err != nil {
		t.Fatal(err)
	}
	s, err := store.Read(path)
	if err != nil {
		t.Fatal(err)
	}

	// The two facts REQ-RES-017 decides ownership from, and the adoption record
	// REQ-RCN-072 tests before permitting a release.
	if !s.Describes("wg0") {
		t.Error("wg0 is in desired state, so it is MANAGED")
	}
	if !s.DeletionRecord("wg1") {
		t.Error("wg1 holds a deletion record, so it is ORPHANED")
	}
	if !s.AdoptionRecord("wg0") {
		t.Error("wg0 holds an adoption record, so it may be released")
	}
	if s.Describes("wg2") || s.DeletionRecord("wg2") {
		t.Error("an unmentioned link is FOREIGN")
	}
}

func TestStore_MalformedFileIsAnError_REQ_RCN_005(t *testing.T) {
	path := filepath.Join(t.TempDir(), "state.db")
	if err := os.WriteFile(path, []byte("not json"), 0o600); err != nil {
		t.Fatal(err)
	}
	if _, err := store.Read(path); err == nil {
		t.Fatal("a malformed store must be an error rather than an empty read")
	}
}
