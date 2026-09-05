// Package service holds the readiness report of SPEC-11 section 5.
//
// The report is a pure function over the platform ports, so it is testable
// without privilege: only the adapters under internal/platform touch the
// kernel or the filesystem.
package service

import (
	"fmt"
	"net/netip"
	"sort"
	"strings"

	"wg-agent/internal/platform"
)

// Result is the vocabulary REQ-DIA-002 fixes, which REQ-DIA-041 reuses rather
// than inventing a second one.
type Result string

const (
	Pass    Result = "PASS"
	Warn    Result = "WARN"
	Fail    Result = "FAIL"
	Unknown Result = "UNKNOWN"
)

// Ownership is the vocabulary of REQ-RES-017. Every value is decided from
// desired state and the deletion record, both of which the store holds —
// creation history is not among them, because the agent has no durable memory
// of it.
type Ownership string

const (
	Managed  Ownership = "MANAGED"
	Foreign  Ownership = "FOREIGN"
	Orphaned Ownership = "ORPHANED"
)

// Finding carries the fields REQ-DIA-002 requires, per REQ-DIA-041.
type Finding struct {
	Name     string `json:"name"`
	Result   Result `json:"result"`
	Observed string `json:"observed"`
	Expected string `json:"expected"`
	HintCode string `json:"hint_code"`
	Hint     string `json:"hint"`
}

// Hint codes. REQ-DIA-030 requires a stable identifier from a closed set, with
// hint as its English rendering, so that improving the wording is not a
// contract change.
const (
	HintUnitEnabled       = "WG_QUICK_UNIT_ENABLED"
	HintUnitUndetermined  = "WG_QUICK_UNIT_UNDETERMINED"
	HintHookDirective     = "WG_QUICK_HOOK_DIRECTIVE"
	HintUnownedDirective  = "WG_QUICK_UNOWNED_DIRECTIVE"
	HintConfigUnparseable = "WG_QUICK_CONFIG_UNPARSEABLE"
	HintPeerEndpoint      = "PEER_ENDPOINT_DISCARDED"
	HintNoAddress         = "INTERFACE_NO_ADDRESS"
	HintIPv6Address       = "INTERFACE_IPV6_ADDRESS"
	HintPeerIPv6          = "PEER_IPV6_ALLOWED_IPS"
	HintPeerNoAllowedIPs  = "PEER_NO_ALLOWED_IPS"
	HintPortCollision     = "LISTEN_PORT_COLLISION"
	HintAddressCollision  = "ADDRESS_COLLISION"
	HintNotWireguard      = "LINK_NOT_WIREGUARD"
	HintReady             = "ADOPTION_READY"
)

// AdoptablePeer is a peer as adoption would store it. REQ-DIA-047 forbids the
// preshared key itself, so only its presence appears.
type AdoptablePeer struct {
	PublicKey           string   `json:"public_key"`
	PresharedKeyPresent bool     `json:"preshared_key_present"`
	AllowedIPs          []string `json:"allowed_ips"`
	PersistentKeepalive int      `json:"persistent_keepalive_seconds"`
	// EndpointPresent records that the kernel holds an endpoint which
	// REQ-RCN-063 does not store. The value is omitted because it is not part
	// of the spec adoption would write.
	EndpointPresent bool `json:"endpoint_present"`
}

// AdoptableSpec is the spec adoption would read from the kernel. It carries
// only the fields the kernel supplies: forward_policy, nat and manage_routes
// come from the adoption request under REQ-RCN-066, so they are absent here.
//
// REQ-DIA-047 forbids the private key, so only its presence appears.
type AdoptableSpec struct {
	Name              string          `json:"name"`
	PrivateKeyPresent bool            `json:"private_key_present"`
	PublicKey         string          `json:"public_key"`
	ListenPort        int             `json:"listen_port"`
	Fwmark            uint32          `json:"fwmark"`
	Addresses         []string        `json:"addresses"`
	MTU               int             `json:"mtu"`
	Enabled           bool            `json:"enabled"`
	Peers             []AdoptablePeer `json:"peers"`
}

// InterfaceReadiness is one interface's entry in the report.
type InterfaceReadiness struct {
	Name      string         `json:"name"`
	Ownership Ownership      `json:"ownership"`
	Spec      *AdoptableSpec `json:"spec,omitempty"`
	Findings  []Finding      `json:"findings"`
}

// Blocking reports whether adoption of this interface is refused. REQ-RCN-064
// blocks on FAIL alone, so an UNKNOWN finding does not stop an operator.
func (i InterfaceReadiness) Blocking() bool {
	for _, f := range i.Findings {
		if f.Result == Fail {
			return true
		}
	}
	return false
}

// Report is the adoption readiness report of REQ-DIA-040.
type Report struct {
	Interfaces []InterfaceReadiness `json:"interfaces"`
}

// Blocking reports whether any interface carries a FAIL finding.
func (r Report) Blocking() bool {
	for _, i := range r.Interfaces {
		if i.Blocking() {
			return true
		}
	}
	return false
}

// Readiness builds the report of REQ-DIA-040.
type Readiness struct {
	Device  platform.Device
	Link    platform.Link
	HostFS  platform.HostFS
	Desired platform.DesiredState
}

// Build produces the report. REQ-DIA-005 forbids diagnostics from altering
// state, and nothing here writes.
func (r Readiness) Build() (Report, error) {
	names, err := r.Device.Names()
	if err != nil {
		return Report{}, fmt.Errorf("enumerate wireguard devices: %w", err)
	}
	sort.Strings(names)

	// Ports and subnets already held by another WireGuard interface on the host
	// are what REQ-VAL-013 and REQ-VAL-014 reject. Collect them first so a
	// collision between two foreign interfaces is reported, which the
	// managed-only reading would have missed.
	states := make(map[string]platform.LinkState, len(names))
	devices := make(map[string]platform.DeviceState, len(names))
	for _, n := range names {
		ls, err := r.Link.State(n)
		if err != nil {
			return Report{}, err
		}
		ds, err := r.Device.Snapshot(n)
		if err != nil {
			return Report{}, err
		}
		states[n] = ls
		devices[n] = ds
	}

	var rep Report
	for _, n := range names {
		entry, err := r.interfaceEntry(n, states, devices)
		if err != nil {
			return Report{}, err
		}
		rep.Interfaces = append(rep.Interfaces, entry)
	}
	return rep, nil
}

func (r Readiness) interfaceEntry(
	name string,
	states map[string]platform.LinkState,
	devices map[string]platform.DeviceState,
) (InterfaceReadiness, error) {
	out := InterfaceReadiness{Name: name, Ownership: ownershipOf(name, r.Desired)}

	// REQ-DIA-040 scopes the report to FOREIGN interfaces. A managed or
	// orphaned one is named so an operator sees the whole node, and carries no
	// findings because adoption does not apply to it.
	if out.Ownership != Foreign {
		return out, nil
	}

	ls := states[name]
	ds := devices[name]

	if ls.Type != "wireguard" {
		out.Findings = append(out.Findings, Finding{
			Name: "link_type", Result: Fail,
			Observed: ls.Type, Expected: "wireguard",
			HintCode: HintNotWireguard,
			Hint:     "The resource model describes WireGuard links only. This link cannot be adopted.",
		})
		return out, nil
	}

	out.Spec = specOf(name, ls, ds)
	out.Findings = append(out.Findings, r.unitFindings(name)...)
	out.Findings = append(out.Findings, r.configFindings(name)...)
	out.Findings = append(out.Findings, addressFindings(ls)...)
	out.Findings = append(out.Findings, collisionFindings(name, ls, states, devices)...)
	out.Findings = append(out.Findings, peerFindings(ds)...)

	if len(out.Findings) == 0 {
		out.Findings = append(out.Findings, Finding{
			Name: "adoption", Result: Pass,
			Observed: "no blocking or advisory finding", Expected: "none",
			HintCode: HintReady,
			Hint:     "Adopt this interface with `wg-agent adopt " + name + "`.",
		})
	}
	return out, nil
}

// ownershipOf decides REQ-RES-017's three values from the two facts the store
// holds. Order matters: desired state wins, then the deletion record.
func ownershipOf(name string, d platform.DesiredState) Ownership {
	switch {
	case d != nil && d.Describes(name):
		return Managed
	case d != nil && d.DeletionRecord(name):
		return Orphaned
	default:
		return Foreign
	}
}

func specOf(name string, ls platform.LinkState, ds platform.DeviceState) *AdoptableSpec {
	spec := &AdoptableSpec{
		Name:              name,
		PrivateKeyPresent: ds.PrivateKey.Present(),
		PublicKey:         ds.PublicKey,
		ListenPort:        ds.ListenPort,
		Fwmark:            ds.Fwmark,
		MTU:               ls.MTU,
		// REQ-RES-019: a WireGuard link reports its operational state as
		// unknown even while up, so the administrative flag is the source.
		Enabled: ls.AdminUp,
	}
	for _, a := range ls.Addresses {
		spec.Addresses = append(spec.Addresses, a.String())
	}
	for _, p := range ds.Peers {
		peer := AdoptablePeer{
			PublicKey:           p.PublicKey,
			PresharedKeyPresent: p.PresharedKey.Present(),
			PersistentKeepalive: int(p.PersistentKeepalive.Seconds()),
			EndpointPresent:     p.Endpoint != "",
		}
		for _, a := range p.AllowedIPs {
			peer.AllowedIPs = append(peer.AllowedIPs, a.String())
		}
		spec.Peers = append(spec.Peers, peer)
	}
	return spec
}

// unitFindings implements REQ-DIA-043 and REQ-DIA-050.
func (r Readiness) unitFindings(name string) []Finding {
	if r.HostFS == nil {
		return nil
	}
	state, err := r.HostFS.WgQuickUnit(name)
	if err != nil || state == platform.UnitUndetermined {
		return []Finding{{
			Name: "wg_quick_unit", Result: Unknown,
			Observed: "systemd unit directories could not be read",
			Expected: "no enabled wg-quick unit for this interface",
			HintCode: HintUnitUndetermined,
			Hint: "Confirm by hand that `wg-quick@" + name +
				"` is not enabled before adopting.",
		}}
	}
	if state == platform.UnitEnabled {
		return []Finding{{
			Name: "wg_quick_unit", Result: Fail,
			Observed: "wg-quick@" + name + " is enabled",
			Expected: "no enabled wg-quick unit for this interface",
			HintCode: HintUnitEnabled,
			Hint: "Run `systemctl disable wg-quick@" + name +
				"` and adopt again. Disabling does not stop the unit, so the interface keeps running.",
		}}
	}
	return nil
}

// configFindings implements REQ-DIA-044 and REQ-DIA-046.
func (r Readiness) configFindings(name string) []Finding {
	if r.HostFS == nil {
		return nil
	}
	cfg, err := r.HostFS.WgQuickConfig(name)
	if err != nil || !cfg.Present {
		return nil
	}

	var out []Finding
	if cfg.Unparseable {
		out = append(out, Finding{
			Name: "wg_quick_config", Result: Warn,
			Observed: cfg.Path + " could not be read",
			Expected: "a readable configuration, or none",
			HintCode: HintConfigUnparseable,
			Hint: "Adoption reads every field from the kernel, so this costs only the directive warnings below. " +
				"Read the file by hand if it may contain a PostUp rule.",
		})
	}
	for _, d := range cfg.Directives {
		f := Finding{
			Name:     "wg_quick_directive",
			Result:   Warn,
			Observed: d + " in " + cfg.Path,
			Expected: "no directive outside the resource model",
			HintCode: HintUnownedDirective,
			Hint:     "The agent has no equivalent for " + d + ". Its effect ends when wg-quick stops managing the interface.",
		}
		if isHook(d) {
			f.HintCode = HintHookDirective
			f.Hint = "The agent cannot reproduce " + d +
				". Rules it installs survive adoption and disappear at the next boot once wg-quick is disabled — reproduce them yourself before rebooting."
		}
		out = append(out, f)
	}
	return out
}

func isHook(d string) bool {
	switch d {
	case "PreUp", "PostUp", "PreDown", "PostDown":
		return true
	}
	return false
}

// addressFindings implements the REQ-VAL-016 and REQ-VAL-020 rows of the
// findings table. The rules themselves live in SPEC-07; this reports what they
// would reject.
func addressFindings(ls platform.LinkState) []Finding {
	var out []Finding
	if len(ls.Addresses) == 0 {
		out = append(out, Finding{
			Name: "addresses", Result: Fail,
			Observed: "none", Expected: "at least one IPv4 address",
			HintCode: HintNoAddress,
			Hint: "Assign an address with `ip addr add <cidr> dev " + ls.Name +
				"` before adopting, since REQ-VAL-016 rejects an empty list.",
		})
	}
	for _, a := range ls.Addresses {
		if a.Addr().Is6() {
			out = append(out, Finding{
				Name: "addresses", Result: Fail,
				Observed: a.String(), Expected: "IPv4 only",
				HintCode: HintIPv6Address,
				Hint: "Remove the IPv6 address with `ip addr del " + a.String() + " dev " + ls.Name +
					"`. IPv4 only in v1, per ADR-0005.",
			})
		}
	}
	return out
}

// collisionFindings implements the REQ-VAL-013 and REQ-VAL-014 rows. Both reach
// every WireGuard interface on the host, not only managed ones, and both
// exclude the interface being examined.
func collisionFindings(
	name string,
	ls platform.LinkState,
	states map[string]platform.LinkState,
	devices map[string]platform.DeviceState,
) []Finding {
	var out []Finding
	self := devices[name]

	for other, od := range devices {
		if other == name {
			continue
		}
		if self.ListenPort != 0 && od.ListenPort == self.ListenPort {
			out = append(out, Finding{
				Name: "listen_port", Result: Fail,
				Observed: fmt.Sprintf("port %d also held by %s", self.ListenPort, other),
				Expected: "a port no other WireGuard interface holds",
				HintCode: HintPortCollision,
				Hint:     "Change the listen port of one interface before adopting either.",
			})
		}
	}
	for other, os := range states {
		if other == name {
			continue
		}
		for _, a := range ls.Addresses {
			for _, b := range os.Addresses {
				if overlaps(a, b) {
					out = append(out, Finding{
						Name: "addresses", Result: Fail,
						Observed: fmt.Sprintf("%s overlaps %s on %s", a, b, other),
						Expected: "a subnet no other WireGuard interface carries",
						HintCode: HintAddressCollision,
						Hint:     "Renumber one of the two interfaces. Overlapping subnets need network namespaces, which v1 does not support.",
					})
				}
			}
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Observed < out[j].Observed })
	return out
}

func overlaps(a, b netip.Prefix) bool {
	return a.Overlaps(b)
}

// peerFindings implements REQ-DIA-048 and the peer rows of the findings table.
func peerFindings(ds platform.DeviceState) []Finding {
	var out []Finding
	var withEndpoint []string

	for _, p := range ds.Peers {
		if p.Endpoint != "" {
			withEndpoint = append(withEndpoint, short(p.PublicKey))
		}
		if len(p.AllowedIPs) == 0 {
			out = append(out, Finding{
				Name: "peer_allowed_ips", Result: Fail,
				Observed: short(p.PublicKey) + " carries no allowed-ips",
				Expected: "at least one IPv4 CIDR",
				HintCode: HintPeerNoAllowedIPs,
				Hint: "Give this peer an allowed-ips entry, or remove it with `wg set " + ds.Name +
					" peer <key> remove`. A peer with none receives no traffic, and REQ-VAL-017 " +
					"rejects the resulting spec.",
			})
		}
		for _, a := range p.AllowedIPs {
			if a.Addr().Is6() {
				out = append(out, Finding{
					Name: "peer_allowed_ips", Result: Fail,
					Observed: short(p.PublicKey) + " allows " + a.String(),
					Expected: "IPv4 only",
					HintCode: HintPeerIPv6,
					Hint: "Remove the IPv6 entry from this peer's AllowedIPs. REQ-VAL-020 rejects any " +
						"address of the IPv6 family, so the adopted spec would not validate.",
				})
			}
		}
	}

	if len(withEndpoint) > 0 {
		out = append(out, Finding{
			Name: "peer_endpoint", Result: Warn,
			Observed: "endpoint held for " + strings.Join(withEndpoint, ", "),
			Expected: "no configured endpoint, or one restated after adoption",
			HintCode: HintPeerEndpoint,
			Hint: "Adoption does not store a peer endpoint, because the kernel does not distinguish one it learned " +
				"from a handshake from one an operator set. Restate any static endpoint after adopting.",
		})
	}
	return out
}

// short renders a public key identifiably without printing all of it.
func short(publicKey string) string {
	if len(publicKey) <= 12 {
		return publicKey
	}
	return publicKey[:12] + "…"
}
