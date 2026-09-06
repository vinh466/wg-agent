// Package store persists desired state.
//
// This file carries the read side only, which is what doctor needs under
// REQ-CLI-004: it reads the store directly rather than through the API, so the
// command works on a node where the agent has never started.
//
// The format is a single JSON file written atomically. bbolt is named in
// SPEC-03 without an RFC 2119 keyword, so it is explanatory rather than
// required, and REQ-RCN-001 to REQ-RCN-005 are satisfied by a file written to a
// temporary path and renamed at mode 0600.
package store

import (
	"encoding/json"
	"errors"
	"fmt"
	"io/fs"
	"os"
	"sort"

	"wg-agent/internal/model"
)

// SchemaVersion is the version this build understands. REQ-RCN-005 requires the
// agent to refuse a newer one rather than misinterpret the data.
const SchemaVersion = 1

// DefaultPath matches store.path in the SPEC-09 configuration sample.
const DefaultPath = "/var/lib/wg-agent/state.db"

// file is the on-disk shape. Its contents are bounded by REQ-RCN-002: spec
// values, instance_id, created_at, deletion records and adoption records.
// REQ-RCN-050 keeps status and traffic counters out.
type file struct {
	Schema     int                        `json:"schema"`
	Interfaces map[string]iface           `json:"interfaces,omitempty"`
	Deletions  map[string]deletion        `json:"deletions,omitempty"`
	Adoptions  map[string]adoption        `json:"adoptions,omitempty"`
	Peers      map[string]json.RawMessage `json:"peers,omitempty"`
}

type iface struct {
	InstanceID string `json:"instance_id"`
	CreatedAt  string `json:"created_at"`
	// Spec is held opaque here. The read side that doctor needs asks only
	// whether desired state describes an interface, which is membership of this
	// map — REQ-RES-017's definition of MANAGED.
	Spec json.RawMessage `json:"spec,omitempty"`
}

type deletion struct {
	RecordedAt string `json:"recorded_at"`
}

// adoption is the record REQ-RCN-070 requires. It names the interface, by being
// keyed on it, and holds the forwarding sysctl value REQ-FWD-024 restores.
type adoption struct {
	AdoptedAt          string `json:"adopted_at"`
	ForwardingBaseline string `json:"forwarding_baseline"`
}

// Snapshot is an immutable read of desired state. It implements
// platform.DesiredState.
type Snapshot struct {
	f file
}

// ErrSchemaTooNew reports a store written by a newer build — REQ-RCN-005.
var ErrSchemaTooNew = errors.New("store schema is newer than this build understands")

// Read loads a snapshot from path.
//
// An absent file yields an empty snapshot and no error. That is the ordinary
// case for doctor: before the first write there is no desired state, so every
// WireGuard link on the host is FOREIGN, which is exactly the set the readiness
// report of REQ-DIA-040 covers.
func Read(path string) (*Snapshot, error) {
	b, err := os.ReadFile(path)
	if err != nil {
		if errors.Is(err, fs.ErrNotExist) {
			return &Snapshot{f: file{Schema: SchemaVersion}}, nil
		}
		return nil, fmt.Errorf("read store %q: %w", path, err)
	}

	var f file
	if err := json.Unmarshal(b, &f); err != nil {
		return nil, fmt.Errorf("parse store %q: %w", path, err)
	}
	if f.Schema > SchemaVersion {
		return nil, fmt.Errorf("%w: file is %d, this build understands %d",
			ErrSchemaTooNew, f.Schema, SchemaVersion)
	}
	return &Snapshot{f: f}, nil
}

// Describes reports whether desired state describes the interface, which is
// what REQ-RES-017 calls MANAGED.
func (s *Snapshot) Describes(name string) bool {
	_, ok := s.f.Interfaces[name]
	return ok
}

// DeletionRecord reports whether a deletion record names the interface, which
// is what REQ-RCN-034 calls ORPHANED.
func (s *Snapshot) DeletionRecord(name string) bool {
	_, ok := s.f.Deletions[name]
	return ok
}

// AdoptionRecord reports whether an adoption record names the interface, which
// is what REQ-RCN-072 tests before permitting a release.
func (s *Snapshot) AdoptionRecord(name string) bool {
	_, ok := s.f.Adoptions[name]
	return ok
}

// Names lists the interfaces desired state describes, sorted. It is the set
// REQ-RCN-022 iterates.
func (s *Snapshot) Names() []string {
	out := make([]string, 0, len(s.f.Interfaces))
	for k := range s.f.Interfaces {
		out = append(out, k)
	}
	sort.Strings(out)
	return out
}

// Interface returns one stored spec. The second result reports whether desired
// state describes the interface at all, which REQ-RES-017 calls MANAGED.
func (s *Snapshot) Interface(name string) (model.InterfaceSpec, bool, error) {
	i, ok := s.f.Interfaces[name]
	if !ok {
		return model.InterfaceSpec{}, false, nil
	}
	var spec model.InterfaceSpec
	if len(i.Spec) > 0 {
		if err := json.Unmarshal(i.Spec, &spec); err != nil {
			return model.InterfaceSpec{}, true, fmt.Errorf("decode spec of %q: %w", name, err)
		}
	}
	return spec, true, nil
}

// Identity returns the instance_id and created_at REQ-RES-018 assigns. They sit
// beside the spec rather than inside it, per REQ-RES-034.
func (s *Snapshot) Identity(name string) (instanceID, createdAt string, ok bool) {
	i, found := s.f.Interfaces[name]
	if !found {
		return "", "", false
	}
	return i.InstanceID, i.CreatedAt, true
}

// Peers returns the stored peers of one interface.
func (s *Snapshot) Peers(name string) ([]model.Peer, error) {
	raw, ok := s.f.Peers[name]
	if !ok {
		return nil, nil
	}
	var out []model.Peer
	if err := json.Unmarshal(raw, &out); err != nil {
		return nil, fmt.Errorf("decode peer specs of %q: %w", name, err)
	}
	return out, nil
}

// DeletionNames lists the interfaces a deletion record names, sorted.
// REQ-RCN-037 requires each to be cleared once its link is absent.
func (s *Snapshot) DeletionNames() []string {
	out := make([]string, 0, len(s.f.Deletions))
	for k := range s.f.Deletions {
		out = append(out, k)
	}
	sort.Strings(out)
	return out
}
