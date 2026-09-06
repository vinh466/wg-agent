package service_test

import (
	"encoding/json"
	"strings"
	"testing"

	"wg-agent/internal/model"
	"wg-agent/internal/platform"
	"wg-agent/internal/platform/fake"
	"wg-agent/internal/service"
)

// build assembles a report over an in-memory node. No privilege is used, which
// is the property that keeps every test above internal/platform cheap.
func build(t *testing.T, n *fake.Node) service.Report {
	t.Helper()
	r, err := service.Readiness{
		Device:  n,
		Link:    fake.LinkView{N: n},
		HostFS:  n,
		Desired: n,
	}.Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	return r
}

func only(t *testing.T, r service.Report, name string) service.InterfaceReadiness {
	t.Helper()
	for _, i := range r.Interfaces {
		if i.Name == name {
			return i
		}
	}
	t.Fatalf("interface %q absent from the report", name)
	return service.InterfaceReadiness{}
}

func findingByCode(i service.InterfaceReadiness, code string) *service.Finding {
	for k := range i.Findings {
		if i.Findings[k].HintCode == code {
			return &i.Findings[k]
		}
	}
	return nil
}

func TestReadiness_NamesEveryForeignInterface_REQ_DIA_040(t *testing.T) {
	n := fake.NewNode().AddInterface("wg0", "10.0.0.1/24", 51820)
	n.AddInterface("wg1", "10.1.0.1/24", 51821)

	r := build(t, n)
	if len(r.Interfaces) != 2 {
		t.Fatalf("want 2 interfaces, got %d", len(r.Interfaces))
	}
	for _, i := range r.Interfaces {
		if i.Ownership != model.Foreign {
			t.Errorf("%s: want FOREIGN, got %s", i.Name, i.Ownership)
		}
		if i.Spec == nil {
			t.Errorf("%s: a foreign interface must carry the spec adoption would read", i.Name)
		}
	}
}

func TestReadiness_FindingCarriesDiagnosticFields_REQ_DIA_041(t *testing.T) {
	n := fake.NewNode().AddInterface("wg0", "10.0.0.1/24", 51820).
		WithUnit("wg0", platform.UnitEnabled)

	f := findingByCode(only(t, build(t, n), "wg0"), service.HintUnitEnabled)
	if f == nil {
		t.Fatal("no finding for an enabled unit")
	}
	// REQ-DIA-002 fixes the shape: name, result, observed, expected, hint_code
	// and hint. REQ-DIA-041 reuses it rather than defining a second one.
	for label, v := range map[string]string{
		"name": f.Name, "observed": f.Observed, "expected": f.Expected,
		"hint_code": f.HintCode, "hint": f.Hint,
	} {
		if v == "" {
			t.Errorf("finding field %q is empty", label)
		}
	}
	if f.Result == "" {
		t.Error("finding result is empty")
	}
}

func TestReadiness_EnabledUnitFails_REQ_DIA_043(t *testing.T) {
	n := fake.NewNode().AddInterface("wg0", "10.0.0.1/24", 51820).
		WithUnit("wg0", platform.UnitEnabled)

	i := only(t, build(t, n), "wg0")
	f := findingByCode(i, service.HintUnitEnabled)
	if f == nil || f.Result != service.Fail {
		t.Fatalf("want a FAIL for an enabled wg-quick unit, got %+v", f)
	}
	if !i.Blocking() {
		t.Error("an enabled unit must block adoption")
	}
	// REQ-CLI-005 renders the hint, so it has to name the action that clears
	// the finding rather than restate the symptom (REQ-DIA-003).
	if !strings.Contains(f.Hint, "systemctl disable") {
		t.Errorf("hint does not name the corrective action: %q", f.Hint)
	}
}

func TestReadiness_BareConfigFileDoesNotBlock_REQ_DIA_043(t *testing.T) {
	// A configuration file survives `systemctl disable` and describes an
	// interface nobody is starting, so its presence is not contention.
	n := fake.NewNode().AddInterface("wg0", "10.0.0.1/24", 51820).
		WithUnit("wg0", platform.UnitDisabled).
		WithConfig("wg0", platform.WgQuickConfig{Path: "/etc/wireguard/wg0.conf", Present: true})

	if only(t, build(t, n), "wg0").Blocking() {
		t.Error("a present but disabled wg-quick configuration must not block adoption")
	}
}

func TestReadiness_UnsupportedDirectiveWarns_REQ_DIA_044(t *testing.T) {
	n := fake.NewNode().AddInterface("wg0", "10.0.0.1/24", 51820).
		WithConfig("wg0", platform.WgQuickConfig{
			Path: "/etc/wireguard/wg0.conf", Present: true,
			Directives: []string{"PostUp", "DNS"},
		})

	i := only(t, build(t, n), "wg0")
	hook := findingByCode(i, service.HintHookDirective)
	if hook == nil || hook.Result != service.Warn {
		t.Fatalf("want a WARN for PostUp, got %+v", hook)
	}
	if !strings.Contains(hook.Observed, "PostUp") {
		t.Errorf("finding does not name the directive: %q", hook.Observed)
	}
	if findingByCode(i, service.HintUnownedDirective) == nil {
		t.Error("want a WARN for DNS")
	}
	if i.Blocking() {
		t.Error("an unsupported directive is advisory, not blocking")
	}
}

func TestReadiness_UnparseableConfigWarnsRatherThanFails_REQ_DIA_046(t *testing.T) {
	n := fake.NewNode().AddInterface("wg0", "10.0.0.1/24", 51820).
		WithConfig("wg0", platform.WgQuickConfig{
			Path: "/etc/wireguard/wg0.conf", Present: true, Unparseable: true,
		})

	i := only(t, build(t, n), "wg0")
	f := findingByCode(i, service.HintConfigUnparseable)
	if f == nil || f.Result != service.Warn {
		t.Fatalf("want a WARN for an unreadable configuration, got %+v", f)
	}
	if i.Blocking() {
		t.Error("field values come from the kernel, so an unreadable file must not block")
	}
}

func TestReadiness_ReportOmitsKeys_REQ_DIA_047(t *testing.T) {
	n := fake.NewNode().AddInterface("wg0", "10.0.0.1/24", 51820)
	n.AddPeer("wg0", "peer-public-key-aaaa", "10.0.0.2/32", "", true)

	b, err := json.Marshal(build(t, n))
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}
	body := string(b)

	// A 32-byte key base64-encodes to 44 characters ending in '='. The fake
	// uses an all-zero key, whose encoding is a run of 'A'.
	if strings.Contains(body, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=") {
		t.Error("the report serialised a key")
	}
	if !strings.Contains(body, `"private_key_present":true`) {
		t.Error("the report must state that a private key is present")
	}
	if !strings.Contains(body, `"preshared_key_present":true`) {
		t.Error("the report must state that a preshared key is present")
	}
}

func TestReadiness_PeerEndpointWarns_REQ_DIA_048(t *testing.T) {
	n := fake.NewNode().AddInterface("wg0", "10.0.0.1/24", 51820)
	n.AddPeer("wg0", "peer-public-key-aaaa", "10.0.0.2/32", "203.0.113.9:51820", false)

	i := only(t, build(t, n), "wg0")
	f := findingByCode(i, service.HintPeerEndpoint)
	if f == nil || f.Result != service.Warn {
		t.Fatalf("want a WARN naming the peer whose endpoint is discarded, got %+v", f)
	}
	if i.Spec.Peers[0].EndpointPresent != true {
		t.Error("the spec must record that the kernel holds an endpoint")
	}
	if i.Blocking() {
		t.Error("a discarded endpoint is a known loss, not a blocker")
	}
}

func TestReadiness_UndeterminedUnitIsUnknown_REQ_DIA_050(t *testing.T) {
	n := fake.NewNode().AddInterface("wg0", "10.0.0.1/24", 51820).
		WithUnit("wg0", platform.UnitUndetermined)

	i := only(t, build(t, n), "wg0")
	f := findingByCode(i, service.HintUnitUndetermined)
	if f == nil || f.Result != service.Unknown {
		t.Fatalf("want UNKNOWN when enablement cannot be determined, got %+v", f)
	}
	if i.Blocking() {
		t.Error("REQ-RCN-064 blocks on FAIL alone, so UNKNOWN must not refuse adoption")
	}
}

func TestReadiness_OwnershipFromStoreNotCreationHistory_REQ_RES_017(t *testing.T) {
	n := fake.NewNode().
		AddInterface("wg0", "10.0.0.1/24", 51820).WithManaged("wg0").
		AddInterface("wg1", "10.1.0.1/24", 51821).WithDeletionRecord("wg1").
		AddInterface("wg2", "10.2.0.1/24", 51822)

	r := build(t, n)
	want := map[string]model.Ownership{
		"wg0": model.Managed, "wg1": model.Orphaned, "wg2": model.Foreign,
	}
	for name, w := range want {
		got := only(t, r, name)
		if got.Ownership != w {
			t.Errorf("%s: want %s, got %s", name, w, got.Ownership)
		}
	}
	// Adoption applies to a foreign interface alone, so only it carries a spec.
	if only(t, r, "wg0").Spec != nil || only(t, r, "wg1").Spec != nil {
		t.Error("a managed or orphaned interface must not carry an adoptable spec")
	}
	if only(t, r, "wg2").Spec == nil {
		t.Error("a foreign interface must carry one")
	}
}

func TestReadiness_OperStateFromAdminFlag_REQ_RES_019(t *testing.T) {
	// A WireGuard link reports its operational state as unknown even while up,
	// so the spec's `enabled` has to come from the administrative flag.
	n := fake.NewNode().AddInterface("wg0", "10.0.0.1/24", 51820)
	l := n.Links["wg0"]
	l.AdminUp = false
	n.Links["wg0"] = l

	if only(t, build(t, n), "wg0").Spec.Enabled {
		t.Error("enabled must follow the administrative flag")
	}
}

func TestReadiness_PortCollisionReachesForeignInterfaces_REQ_VAL_013(t *testing.T) {
	// Both interfaces are foreign. The managed-only reading of REQ-VAL-013
	// would have found no collision here and let adoption fail at apply time.
	n := fake.NewNode().
		AddInterface("wg0", "10.0.0.1/24", 51820).
		AddInterface("wg1", "10.1.0.1/24", 51820)

	i := only(t, build(t, n), "wg0")
	f := findingByCode(i, service.HintPortCollision)
	if f == nil || f.Result != service.Fail {
		t.Fatalf("want a FAIL for a port held by another host interface, got %+v", f)
	}
	if !strings.Contains(f.Observed, "wg1") {
		t.Errorf("finding does not name the other interface: %q", f.Observed)
	}
}

func TestReadiness_AddressOverlapReachesForeignInterfaces_REQ_VAL_014(t *testing.T) {
	n := fake.NewNode().
		AddInterface("wg0", "10.0.0.1/24", 51820).
		AddInterface("wg1", "10.0.0.2/24", 51821)

	if findingByCode(only(t, build(t, n), "wg0"), service.HintAddressCollision) == nil {
		t.Error("want a FAIL for an overlapping subnet on another host interface")
	}
}

func TestReadiness_EmptyAddressesFails_REQ_VAL_016(t *testing.T) {
	n := fake.NewNode().AddInterface("wg0", "10.0.0.1/24", 51820).WithAddress("wg0")

	i := only(t, build(t, n), "wg0")
	f := findingByCode(i, service.HintNoAddress)
	if f == nil || f.Result != service.Fail {
		t.Fatalf("want a FAIL for a link with no address, got %+v", f)
	}
	if !i.Blocking() {
		t.Error("an empty address list must block, since REQ-VAL-016 rejects the spec")
	}
}

func TestReadiness_IPv6AddressFails_REQ_VAL_020(t *testing.T) {
	n := fake.NewNode().AddInterface("wg0", "10.0.0.1/24", 51820).
		WithAddress("wg0", "10.0.0.1/24", "fd00::1/64")

	if findingByCode(only(t, build(t, n), "wg0"), service.HintIPv6Address) == nil {
		t.Error("want a FAIL for an IPv6 address, per ADR-0005")
	}
}

func TestReadiness_PeerIPv6AllowedIPsFails_REQ_VAL_020(t *testing.T) {
	n := fake.NewNode().AddInterface("wg0", "10.0.0.1/24", 51820)
	n.AddPeer("wg0", "peer-public-key-aaaa", "fd00::/8", "", false)

	i := only(t, build(t, n), "wg0")
	f := findingByCode(i, service.HintPeerIPv6)
	if f == nil || f.Result != service.Fail {
		t.Fatalf("want a FAIL for an IPv6 entry in a peer's allowed-ips, got %+v", f)
	}
	if !i.Blocking() {
		t.Error("the adopted spec would not validate, so the report must block")
	}
}

func TestReadiness_PeerWithoutAllowedIPsFails_REQ_VAL_017(t *testing.T) {
	// The kernel permits a peer with no allowed-ips. It receives no traffic, and
	// allowed_ips is a required field of PeerSpec, so the adopted spec would be
	// invalid. Nothing rejected an empty list before REQ-VAL-017.
	n := fake.NewNode().AddInterface("wg0", "10.0.0.1/24", 51820)
	n.AddPeer("wg0", "peer-public-key-aaaa", "", "", false)

	i := only(t, build(t, n), "wg0")
	f := findingByCode(i, service.HintPeerNoAllowedIPs)
	if f == nil || f.Result != service.Fail {
		t.Fatalf("want a FAIL for a peer with no allowed-ips, got %+v", f)
	}
	if !i.Blocking() {
		t.Error("an unusable peer must block adoption")
	}
}

func TestReadiness_NonWireguardLinkFails_REQ_DIA_042(t *testing.T) {
	n := fake.NewNode().AddInterface("br0", "10.9.0.1/24", 0).WithLinkType("br0", "bridge")

	i := only(t, build(t, n), "br0")
	f := findingByCode(i, service.HintNotWireguard)
	if f == nil || f.Result != service.Fail {
		t.Fatalf("want a FAIL for a link that is not of type wireguard, got %+v", f)
	}
	if i.Spec != nil {
		t.Error("a link the resource model does not describe must carry no adoptable spec")
	}
}

func TestReadiness_CleanInterfacePasses_REQ_DIA_040(t *testing.T) {
	n := fake.NewNode().AddInterface("wg0", "10.0.0.1/24", 51820).
		WithUnit("wg0", platform.UnitAbsent)
	n.AddPeer("wg0", "peer-public-key-aaaa", "10.0.0.2/32", "", true)

	r := build(t, n)
	i := only(t, r, "wg0")
	if i.Blocking() || r.Blocking() {
		t.Fatalf("a clean interface must not block: %+v", i.Findings)
	}
	if findingByCode(i, service.HintReady) == nil {
		t.Error("a clean interface must carry a PASS naming the next action")
	}
}

func TestKey_StringRedacts_REQ_SEC_050(t *testing.T) {
	b := make([]byte, 32)
	for i := range b {
		b[i] = 7
	}
	k := platform.KeyFromBytes(b)
	if got := k.String(); got != "[redacted]" {
		t.Errorf("String must redact, got %q", got)
	}
	if !strings.Contains(k.Base64(), "Bwc") {
		t.Errorf("Base64 must still expose the value for the store, got %q", k.Base64())
	}
	var absent platform.Key
	if absent.String() != "" || absent.Present() {
		t.Error("an unset key must render empty and report absent")
	}
	// The kernel reads an unset key back as zeros, so KeyFromBytes reports one
	// absent. Without this, a device with no private key would look like a
	// device holding a key of zeros.
	if platform.KeyFromBytes(make([]byte, 32)).Present() {
		t.Error("an all-zero key must report absent")
	}
}
