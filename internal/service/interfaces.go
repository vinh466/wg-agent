package service

import (
	"fmt"

	"wg-agent/internal/model"
	"wg-agent/internal/platform"
	"wg-agent/internal/reconcile"
	"wg-agent/internal/store"
	"wg-agent/internal/validate"
)

// Reconciler is the part of the reconcile engine the service layer drives.
// REQ-RCN-020 lists an API write among the triggers, so every write here ends
// in one — a write that returned before the kernel was touched would report a
// status describing the state before its own change.
type Reconciler interface {
	Interface(name string) reconcile.Status
	Status(name string) (reconcile.Status, bool)
	Statuses() []reconcile.Status
}

// Interfaces implements the interface half of the contract.
//
// Every write follows the same order: validate, store in one transaction,
// reconcile, then read the status back. Validating before the store keeps
// REQ-VAL-001 from being bypassed, and reconciling before returning is what
// makes the status in the response describe the change the caller just made.
type Interfaces struct {
	Store  *store.Store
	Device platform.Device
	Link   platform.Link
	Engine Reconciler

	// Defaults are applied to a field the caller omits. Adoption does not use
	// them: REQ-RCN-066 requires the adoption request to supply its own.
	Defaults Defaults

	// NewKey generates an interface private key — REQ-KEY-001. Injected so a
	// test can assert which key was stored without reading /dev/urandom.
	NewKey func() (platform.Key, error)
	// NewID and Now supply the identity fields of REQ-RES-018.
	NewID func() string
	Now   func() string
}

// Defaults carries the configured defaults of SPEC-09 for a created interface.
type Defaults struct {
	ForwardPolicy model.ForwardPolicySpec
	MTU           int
}

// Interface is one interface as a response carries it. The private key is
// absent: REQ-KEY-002 and REQ-RES-013 keep it out of every response, and the
// type is what enforces that rather than a rule each call site must remember.
type Interface struct {
	Name   string            `json:"name"`
	Spec   InterfaceSpecView `json:"spec"`
	Status InterfaceStatus   `json:"status"`
}

// InterfaceSpecView is InterfaceSpec without the private key.
type InterfaceSpecView struct {
	ListenPort    int                     `json:"listen_port"`
	Addresses     []string                `json:"addresses"`
	MTU           int                     `json:"mtu"`
	Fwmark        uint32                  `json:"fwmark"`
	ManageRoutes  bool                    `json:"manage_routes"`
	ForwardPolicy model.ForwardPolicySpec `json:"forward_policy"`
	NAT           model.NatSpec           `json:"nat"`
	Enabled       bool                    `json:"enabled"`
	Labels        map[string]string       `json:"labels,omitempty"`
}

// InterfaceStatus is SPEC-01 section 3.3.
type InterfaceStatus struct {
	PublicKey  string              `json:"public_key"`
	ListenPort int                 `json:"listen_port"`
	InstanceID string              `json:"instance_id"`
	CreatedAt  string              `json:"created_at"`
	Revision   string              `json:"revision"`
	OperState  string              `json:"oper_state"`
	Ownership  model.Ownership     `json:"ownership"`
	PeerCount  int                 `json:"peer_count"`
	Condition  reconcile.Condition `json:"condition"`
	Warnings   []validate.Finding  `json:"warnings,omitempty"`
}

func viewOf(spec model.InterfaceSpec) InterfaceSpecView {
	return InterfaceSpecView{
		ListenPort:    spec.ListenPort,
		Addresses:     spec.Addresses,
		MTU:           spec.MTU,
		Fwmark:        spec.Fwmark,
		ManageRoutes:  spec.ManageRoutes,
		ForwardPolicy: spec.ForwardPolicy,
		NAT:           spec.NAT,
		Enabled:       spec.Enabled,
		Labels:        spec.Labels,
	}
}

// Create stores a new interface and applies it.
func (s Interfaces) Create(name string, spec model.InterfaceSpec) (*Interface, error) {
	spec = s.applyDefaults(spec)

	// REQ-KEY-001 — a private key the caller omitted is generated from a
	// cryptographically secure source rather than left empty, which reconcile
	// would then warn about on every pass.
	if spec.PrivateKey == "" {
		k, err := s.newKey()
		if err != nil {
			return nil, fmt.Errorf("generate interface key: %w", err)
		}
		spec.PrivateKey = k.Base64()
	}

	if err := s.validate(name, spec, nil, validate.Create); err != nil {
		return nil, err
	}

	instanceID, now := s.NewID(), s.Now()
	if err := s.Store.Update(func(t *store.Txn) error {
		return t.PutInterface(name, spec, instanceID, now)
	}); err != nil {
		return nil, fmt.Errorf("write desired state: %w", err)
	}

	return s.applyAndRead(name)
}

// Update replaces a stored spec.
//
// REQ-API-069 refuses a name desired state does not describe: an update is not
// an upsert, and a PUT to an unknown name is far more often a mistake than an
// intent to create.
func (s Interfaces) Update(name string, spec model.InterfaceSpec) (*Interface, error) {
	snap := s.Store.Snapshot()
	stored, ok, err := snap.Interface(name)
	if err != nil {
		return nil, err
	}
	if !ok {
		return nil, reasonErr(ReasonInterfaceNotManaged,
			"desired state does not describe %q", name)
	}

	// The private key is write-only. An update that omits it keeps the stored
	// one, because REQ-KEY-002 means the caller could not have read it to send
	// it back, and clearing it would disconnect every peer.
	if spec.PrivateKey == "" {
		spec.PrivateKey = stored.PrivateKey
	}
	spec = s.applyDefaults(spec)

	peers, err := snap.Peers(name)
	if err != nil {
		return nil, err
	}
	if err := s.validate(name, spec, peers, validate.Update); err != nil {
		return nil, err
	}

	instanceID, createdAt, _ := snap.Identity(name)
	if err := s.Store.Update(func(t *store.Txn) error {
		return t.PutInterface(name, spec, instanceID, createdAt)
	}); err != nil {
		return nil, fmt.Errorf("write desired state: %w", err)
	}
	return s.applyAndRead(name)
}

// Get returns one interface. An interface outside desired state is reported
// with its spec absent, which REQ-RCN-031 requires of a FOREIGN one.
func (s Interfaces) Get(name string) (*Interface, error) {
	snap := s.Store.Snapshot()
	spec, managed, err := snap.Interface(name)
	if err != nil {
		return nil, err
	}
	if !managed {
		if !s.hostHas(name) {
			return nil, reasonErr(ReasonInterfaceNotFound,
				"no interface named %q", name)
		}
		return s.foreignView(name, snap), nil
	}
	return s.read(name, spec, snap)
}

// List returns every interface desired state describes, then every WireGuard
// link it does not — REQ-RCN-036 classifies the second set, and REQ-RCN-031
// requires them to appear.
func (s Interfaces) List() ([]Interface, error) {
	snap := s.Store.Snapshot()
	var out []Interface

	for _, name := range snap.Names() {
		spec, _, err := snap.Interface(name)
		if err != nil {
			return nil, err
		}
		iface, err := s.read(name, spec, snap)
		if err != nil {
			return nil, err
		}
		out = append(out, *iface)
	}

	names, err := s.Device.Names()
	if err != nil {
		return nil, fmt.Errorf("enumerate wireguard devices: %w", err)
	}
	for _, name := range names {
		if snap.Describes(name) {
			continue
		}
		out = append(out, *s.foreignView(name, snap))
	}
	return out, nil
}

// Delete removes the link from the kernel and the spec from the store —
// REQ-RCN-032 — together with its peers, in the same transaction under
// REQ-RCN-038.
//
// The order is kernel first: REQ-RCN-033 requires a deletion record when link
// removal does not complete, and the record is only meaningful if the store
// still describes what failed to go.
func (s Interfaces) Delete(name string) error {
	snap := s.Store.Snapshot()
	if !snap.Describes(name) {
		return reasonErr(ReasonInterfaceNotManaged,
			"desired state does not describe %q", name)
	}

	delErr := s.Link.Del(name)

	return s.Store.Update(func(t *store.Txn) error {
		// REQ-RCN-071 reaches DeleteInterface as well as release, so the
		// adoption record goes with the spec.
		t.RemoveInterface(name)
		if delErr != nil {
			// REQ-RCN-033 — the record is what makes REQ-RCN-034 report the
			// surviving link as ORPHANED rather than as a foreign one.
			t.PutDeletion(name, s.Now())
			return nil
		}
		t.ClearDeletion(name)
		return nil
	})
}

// ── internals ───────────────────────────────────────────────────────────────

func (s Interfaces) applyDefaults(spec model.InterfaceSpec) model.InterfaceSpec {
	if spec.ForwardPolicy.IntraInterface == "" {
		spec.ForwardPolicy.IntraInterface = s.Defaults.ForwardPolicy.IntraInterface
	}
	if spec.ForwardPolicy.InterInterface == "" {
		spec.ForwardPolicy.InterInterface = s.Defaults.ForwardPolicy.InterInterface
	}
	if spec.ForwardPolicy.External == "" {
		spec.ForwardPolicy.External = s.Defaults.ForwardPolicy.External
	}
	if spec.MTU == 0 {
		spec.MTU = s.Defaults.MTU
	}
	return spec
}

func (s Interfaces) newKey() (platform.Key, error) {
	if s.NewKey != nil {
		return s.NewKey()
	}
	return GenerateKey()
}

func (s Interfaces) validate(
	name string, spec model.InterfaceSpec, peers []model.Peer, op validate.Op,
) error {
	host, err := validate.ReadHost(s.Device, s.Link)
	if err != nil {
		return err
	}
	v := validate.Validator{Host: host, Desired: s.Store.Snapshot()}
	return v.Interface(name, spec, peers, op).Err()
}

// applyAndRead reconciles the interface and returns it, so the status in the
// response describes the change rather than the state before it — REQ-RCN-020
// lists an API write among the triggers.
func (s Interfaces) applyAndRead(name string) (*Interface, error) {
	if s.Engine != nil {
		s.Engine.Interface(name)
	}
	snap := s.Store.Snapshot()
	spec, _, err := snap.Interface(name)
	if err != nil {
		return nil, err
	}
	return s.read(name, spec, snap)
}

func (s Interfaces) read(
	name string, spec model.InterfaceSpec, snap *store.Snapshot,
) (*Interface, error) {
	peers, err := snap.Peers(name)
	if err != nil {
		return nil, err
	}
	instanceID, createdAt, _ := snap.Identity(name)

	out := &Interface{
		Name: name,
		Spec: viewOf(spec),
		Status: InterfaceStatus{
			ListenPort: spec.ListenPort,
			InstanceID: instanceID,
			CreatedAt:  createdAt,
			Revision:   Revision(spec, peers),
			Ownership:  model.Managed,
			OperState:  reconcile.OperAbsent,
			PeerCount:  len(peers),
		},
	}

	// REQ-RES-024 — the kernel-sourced fields come from the kernel rather than
	// from the store, so a value the agent did not set is still reported.
	if ds, err := s.Device.Snapshot(name); err == nil {
		out.Status.PublicKey = ds.PublicKey
		out.Status.ListenPort = ds.ListenPort
		out.Status.PeerCount = len(ds.Peers)
	}
	if ls, err := s.Link.State(name); err == nil {
		out.Status.OperState = operStateOf(ls)
	}
	if s.Engine != nil {
		if st, ok := s.Engine.Status(name); ok {
			out.Status.Condition = st.Condition
			out.Status.OperState = st.OperState
			for _, w := range st.Warnings {
				out.Status.Warnings = append(out.Status.Warnings,
					validate.Finding{Reason: w.Reason, Message: w.Message})
			}
		}
	}
	return out, nil
}

// foreignView reports an interface desired state does not describe. REQ-RCN-031
// requires the spec absent, which is what keeps a caller from mistaking a read
// of the kernel for something the agent is enforcing.
func (s Interfaces) foreignView(name string, snap *store.Snapshot) *Interface {
	out := &Interface{
		Name: name,
		Status: InterfaceStatus{
			Ownership: model.OwnershipOf(false, snap.DeletionRecord(name)),
			OperState: reconcile.OperAbsent,
		},
	}
	if ds, err := s.Device.Snapshot(name); err == nil {
		out.Status.PublicKey = ds.PublicKey
		out.Status.ListenPort = ds.ListenPort
		out.Status.PeerCount = len(ds.Peers)
	}
	if ls, err := s.Link.State(name); err == nil {
		out.Status.OperState = operStateOf(ls)
	}
	return out
}

func (s Interfaces) hostHas(name string) bool {
	names, err := s.Device.Names()
	if err != nil {
		return false
	}
	for _, n := range names {
		if n == name {
			return true
		}
	}
	return false
}

// operStateOf implements REQ-RES-019 the same way the engine does: the
// administrative flag, because a WireGuard link reports its operational state
// as unknown even while up.
func operStateOf(ls platform.LinkState) string {
	if ls.AdminUp {
		return reconcile.OperUp
	}
	return reconcile.OperDown
}
