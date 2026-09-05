package store

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"syscall"

	"wg-agent/internal/model"
)

// ErrLocked reports that another process holds the store lock — REQ-RCN-007.
// For a command reaching the store directly that means the agent is serving.
var ErrLocked = errors.New("another process holds the store lock")

// Store is an exclusive handle on the desired-state file.
//
// Open acquires the lock of REQ-RCN-006 and Close releases it. Every write goes
// through Update, which is the transaction REQ-RCN-003 requires: the whole file
// is rendered to a temporary path and renamed over the original, so a reader
// sees either the previous state or the next one.
type Store struct {
	path string
	lock *os.File
	f    file
}

// lockPath is a sibling of the store rather than the store itself. A lock held
// on the store file would be lost the moment Update renamed a new file over it,
// because the lock belongs to the inode and not to the name.
func lockPath(path string) string {
	return filepath.Join(filepath.Dir(path), "."+filepath.Base(path)+".lock")
}

// Open takes the exclusive lock and reads the current state. An absent store
// file is an empty state, which is what lets a command act on a node before the
// agent has ever run.
func Open(path string) (*Store, error) {
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return nil, fmt.Errorf("create store directory: %w", err)
	}

	lf, err := os.OpenFile(lockPath(path), os.O_CREATE|os.O_RDWR, 0o600)
	if err != nil {
		return nil, fmt.Errorf("open store lock: %w", err)
	}
	if err := syscall.Flock(int(lf.Fd()), syscall.LOCK_EX|syscall.LOCK_NB); err != nil {
		_ = lf.Close()
		if errors.Is(err, syscall.EWOULDBLOCK) {
			return nil, ErrLocked
		}
		return nil, fmt.Errorf("lock store: %w", err)
	}

	snap, err := Read(path)
	if err != nil {
		_ = syscall.Flock(int(lf.Fd()), syscall.LOCK_UN)
		_ = lf.Close()
		return nil, err
	}
	return &Store{path: path, lock: lf, f: snap.f}, nil
}

// Close releases the lock. The kernel would release it when the process exits
// in any case, which is what makes a crash safe.
func (s *Store) Close() error {
	if s.lock == nil {
		return nil
	}
	err := syscall.Flock(int(s.lock.Fd()), syscall.LOCK_UN)
	cerr := s.lock.Close()
	s.lock = nil
	if err != nil {
		return err
	}
	return cerr
}

// Snapshot returns a read view of the state currently held.
func (s *Store) Snapshot() *Snapshot { return &Snapshot{f: s.f} }

// Txn is the mutable view handed to Update. Nothing reaches disk until Update
// returns without error.
type Txn struct{ f *file }

// Update applies fn and writes the result atomically. When fn returns an error
// nothing is written, which is what makes an adoption that fails part-way leave
// the store unchanged — REQ-RCN-064.
func (s *Store) Update(fn func(*Txn) error) error {
	if s.lock == nil {
		return errors.New("store is closed")
	}

	next := s.f.clone()
	next.Schema = SchemaVersion
	if err := fn(&Txn{f: &next}); err != nil {
		return err
	}
	if err := writeAtomic(s.path, &next); err != nil {
		return err
	}
	s.f = next
	return nil
}

// writeAtomic renders the file to a temporary path in the same directory and
// renames it over the target. Same directory matters: rename is only atomic
// within a filesystem.
func writeAtomic(path string, f *file) error {
	b, err := json.MarshalIndent(f, "", "  ")
	if err != nil {
		return fmt.Errorf("encode store: %w", err)
	}
	b = append(b, '\n')

	tmp, err := os.CreateTemp(filepath.Dir(path), "."+filepath.Base(path)+".*")
	if err != nil {
		return fmt.Errorf("create temporary store: %w", err)
	}
	name := tmp.Name()
	defer func() { _ = os.Remove(name) }()

	// REQ-RCN-004: mode 0600. CreateTemp already makes it 0600, and Chmod is
	// stated anyway so the guarantee does not rest on that.
	if err := tmp.Chmod(0o600); err != nil {
		_ = tmp.Close()
		return fmt.Errorf("set store mode: %w", err)
	}
	if _, err := tmp.Write(b); err != nil {
		_ = tmp.Close()
		return fmt.Errorf("write store: %w", err)
	}
	if err := tmp.Sync(); err != nil {
		_ = tmp.Close()
		return fmt.Errorf("sync store: %w", err)
	}
	if err := tmp.Close(); err != nil {
		return fmt.Errorf("close store: %w", err)
	}
	if err := os.Rename(name, path); err != nil {
		return fmt.Errorf("replace store: %w", err)
	}

	// Fsync the directory so the rename itself survives a power loss.
	d, err := os.Open(filepath.Dir(path))
	if err == nil {
		_ = d.Sync()
		_ = d.Close()
	}
	return nil
}

// ── Txn mutations ───────────────────────────────────────────────────────────

// Describes reports whether desired state already describes the interface.
func (t *Txn) Describes(name string) bool {
	_, ok := t.f.Interfaces[name]
	return ok
}

// AdoptionRecord reports whether an adoption record names the interface, which
// REQ-RCN-072 tests before permitting a release.
func (t *Txn) AdoptionRecord(name string) bool {
	_, ok := t.f.Adoptions[name]
	return ok
}

// DeletionRecord reports whether a deletion record names the interface.
func (t *Txn) DeletionRecord(name string) bool {
	_, ok := t.f.Deletions[name]
	return ok
}

// ForwardingBaseline returns the sysctl value recorded at adoption, which
// REQ-FWD-024 restores on release.
func (t *Txn) ForwardingBaseline(name string) (string, bool) {
	a, ok := t.f.Adoptions[name]
	if !ok {
		return "", false
	}
	return a.ForwardingBaseline, true
}

// PutInterface writes an interface spec together with the identity fields
// REQ-RCN-002 permits the store to hold.
func (t *Txn) PutInterface(name string, spec model.InterfaceSpec, instanceID, createdAt string) error {
	b, err := json.Marshal(spec)
	if err != nil {
		return fmt.Errorf("encode interface spec: %w", err)
	}
	if t.f.Interfaces == nil {
		t.f.Interfaces = map[string]iface{}
	}
	t.f.Interfaces[name] = iface{
		InstanceID: instanceID,
		CreatedAt:  createdAt,
		Spec:       b,
	}
	return nil
}

// PutPeers replaces the peer set of one interface.
func (t *Txn) PutPeers(name string, peers []model.Peer) error {
	if t.f.Peers == nil {
		t.f.Peers = map[string]json.RawMessage{}
	}
	if len(peers) == 0 {
		delete(t.f.Peers, name)
		return nil
	}
	b, err := json.Marshal(peers)
	if err != nil {
		return fmt.Errorf("encode peer specs: %w", err)
	}
	t.f.Peers[name] = b
	return nil
}

// Peers returns the stored peers of one interface.
func (t *Txn) Peers(name string) ([]model.Peer, error) {
	raw, ok := t.f.Peers[name]
	if !ok {
		return nil, nil
	}
	var out []model.Peer
	if err := json.Unmarshal(raw, &out); err != nil {
		return nil, fmt.Errorf("decode peer specs: %w", err)
	}
	return out, nil
}

// PutAdoption records that the interface was adopted, carrying the forwarding
// sysctl value REQ-FWD-024 restores — REQ-RCN-070.
func (t *Txn) PutAdoption(name, forwardingBaseline, at string) {
	if t.f.Adoptions == nil {
		t.f.Adoptions = map[string]adoption{}
	}
	t.f.Adoptions[name] = adoption{AdoptedAt: at, ForwardingBaseline: forwardingBaseline}
}

// RemoveInterface removes an interface's spec, its peers and its adoption
// record. REQ-RCN-071 requires the record to be cleared in the same transaction
// that removes the spec, and REQ-RCN-038 requires the same of the peers, so one
// method covers release and DeleteInterface alike.
func (t *Txn) RemoveInterface(name string) {
	delete(t.f.Interfaces, name)
	delete(t.f.Peers, name)
	delete(t.f.Adoptions, name)
}

// clone deep-copies the file so a failed transaction leaves the in-memory state
// untouched as well as the file.
func (f file) clone() file {
	out := file{Schema: f.Schema}
	if f.Interfaces != nil {
		out.Interfaces = make(map[string]iface, len(f.Interfaces))
		for k, v := range f.Interfaces {
			out.Interfaces[k] = v
		}
	}
	if f.Deletions != nil {
		out.Deletions = make(map[string]deletion, len(f.Deletions))
		for k, v := range f.Deletions {
			out.Deletions[k] = v
		}
	}
	if f.Adoptions != nil {
		out.Adoptions = make(map[string]adoption, len(f.Adoptions))
		for k, v := range f.Adoptions {
			out.Adoptions[k] = v
		}
	}
	if f.Peers != nil {
		out.Peers = make(map[string]json.RawMessage, len(f.Peers))
		for k, v := range f.Peers {
			out.Peers[k] = v
		}
	}
	return out
}
