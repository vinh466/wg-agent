package reconcile

import (
	"fmt"
	"sort"
	"sync"
	"time"

	"wg-agent/internal/model"
	"wg-agent/internal/platform"
)

// Store is the desired-state access a pass needs. *store.Store satisfies it.
//
// The engine reads through this rather than re-reading the file, because the
// process running a pass is the one holding the lock of REQ-RCN-006.
type Store interface {
	Names() []string
	Interface(name string) (model.InterfaceSpec, bool, error)
	Peers(name string) ([]model.Peer, error)
	Describes(name string) bool
	DeletionRecord(name string) bool
	DeletionNames() []string
	ClearDeletion(name string) error
}

// Engine applies desired state to the kernel.
type Engine struct {
	Store  Store
	Link   platform.Link
	Device platform.Device

	// Now is injectable so a test can assert a timestamp. A nil value means
	// time.Now.
	Now func() time.Time

	mu     sync.Mutex
	locks  map[string]*sync.Mutex
	status map[string]Status
}

func (e *Engine) now() time.Time {
	if e.Now != nil {
		return e.Now()
	}
	return time.Now()
}

// lockFor returns the per-interface lock of REQ-RCN-042, creating it on first
// use. Every operation on one interface serialises behind it, so an API write
// and a periodic pass cannot interleave their reads and writes.
func (e *Engine) lockFor(name string) *sync.Mutex {
	e.mu.Lock()
	defer e.mu.Unlock()
	if e.locks == nil {
		e.locks = map[string]*sync.Mutex{}
	}
	l, ok := e.locks[name]
	if !ok {
		l = &sync.Mutex{}
		e.locks[name] = l
	}
	return l
}

func (e *Engine) putStatus(s Status) {
	e.mu.Lock()
	defer e.mu.Unlock()
	if e.status == nil {
		e.status = map[string]Status{}
	}
	e.status[s.Name] = s
}

// Status returns the last status written for one interface.
func (e *Engine) Status(name string) (Status, bool) {
	e.mu.Lock()
	defer e.mu.Unlock()
	s, ok := e.status[name]
	return s, ok
}

// Statuses returns every status written, sorted by name.
func (e *Engine) Statuses() []Status {
	e.mu.Lock()
	defer e.mu.Unlock()
	out := make([]Status, 0, len(e.status))
	for _, s := range e.status {
		out = append(out, s)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Name < out[j].Name })
	return out
}

// Pass reconciles every interface desired state describes, then classifies the
// WireGuard links it does not — REQ-RCN-022 and REQ-RCN-036.
//
// A failure on one interface does not stop the others: REQ-RCN-040 makes a
// failure a DEGRADED condition on that resource, and an interface unrelated to
// it is still owed a pass. The error returned reports how many failed, so a
// caller can drive the backoff of REQ-RCN-041.
func (e *Engine) Pass() error {
	names := e.Store.Names()
	failed := 0
	for _, n := range names {
		if st := e.Interface(n); st.Condition.State == Degraded {
			failed++
		}
	}
	if err := e.classify(); err != nil {
		return err
	}
	if failed > 0 {
		return fmt.Errorf("%d of %d interfaces did not reconcile", failed, len(names))
	}
	return nil
}

// Interface reconciles one interface and returns the status it wrote. It takes
// the per-interface lock of REQ-RCN-042, so it is the entry point for an API
// write as well as for a pass.
func (e *Engine) Interface(name string) Status {
	l := e.lockFor(name)
	l.Lock()
	defer l.Unlock()

	st := e.apply(name)
	e.putStatus(st)
	return st
}

// apply is the algorithm of REQ-RCN-022. Steps 9 and 10 are deferred under
// B-04; every other step is here in the order the requirement fixes.
func (e *Engine) apply(name string) Status {
	st := Status{
		Name:      name,
		Ownership: string(model.Managed),
		OperState: OperAbsent,
		UpdatedAt: e.now(),
	}

	spec, ok, err := e.Store.Interface(name)
	if err != nil {
		st.degraded(ReasonStoreCorrupt, err.Error())
		return st
	}
	if !ok {
		st.Ownership = string(model.OwnershipOf(false, e.Store.DeletionRecord(name)))
		st.degraded(ReasonReconcileFailed, "desired state does not describe "+name)
		return st
	}
	peers, err := e.Store.Peers(name)
	if err != nil {
		st.degraded(ReasonStoreCorrupt, err.Error())
		return st
	}

	// Step 1 — link present?
	present, err := e.linkPresent(name)
	if err != nil {
		st.degraded(ReasonReconcileFailed, err.Error())
		return st
	}
	if !present {
		if err := e.Link.Add(name); err != nil {
			st.degraded(ReasonReconcileFailed, err.Error())
			return st
		}
	}

	ls, err := e.Link.State(name)
	if err != nil {
		st.degraded(ReasonReconcileFailed, err.Error())
		return st
	}
	st.OperState = operState(ls)

	// Step 2 — link of type wireguard?
	if ls.Type != "wireguard" {
		st.degraded(ReasonReconcileFailed,
			fmt.Sprintf("%s is a %q link; the resource model describes wireguard links only",
				name, ls.Type))
		return st
	}

	ds, err := e.Device.Snapshot(name)
	if err != nil {
		st.degraded(ReasonReconcileFailed, err.Error())
		return st
	}
	st.PeerCount = len(ds.Peers)

	if spec.PrivateKey == "" {
		// Leaving the kernel's key alone is the conservative reading: clearing
		// it would break every established client, and no requirement asks for
		// that. REQ-RCN-061 and the create path both store one, so an absent
		// value is a store that lost it.
		st.warn(ReasonStoreCorrupt,
			"desired state holds no private key for "+name+"; the kernel's is left in place")
	}

	// Steps 3 and 4 — device config and peer set, in one Configure call.
	// The kernel applies a device delta and its peer deltas together, so one
	// call is both fewer syscalls and one less window for a partial write.
	cfg, deviceChanged, err := deviceDelta(spec, ds)
	if err != nil {
		st.degraded(ReasonStoreCorrupt, err.Error())
		return st
	}
	pcs, err := peerDelta(peers, ds)
	if err != nil {
		st.degraded(ReasonStoreCorrupt, err.Error())
		return st
	}
	cfg.Peers = pcs
	if deviceChanged || len(pcs) > 0 {
		if err := e.Device.Configure(name, cfg); err != nil {
			st.degraded(ReasonReconcileFailed, err.Error())
			return st
		}
		// SPEC-01 defines peer_count as the count in the kernel, so it is read
		// back rather than assumed from the desired set: a peer the kernel
		// declined to create would otherwise be counted as present.
		if after, err := e.Device.Snapshot(name); err == nil {
			st.PeerCount = len(after.Peers)
		}
	}

	// Step 5 — addresses.
	add, del, err := addrDelta(spec, ls)
	if err != nil {
		st.degraded(ReasonStoreCorrupt, err.Error())
		return st
	}
	for _, p := range add {
		if err := e.Link.AddrAdd(name, p); err != nil {
			st.degraded(ReasonReconcileFailed, err.Error())
			return st
		}
	}
	for _, p := range del {
		if err := e.Link.AddrDel(name, p); err != nil {
			st.degraded(ReasonReconcileFailed, err.Error())
			return st
		}
	}

	// Step 6 — MTU. A zero spec value is an unset one, which leaves the
	// kernel's default in place rather than driving the MTU to zero.
	if spec.MTU != 0 && spec.MTU != ls.MTU {
		if err := e.Link.SetMTU(name, spec.MTU); err != nil {
			st.degraded(ReasonReconcileFailed, err.Error())
			return st
		}
	}

	// Step 7 — enabled. This precedes routing because the kernel refuses a
	// route whose output device is down.
	if spec.Enabled != ls.AdminUp {
		var err error
		if spec.Enabled {
			err = e.Link.SetUp(name)
		} else {
			err = e.Link.SetDown(name)
		}
		if err != nil {
			st.degraded(ReasonReconcileFailed, err.Error())
			return st
		}
		st.OperState = OperDown
		if spec.Enabled {
			st.OperState = OperUp
		}
	}

	// Step 8 — routes. REQ-RCN-024 skips this while the link is down, because
	// the kernel withdrew whatever was there and refuses what would be added.
	if spec.ManageRoutes && spec.Enabled {
		if err := e.syncRoutes(name, peers); err != nil {
			st.degraded(ReasonReconcileFailed, err.Error())
			return st
		}
	}

	// Steps 9 and 10 — the forwarding sysctl and nftables — are deferred under
	// B-04 in docs/60-planning/backlog.md.

	// Step 11 — status.
	st.ready()
	return st
}

func (e *Engine) syncRoutes(name string, peers []model.Peer) error {
	have, err := e.Link.Routes(name)
	if err != nil {
		return err
	}
	add, del, err := routeDelta(peers, have)
	if err != nil {
		return err
	}
	for _, p := range add {
		if err := e.Link.RouteAdd(name, p); err != nil {
			return err
		}
	}
	for _, p := range del {
		if err := e.Link.RouteDel(name, p); err != nil {
			return err
		}
	}
	return nil
}

// classify covers REQ-RCN-036: every WireGuard link absent from desired state
// is FOREIGN or ORPHANED. Neither is touched — REQ-RCN-030 leaves a foreign
// link alone and REQ-RCN-035 forbids acting on an orphan.
//
// It also clears the deletion records of REQ-RCN-037, whose link is gone.
func (e *Engine) classify() error {
	devices, err := e.Device.Names()
	if err != nil {
		return fmt.Errorf("enumerate wireguard devices: %w", err)
	}
	sort.Strings(devices)

	live := make(map[string]struct{}, len(devices))
	for _, n := range devices {
		live[n] = struct{}{}
		if e.Store.Describes(n) {
			continue
		}
		own := model.OwnershipOf(false, e.Store.DeletionRecord(n))
		st := Status{
			Name:      n,
			Ownership: string(own),
			OperState: OperAbsent,
			UpdatedAt: e.now(),
		}
		if ls, err := e.Link.State(n); err == nil {
			st.OperState = operState(ls)
		}
		if ds, err := e.Device.Snapshot(n); err == nil {
			st.PeerCount = len(ds.Peers)
		}
		st.Condition = Condition{State: Ready}
		e.putStatus(st)
	}

	// REQ-RCN-037 — a deletion record whose link is absent has served its
	// purpose. Leaving it would keep reporting a link nobody can see.
	for _, n := range e.Store.DeletionNames() {
		if _, still := live[n]; still {
			continue
		}
		if err := e.Store.ClearDeletion(n); err != nil {
			return fmt.Errorf("clear deletion record for %q: %w", n, err)
		}
		e.mu.Lock()
		delete(e.status, n)
		e.mu.Unlock()
	}
	return nil
}

func (e *Engine) linkPresent(name string) (bool, error) {
	names, err := e.Link.Names()
	if err != nil {
		return false, fmt.Errorf("enumerate links: %w", err)
	}
	for _, n := range names {
		if n == name {
			return true, nil
		}
	}
	return false, nil
}

// operState implements REQ-RES-019: the administrative flag, not the
// operational state, because a WireGuard link has no carrier to report on.
func operState(ls platform.LinkState) string {
	if ls.AdminUp {
		return OperUp
	}
	return OperDown
}
