package validate_test

import (
	"encoding/base64"
	"net/netip"
	"strings"
	"testing"

	"wg-agent/internal/model"
	"wg-agent/internal/platform/fake"
	"wg-agent/internal/validate"
)

// key returns a distinct valid public key: base64 of exactly 32 bytes, which is
// what REQ-VAL-011 requires.
func key(seed byte) string {
	b := make([]byte, 32)
	for i := range b {
		b[i] = seed
	}
	return base64.StdEncoding.EncodeToString(b)
}

// desired is desired state in memory, reduced to the three questions
// validate.Desired asks.
type desired struct {
	ifaces    map[string]model.InterfaceSpec
	deletions map[string]bool
}

func newDesired() *desired {
	return &desired{
		ifaces:    map[string]model.InterfaceSpec{},
		deletions: map[string]bool{},
	}
}

func (d *desired) Names() []string {
	out := make([]string, 0, len(d.ifaces))
	for k := range d.ifaces {
		out = append(out, k)
	}
	return out
}

func (d *desired) Interface(n string) (model.InterfaceSpec, bool, error) {
	s, ok := d.ifaces[n]
	return s, ok, nil
}
func (d *desired) DeletionRecord(n string) bool { return d.deletions[n] }

// goodSpec passes every rule, so a test changes one thing and sees one finding.
func goodSpec() model.InterfaceSpec {
	return model.InterfaceSpec{
		PrivateKey:    key(1),
		ListenPort:    51820,
		Addresses:     []string{"10.100.0.1/24"},
		MTU:           1420,
		ForwardPolicy: model.DefaultForwardPolicy(),
		Enabled:       true,
	}
}

func peer(pub, allowed string) model.Peer {
	return model.Peer{
		InterfaceName: "wg0",
		PublicKey:     pub,
		Spec:          model.PeerSpec{AllowedIPs: []string{allowed}},
	}
}

func prefixes(t *testing.T, ss ...string) []netip.Prefix {
	t.Helper()
	out := make([]netip.Prefix, 0, len(ss))
	for _, s := range ss {
		p, err := netip.ParsePrefix(s)
		if err != nil {
			t.Fatalf("bad prefix %q: %v", s, err)
		}
		out = append(out, p)
	}
	return out
}

// hasReason reports whether the list carries the code.
func hasReason(fs []validate.Finding, reason string) bool {
	for _, f := range fs {
		if f.Reason == reason {
			return true
		}
	}
	return false
}

func wantError(t *testing.T, r validate.Result, reason string) {
	t.Helper()
	if !hasReason(r.Errors, reason) {
		t.Errorf("errors = %+v, want %s", r.Errors, reason)
	}
	if !r.Blocked() {
		t.Error("an error-severity finding must block the write — REQ-VAL-001")
	}
}

func wantWarning(t *testing.T, r validate.Result, reason string) {
	t.Helper()
	if !hasReason(r.Warnings, reason) {
		t.Errorf("warnings = %+v, want %s", r.Warnings, reason)
	}
	// REQ-VAL-002 — a warning does not block.
	if r.Blocked() {
		t.Errorf("a warning must not block the write, errors = %+v", r.Errors)
	}
}

// A spec that breaks no rule produces nothing. Without this every test below
// could pass on a validator that reports everything.
func TestInterface_CleanSpecHasNoFindings_REQ_VAL_001(t *testing.T) {
	v := validate.Validator{Desired: newDesired()}
	r := v.Interface("wg0", goodSpec(), []model.Peer{peer(key(9), "10.100.0.2/32")},
		validate.Create)

	if len(r.Errors) != 0 || len(r.Warnings) != 0 {
		t.Fatalf("clean spec produced %+v / %+v", r.Errors, r.Warnings)
	}
}

// ── errors ──────────────────────────────────────────────────────────────────

func TestInterface_NameInvalid_REQ_VAL_010(t *testing.T) {
	v := validate.Validator{Desired: newDesired()}
	bad := []string{
		"",                  // empty
		"0wg",               // leading digit
		"-wg",               // leading dash
		"wg 0",              // space
		"wg.0",              // dot
		"averyverylongname", // 17 characters
		"wg0!",              // punctuation
	}
	for _, name := range bad {
		r := v.Interface(name, goodSpec(), nil, validate.Create)
		if !hasReason(r.Errors, validate.ReasonNameInvalid) {
			t.Errorf("%q was accepted", name)
		}
	}
	good := []string{"wg0", "a", "wg-home", "wg_home", "abcdefghijklmno"}
	for _, name := range good {
		r := v.Interface(name, goodSpec(), nil, validate.Create)
		if hasReason(r.Errors, validate.ReasonNameInvalid) {
			t.Errorf("%q was rejected", name)
		}
	}
}

func TestPeer_PublicKeyInvalid_REQ_VAL_011(t *testing.T) {
	v := validate.Validator{Desired: newDesired()}
	bad := []string{
		"not-base64!",
		base64.StdEncoding.EncodeToString(make([]byte, 31)), // 31 bytes
		base64.StdEncoding.EncodeToString(make([]byte, 33)), // 33 bytes
		"",
	}
	for _, pub := range bad {
		r := v.Interface("wg0", goodSpec(),
			[]model.Peer{peer(pub, "10.100.0.2/32")}, validate.Create)
		if !hasReason(r.Errors, validate.ReasonPublicKeyInvalid) {
			t.Errorf("%q was accepted as a public key", pub)
		}
	}
}

// REQ-VAL-012: for two identical prefixes the later-configured peer silently
// displaces the earlier one, so the ambiguity is refused.
func TestInterface_DuplicateAllowedIPs_REQ_VAL_012(t *testing.T) {
	v := validate.Validator{Desired: newDesired()}
	r := v.Interface("wg0", goodSpec(), []model.Peer{
		peer(key(8), "10.100.0.2/32"),
		peer(key(9), "10.100.0.2/32"),
	}, validate.Create)

	wantError(t, r, validate.ReasonAllowedIPsDup)
}

// One peer holding the same entry twice is not two peers claiming it.
func TestInterface_OnePeerRepeatingAnEntryIsNotADuplicate_REQ_VAL_012(t *testing.T) {
	v := validate.Validator{Desired: newDesired()}
	p := peer(key(9), "10.100.0.2/32")
	p.Spec.AllowedIPs = []string{"10.100.0.2/32", "10.100.0.2/32"}

	r := v.Interface("wg0", goodSpec(), []model.Peer{p}, validate.Create)
	if hasReason(r.Errors, validate.ReasonAllowedIPsDup) {
		t.Errorf("errors = %+v; one peer cannot displace itself", r.Errors)
	}
}

// REQ-VAL-013 reaches foreign interfaces, and excludes the one the spec names.
func TestInterface_ListenPortInUse_REQ_VAL_013(t *testing.T) {
	v := validate.Validator{
		Host:    validate.Host{Ports: map[string]int{"wg1": 51820}},
		Desired: newDesired(),
	}
	r := v.Interface("wg0", goodSpec(), nil, validate.Create)
	wantError(t, r, validate.ReasonListenPortInUse)

	// The same port on the interface the spec names is that interface's own,
	// which is what lets an adopted spec validate.
	v.Host.Ports = map[string]int{"wg0": 51820}
	if r := v.Interface("wg0", goodSpec(), nil, validate.Adopt); r.Blocked() {
		t.Errorf("an adopted spec was rejected against its own link: %+v", r.Errors)
	}
}

func TestInterface_AddressConflict_REQ_VAL_014(t *testing.T) {
	v := validate.Validator{
		Host: validate.Host{
			Addresses: map[string][]netip.Prefix{
				"wg1": prefixes(t, "10.100.0.5/24"),
			},
		},
		Desired: newDesired(),
	}
	r := v.Interface("wg0", goodSpec(), nil, validate.Create)
	wantError(t, r, validate.ReasonAddressConflict)

	// A non-overlapping subnet is fine.
	v.Host.Addresses = map[string][]netip.Prefix{"wg1": prefixes(t, "10.200.0.1/24")}
	if r := v.Interface("wg0", goodSpec(), nil, validate.Create); r.Blocked() {
		t.Errorf("a non-overlapping subnet was rejected: %+v", r.Errors)
	}
}

// REQ-VAL-015 keeps a name collision from becoming a silent takeover.
func TestInterface_CreateNamingAnExistingLink_REQ_VAL_015(t *testing.T) {
	d := newDesired()
	v := validate.Validator{
		Host:    validate.Host{Ports: map[string]int{"wg0": 51999}},
		Desired: d,
	}
	spec := goodSpec()

	r := v.Interface("wg0", spec, nil, validate.Create)
	wantError(t, r, validate.ReasonInterfaceExists)
	if !strings.Contains(r.Errors[0].Message, "adopt") {
		t.Errorf("the message must point at the supported path, got %q", r.Errors[0].Message)
	}

	// An update is not a create; the link existing is the premise.
	if r := v.Interface("wg0", spec, nil, validate.Update); hasReason(r.Errors, validate.ReasonInterfaceExists) {
		t.Error("an update must not be refused for the link existing")
	}
	// Nor is an adoption.
	if r := v.Interface("wg0", spec, nil, validate.Adopt); hasReason(r.Errors, validate.ReasonInterfaceExists) {
		t.Error("an adoption must not be refused for the link existing")
	}
}

// The one exemption: the agent's own orphan under REQ-RCN-034. Refusing to
// recreate a link it failed to delete would leave a shell as the only recovery.
func TestInterface_OrphanMayBeRecreated_REQ_VAL_015(t *testing.T) {
	d := newDesired()
	d.deletions["wg0"] = true
	v := validate.Validator{
		Host:    validate.Host{Ports: map[string]int{"wg0": 51999}},
		Desired: d,
	}
	if r := v.Interface("wg0", goodSpec(), nil, validate.Create); hasReason(r.Errors, validate.ReasonInterfaceExists) {
		t.Errorf("an orphan must be recreatable: %+v", r.Errors)
	}
}

func TestInterface_AddressesRequired_REQ_VAL_016(t *testing.T) {
	v := validate.Validator{Desired: newDesired()}
	spec := goodSpec()
	spec.Addresses = nil

	wantError(t, v.Interface("wg0", spec, nil, validate.Create),
		validate.ReasonAddressesRequired)
}

func TestPeer_AllowedIPsRequired_REQ_VAL_017(t *testing.T) {
	v := validate.Validator{Desired: newDesired()}
	p := model.Peer{InterfaceName: "wg0", PublicKey: key(9)}

	wantError(t, v.Interface("wg0", goodSpec(), []model.Peer{p}, validate.Create),
		validate.ReasonAllowedIPsReq)
}

// REQ-VAL-020: silent omission is prohibited, as is accepting the value
// without configuring it.
func TestInterface_IPv6Rejected_REQ_VAL_020(t *testing.T) {
	v := validate.Validator{Desired: newDesired()}

	spec := goodSpec()
	spec.Addresses = []string{"fd00::1/64"}
	wantError(t, v.Interface("wg0", spec, nil, validate.Create),
		validate.ReasonIPv6NotSupported)

	r := v.Interface("wg0", goodSpec(),
		[]model.Peer{peer(key(9), "fd00::2/128")}, validate.Create)
	wantError(t, r, validate.ReasonIPv6NotSupported)
}

// REQ-VAL-021: the declaration is self-contradictory — policy permits egress
// while the kernel precondition for egress is absent.
func TestInterface_ExternalWithoutUplinkForwarding_REQ_VAL_021(t *testing.T) {
	v := validate.Validator{Desired: newDesired()}
	spec := goodSpec()
	spec.ForwardPolicy.External = model.Allow
	spec.NAT = model.NatSpec{Enabled: true, EnableUplinkForwarding: false}

	wantError(t, v.Interface("wg0", spec, nil, validate.Create),
		validate.ReasonNeedsUplink)
}

// REQ-VAL-022: a typo here causes silent loss of connectivity.
func TestInterface_UnknownPeerInterface_REQ_VAL_022(t *testing.T) {
	d := newDesired()
	d.ifaces["wg1"] = goodSpec()
	v := validate.Validator{Desired: d}

	spec := goodSpec()
	spec.ForwardPolicy.InterInterface = model.AllowList
	spec.ForwardPolicy.AllowedPeerInterfaces = []string{"wg1", "wg7"}

	r := v.Interface("wg0", spec, nil, validate.Create)
	wantError(t, r, validate.ReasonPeerIfaceNotFound)
	if !strings.Contains(r.Errors[0].Message, "wg7") {
		t.Errorf("the message must name the unknown interface, got %q", r.Errors[0].Message)
	}
}

// ── warnings ────────────────────────────────────────────────────────────────

// REQ-VAL-023: valid and stateful under REQ-FWD-017, usually a forgotten
// reciprocal declaration.
func TestInterface_OneSidedAllowList_REQ_VAL_023(t *testing.T) {
	other := goodSpec()
	other.ForwardPolicy.InterInterface = model.AllowList
	// wg1 names nobody in return.
	d := newDesired()
	d.ifaces["wg1"] = other
	v := validate.Validator{Desired: d}

	spec := goodSpec()
	spec.ForwardPolicy.InterInterface = model.AllowList
	spec.ForwardPolicy.AllowedPeerInterfaces = []string{"wg1"}

	wantWarning(t, v.Interface("wg0", spec, nil, validate.Create),
		validate.ReasonOneSided)
}

// A reciprocal declaration produces no warning.
func TestInterface_ReciprocalAllowListIsQuiet_REQ_VAL_023(t *testing.T) {
	other := goodSpec()
	other.ForwardPolicy.InterInterface = model.AllowList
	other.ForwardPolicy.AllowedPeerInterfaces = []string{"wg0"}
	d := newDesired()
	d.ifaces["wg1"] = other
	v := validate.Validator{Desired: d}

	spec := goodSpec()
	spec.ForwardPolicy.InterInterface = model.AllowList
	spec.ForwardPolicy.AllowedPeerInterfaces = []string{"wg1"}

	r := v.Interface("wg0", spec, nil, validate.Create)
	if hasReason(r.Warnings, validate.ReasonOneSided) {
		t.Errorf("warnings = %+v; a reciprocal declaration is complete", r.Warnings)
	}
}

// REQ-VAL-030: longest-prefix matching makes this valid, and it usually
// indicates a mistake.
func TestInterface_OverlappingAllowedIPs_REQ_VAL_030(t *testing.T) {
	v := validate.Validator{Desired: newDesired()}
	spec := goodSpec()
	spec.Addresses = []string{"10.100.0.1/16"}

	r := v.Interface("wg0", spec, []model.Peer{
		peer(key(8), "10.100.0.0/24"),
		peer(key(9), "10.100.0.2/32"),
	}, validate.Create)

	wantWarning(t, r, validate.ReasonAllowedIPsOverlap)
}

// REQ-VAL-031: valid for site-to-site, usually a mistake otherwise.
func TestInterface_AllowedIPsOutOfSubnet_REQ_VAL_031(t *testing.T) {
	v := validate.Validator{Desired: newDesired()}
	r := v.Interface("wg0", goodSpec(),
		[]model.Peer{peer(key(9), "192.168.50.0/24")}, validate.Create)

	wantWarning(t, r, validate.ReasonAllowedIPsSubnet)
}

// A peer inside the subnet is quiet.
func TestInterface_AllowedIPsInSubnetIsQuiet_REQ_VAL_031(t *testing.T) {
	v := validate.Validator{Desired: newDesired()}
	r := v.Interface("wg0", goodSpec(),
		[]model.Peer{peer(key(9), "10.100.0.7/32")}, validate.Create)

	if hasReason(r.Warnings, validate.ReasonAllowedIPsSubnet) {
		t.Errorf("warnings = %+v; 10.100.0.7/32 is inside 10.100.0.0/24", r.Warnings)
	}
}

// A prefix wider than the interface subnet reaches outside it, even though it
// overlaps.
func TestInterface_WiderThanTheSubnetIsOutside_REQ_VAL_031(t *testing.T) {
	v := validate.Validator{Desired: newDesired()}
	r := v.Interface("wg0", goodSpec(),
		[]model.Peer{peer(key(9), "10.0.0.0/8")}, validate.Create)

	wantWarning(t, r, validate.ReasonAllowedIPsSubnet)
}

func TestInterface_MTUOutOfRange_REQ_VAL_032(t *testing.T) {
	v := validate.Validator{Desired: newDesired()}
	for _, mtu := range []int{576, 1279, 1501, 9000} {
		spec := goodSpec()
		spec.MTU = mtu
		if r := v.Interface("wg0", spec, nil, validate.Create); !hasReason(r.Warnings, validate.ReasonMTUOutOfRange) {
			t.Errorf("mtu %d produced no warning", mtu)
		}
	}
	for _, mtu := range []int{1280, 1420, 1500} {
		spec := goodSpec()
		spec.MTU = mtu
		if r := v.Interface("wg0", spec, nil, validate.Create); hasReason(r.Warnings, validate.ReasonMTUOutOfRange) {
			t.Errorf("mtu %d is inside the range and warned", mtu)
		}
	}
}

// REQ-VAL-033: the kernel stores only the resolved address, so a DNS change
// does not propagate.
func TestPeer_HostnameEndpoint_REQ_VAL_033(t *testing.T) {
	v := validate.Validator{Desired: newDesired()}

	p := peer(key(9), "10.100.0.2/32")
	p.Spec.Endpoint = "vpn.example.com:51820"
	wantWarning(t, v.Interface("wg0", goodSpec(), []model.Peer{p}, validate.Create),
		validate.ReasonEndpointNotIP)

	p.Spec.Endpoint = "203.0.113.9:51820"
	r := v.Interface("wg0", goodSpec(), []model.Peer{p}, validate.Create)
	if hasReason(r.Warnings, validate.ReasonEndpointNotIP) {
		t.Errorf("a literal address must not warn: %+v", r.Warnings)
	}
}

// REQ-VAL-034: the field is ignored in that combination.
func TestInterface_IgnoredPeerInterfaces_REQ_VAL_034(t *testing.T) {
	d := newDesired()
	d.ifaces["wg1"] = goodSpec()
	v := validate.Validator{Desired: d}

	spec := goodSpec()
	spec.ForwardPolicy.InterInterface = model.Deny
	spec.ForwardPolicy.AllowedPeerInterfaces = []string{"wg1"}

	wantWarning(t, v.Interface("wg0", spec, nil, validate.Create),
		validate.ReasonPeerIfacesIgnored)
}

// REQ-VAL-035: traffic leaves without source NAT, so return traffic almost
// certainly has no route back.
func TestInterface_ExternalWithoutNAT_REQ_VAL_035(t *testing.T) {
	v := validate.Validator{Desired: newDesired()}
	spec := goodSpec()
	spec.ForwardPolicy.External = model.Allow
	// EnableUplinkForwarding true keeps REQ-VAL-021 from blocking, so the
	// warning is what remains.
	spec.NAT = model.NatSpec{Enabled: false, EnableUplinkForwarding: true}

	wantWarning(t, v.Interface("wg0", spec, nil, validate.Create),
		validate.ReasonExternalNoNAT)
}

// ── the peer entry point ────────────────────────────────────────────────────

// A peer added to an interface is checked against the peers already there.
func TestPeer_DuplicateAgainstStoredPeers_REQ_VAL_012(t *testing.T) {
	v := validate.Validator{Desired: newDesired()}
	existing := []model.Peer{peer(key(8), "10.100.0.2/32")}

	r := v.Peer(goodSpec(), existing, peer(key(9), "10.100.0.2/32"))
	wantError(t, r, validate.ReasonAllowedIPsDup)
}

// Replacing a peer with itself is not a duplicate of itself, which is what
// makes an update of one peer's allowed_ips possible.
func TestPeer_ReplacingItselfIsNotADuplicate_REQ_VAL_012(t *testing.T) {
	v := validate.Validator{Desired: newDesired()}
	existing := []model.Peer{peer(key(9), "10.100.0.2/32")}

	r := v.Peer(goodSpec(), existing, peer(key(9), "10.100.0.2/32"))
	if r.Blocked() {
		t.Errorf("errors = %+v; a peer cannot duplicate itself", r.Errors)
	}
}

// ── the host reader ─────────────────────────────────────────────────────────

// ReadHost collects the facts REQ-VAL-013 and REQ-VAL-014 compare against,
// over every WireGuard interface rather than the managed ones alone.
func TestReadHost_CollectsEveryWireGuardInterface_REQ_VAL_013(t *testing.T) {
	n := fake.NewNode()
	n.AddInterface("wg0", "10.100.0.1/24", 51820)
	n.AddInterface("wg1", "10.101.0.1/24", 51821)

	h, err := validate.ReadHost(n, fake.LinkView{N: n})
	if err != nil {
		t.Fatalf("read host: %v", err)
	}
	if h.Ports["wg0"] != 51820 || h.Ports["wg1"] != 51821 {
		t.Errorf("ports = %v", h.Ports)
	}
	if len(h.Addresses["wg0"]) != 1 || h.Addresses["wg0"][0].String() != "10.100.0.1/24" {
		t.Errorf("addresses = %v", h.Addresses)
	}
}

// The first error is the reason code the API returns, so the order is fixed
// rather than left to map iteration.
func TestResult_ErrCarriesTheFirstReason_REQ_VAL_001(t *testing.T) {
	v := validate.Validator{Desired: newDesired()}
	spec := goodSpec()
	spec.Addresses = nil

	err := v.Interface("0bad name", spec, nil, validate.Create).Err()
	if err == nil {
		t.Fatal("Err returned nil for a blocked result")
	}
	var e *validate.Error
	if !asError(err, &e) {
		t.Fatalf("error %v is not a validate.Error", err)
	}
	// checkName runs before checkAddresses, per the order in Interface.
	if e.Reason() != validate.ReasonNameInvalid {
		t.Errorf("reason = %q, want INTERFACE_NAME_INVALID", e.Reason())
	}
	if len(e.All) < 2 {
		t.Errorf("All carries %d findings, want every error", len(e.All))
	}
}

func TestResult_ErrIsNilWhenClean_REQ_VAL_002(t *testing.T) {
	v := validate.Validator{Desired: newDesired()}
	spec := goodSpec()
	spec.MTU = 9000 // a warning alone

	r := v.Interface("wg0", spec, nil, validate.Create)
	if err := r.Err(); err != nil {
		t.Errorf("Err = %v; a warning must not block", err)
	}
	if len(r.Warnings) == 0 {
		t.Error("the warning is missing")
	}
}

func asError(err error, target **validate.Error) bool {
	if e, ok := err.(*validate.Error); ok {
		*target = e
		return true
	}
	return false
}
