package reconcile

import (
	"fmt"
	"net/netip"
	"sort"
	"time"

	"wg-agent/internal/model"
	"wg-agent/internal/platform"
)

// This file holds the field-ownership rules of section 3 of SPEC-03. Every
// comparison here answers one question: has an agent-owned field drifted?
// A kernel-owned one is never compared, which is REQ-RCN-012.

// deviceDelta builds the device-level part of a Configure call — the
// agent-owned fields private_key, listen_port and fwmark.
//
// A nil field is one that already matches, so a pass over an interface in the
// state its spec asks for produces an empty delta and writes nothing. That is
// what REQ-RES-003 means by repeating a write leaving the same state.
func deviceDelta(spec model.InterfaceSpec, ds platform.DeviceState) (platform.DeviceConfig, bool, error) {
	var out platform.DeviceConfig
	changed := false

	if spec.PrivateKey != "" && spec.PrivateKey != ds.PrivateKey.Base64() {
		k, err := keyFromBase64(spec.PrivateKey)
		if err != nil {
			return out, false, fmt.Errorf("private key: %w", err)
		}
		out.PrivateKey = &k
		changed = true
	}
	if spec.ListenPort != 0 && spec.ListenPort != ds.ListenPort {
		p := spec.ListenPort
		out.ListenPort = &p
		changed = true
	}
	if spec.Fwmark != ds.Fwmark {
		m := spec.Fwmark
		out.Fwmark = &m
		changed = true
	}
	return out, changed, nil
}

// peerDelta diffs the stored peer set against the kernel's, by public key.
//
// Step 4 of REQ-RCN-022 names the three cases. REQ-RCN-023 forbids whole-list
// replacement here, so removals are explicit and every update carries
// UpdateOnly: a delta must not resurrect a peer another writer removed between
// the snapshot and the write.
func peerDelta(peers []model.Peer, ds platform.DeviceState) ([]platform.PeerConfig, error) {
	kernel := make(map[string]platform.PeerState, len(ds.Peers))
	for _, p := range ds.Peers {
		kernel[p.PublicKey] = p
	}
	desired := make(map[string]struct{}, len(peers))

	var out []platform.PeerConfig
	for _, want := range peers {
		desired[want.PublicKey] = struct{}{}

		allowed, err := parseNetworks(want.Spec.AllowedIPs)
		if err != nil {
			return nil, fmt.Errorf("peer %s allowed_ips: %w", want.PublicKey, err)
		}
		keepalive := time.Duration(want.Spec.PersistentKeepalive) * time.Second

		have, present := kernel[want.PublicKey]
		if !present {
			// Creation is the one moment REQ-RCN-013 permits an endpoint.
			pc := platform.PeerConfig{
				PublicKey:         want.PublicKey,
				AllowedIPs:        allowed,
				ReplaceAllowedIPs: true,
			}
			if err := setPSK(&pc, want.Spec.PresharedKey); err != nil {
				return nil, fmt.Errorf("peer %s: %w", want.PublicKey, err)
			}
			if want.Spec.Endpoint != "" {
				e := want.Spec.Endpoint
				pc.Endpoint = &e
			}
			if keepalive != 0 {
				pc.PersistentKeepalive = &keepalive
			}
			out = append(out, pc)
			continue
		}

		// Present in both. Compare the agent-owned fields alone; the endpoint
		// is kernel-owned and REQ-RCN-051 forbids reconcile from writing it.
		pc := platform.PeerConfig{PublicKey: want.PublicKey, UpdateOnly: true}
		drifted := false

		if !samePrefixes(allowed, have.AllowedIPs) {
			pc.AllowedIPs = allowed
			pc.ReplaceAllowedIPs = true
			drifted = true
		}
		if want.Spec.PresharedKey != have.PresharedKey.Base64() {
			if err := setPSK(&pc, want.Spec.PresharedKey); err != nil {
				return nil, fmt.Errorf("peer %s: %w", want.PublicKey, err)
			}
			drifted = true
		}
		if keepalive != have.PersistentKeepalive {
			k := keepalive
			pc.PersistentKeepalive = &k
			drifted = true
		}
		if drifted {
			out = append(out, pc)
		}
	}

	// Present in the kernel only. Sorted so a pass is deterministic, which is
	// what lets a test assert the recorded call sequence.
	var extra []string
	for k := range kernel {
		if _, ok := desired[k]; !ok {
			extra = append(extra, k)
		}
	}
	sort.Strings(extra)
	for _, k := range extra {
		out = append(out, platform.PeerConfig{PublicKey: k, Remove: true})
	}
	return out, nil
}

// setPSK writes the preshared key, translating an empty spec value into an
// absent one. The kernel has no "unset" message for a preshared key, so the
// adapter sends zeros; an empty spec field on a peer the kernel holds a key for
// has to say that rather than say nothing, or the key would survive its removal
// from desired state.
func setPSK(pc *platform.PeerConfig, b64 string) error {
	if b64 == "" {
		var absent platform.Key
		pc.PresharedKey = &absent
		return nil
	}
	k, err := keyFromBase64(b64)
	if err != nil {
		return fmt.Errorf("preshared key: %w", err)
	}
	pc.PresharedKey = &k
	return nil
}

// addrDelta returns the addresses to add and to remove. Addresses are
// agent-owned, so both directions apply — REQ-RCN-011.
func addrDelta(spec model.InterfaceSpec, ls platform.LinkState) (add, del []netip.Prefix, err error) {
	want, err := parseAddrs(spec.Addresses)
	if err != nil {
		return nil, nil, fmt.Errorf("addresses: %w", err)
	}
	return diffPrefixes(want, ls.Addresses)
}

// routeDelta returns the routes to add and to remove, where the desired set is
// the union of every peer's allowed_ips — step 8 of REQ-RCN-022.
func routeDelta(peers []model.Peer, have []netip.Prefix) (add, del []netip.Prefix, err error) {
	seen := map[netip.Prefix]struct{}{}
	var want []netip.Prefix
	for _, p := range peers {
		ps, err := parseNetworks(p.Spec.AllowedIPs)
		if err != nil {
			return nil, nil, fmt.Errorf("peer %s allowed_ips: %w", p.PublicKey, err)
		}
		for _, x := range ps {
			if _, ok := seen[x]; ok {
				continue
			}
			seen[x] = struct{}{}
			want = append(want, x)
		}
	}
	return diffPrefixes(want, have)
}

// ── helpers ─────────────────────────────────────────────────────────────────

func diffPrefixes(want, have []netip.Prefix) (add, del []netip.Prefix, err error) {
	h := map[netip.Prefix]struct{}{}
	for _, p := range have {
		h[p] = struct{}{}
	}
	w := map[netip.Prefix]struct{}{}
	for _, p := range want {
		w[p] = struct{}{}
		if _, ok := h[p]; !ok {
			add = append(add, p)
		}
	}
	for _, p := range have {
		if _, ok := w[p]; !ok {
			del = append(del, p)
		}
	}
	sortPrefixes(add)
	sortPrefixes(del)
	return add, del, nil
}

// parseAddrs parses interface addresses. The host bits are kept: `10.0.0.1/24`
// is the address to assign, and masking it to `10.0.0.0/24` would put the
// network address on the interface instead.
func parseAddrs(ss []string) ([]netip.Prefix, error) { return parse(ss, false) }

// parseNetworks parses allowed_ips and routes, masking off the host bits. Both
// the WireGuard device and the routing table normalise a prefix that way, so an
// unmasked value would read back different from what was written and drift on
// every pass.
func parseNetworks(ss []string) ([]netip.Prefix, error) { return parse(ss, true) }

func parse(ss []string, mask bool) ([]netip.Prefix, error) {
	out := make([]netip.Prefix, 0, len(ss))
	for _, s := range ss {
		p, err := netip.ParsePrefix(s)
		if err != nil {
			return nil, fmt.Errorf("parse %q: %w", s, err)
		}
		if mask {
			p = p.Masked()
		}
		out = append(out, p)
	}
	return out, nil
}

func samePrefixes(a, b []netip.Prefix) bool {
	if len(a) != len(b) {
		return false
	}
	x := append([]netip.Prefix(nil), a...)
	y := append([]netip.Prefix(nil), b...)
	sortPrefixes(x)
	sortPrefixes(y)
	for i := range x {
		if x[i] != y[i] {
			return false
		}
	}
	return true
}

func sortPrefixes(ps []netip.Prefix) {
	sort.Slice(ps, func(i, j int) bool { return ps[i].String() < ps[j].String() })
}

func keyFromBase64(s string) (platform.Key, error) { return platform.ParseKey(s) }
