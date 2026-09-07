package service_test

import (
	"strings"
	"testing"
	"time"

	"wg-agent/internal/model"
	"wg-agent/internal/platform"
	"wg-agent/internal/platform/fake"
	"wg-agent/internal/service"
)

var fixedNow = time.Date(2026, 9, 7, 12, 0, 0, 0, time.UTC)

// runtimeOver wires the runtime reader with a fixed clock, so a handshake age
// is an assertion rather than a race with the wall clock.
func runtimeOver(ifaces service.Interfaces) service.Runtime {
	return service.Runtime{
		Interfaces: ifaces,
		Now:        func() time.Time { return fixedNow },
	}
}

// setHandshake stamps a peer the way the kernel would.
func setHandshake(n *fake.Node, iface, pub string, at time.Time, rx, tx int64) {
	d := n.Devices[iface]
	for i := range d.Peers {
		if d.Peers[i].PublicKey == pub {
			d.Peers[i].LastHandshake = at
			d.Peers[i].ReceiveBytes = rx
			d.Peers[i].TransmitBytes = tx
			d.Peers[i].ProtocolVersion = 1
		}
	}
	n.Devices[iface] = d
}

// REQ-RES-025: online is the handshake being recent, and the threshold
// defaults to 180 seconds.
func TestRuntime_OnlineFromHandshakeAge_REQ_RES_025(t *testing.T) {
	pub := keyOf(9)
	cases := []struct {
		name       string
		ago        time.Duration
		wantOnline bool
		wantAge    int64
	}{
		{"just now", 0, true, 0},
		{"inside the threshold", 179 * time.Second, true, 179},
		{"at the threshold", 180 * time.Second, false, 180},
		{"well past it", 10 * time.Minute, false, 600},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			_, ps, node := withInterface(t)
			if _, err := ps.Create("wg0", newPeer(pub, "10.100.0.2/32")); err != nil {
				t.Fatalf("create peer: %v", err)
			}
			setHandshake(node, "wg0", pub, fixedNow.Add(-c.ago), 100, 200)

			rt := runtimeOver(ps.Interfaces)
			got, err := rt.ListPeerStatus("wg0")
			if err != nil {
				t.Fatalf("list: %v", err)
			}
			if len(got) != 1 {
				t.Fatalf("got %d entries, want 1", len(got))
			}
			if got[0].Online != c.wantOnline {
				t.Errorf("online = %v, want %v", got[0].Online, c.wantOnline)
			}
			if got[0].HandshakeAgeSeconds == nil {
				t.Fatal("handshake_age_seconds must be set once a handshake happened")
			}
			if *got[0].HandshakeAgeSeconds != c.wantAge {
				t.Errorf("age = %d, want %d", *got[0].HandshakeAgeSeconds, c.wantAge)
			}
		})
	}
}

// A peer that has never handshaked reports a null age rather than a zero one,
// which is the distinction SPEC-01 section 4.3 draws.
func TestRuntime_NoHandshakeIsNullAge_REQ_RES_025(t *testing.T) {
	_, peers, _ := withInterface(t)
	if _, err := peers.Create("wg0", newPeer(keyOf(9), "10.100.0.2/32")); err != nil {
		t.Fatalf("create peer: %v", err)
	}

	got, err := runtimeOver(peers.Interfaces).ListPeerStatus("wg0")
	if err != nil {
		t.Fatalf("list: %v", err)
	}
	if got[0].HandshakeAgeSeconds != nil {
		t.Errorf("age = %d, want null", *got[0].HandshakeAgeSeconds)
	}
	if got[0].Online {
		t.Error("a peer with no handshake is not online")
	}
}

// The configured threshold replaces the default — peer_online_threshold in
// SPEC-09.
func TestRuntime_ConfiguredThresholdApplies_REQ_RES_025(t *testing.T) {
	_, peers, n := withInterface(t)
	pub := keyOf(9)
	if _, err := peers.Create("wg0", newPeer(pub, "10.100.0.2/32")); err != nil {
		t.Fatalf("create peer: %v", err)
	}
	setHandshake(n, "wg0", pub, fixedNow.Add(-90*time.Second), 0, 0)

	rt := service.Runtime{
		Interfaces:      peers.Interfaces,
		OnlineThreshold: 60 * time.Second,
		Now:             func() time.Time { return fixedNow },
	}
	got, err := rt.ListPeerStatus("wg0")
	if err != nil {
		t.Fatalf("list: %v", err)
	}
	if got[0].Online {
		t.Error("90s must be offline against a 60s threshold")
	}
}

// REQ-RES-024: the counters and the resolved endpoint come from the kernel, so
// a peer the store does not describe is still reported.
func TestRuntime_ReadsFromTheKernel_REQ_RES_024(t *testing.T) {
	n := fake.NewNode()
	ifaces, peers, _ := wire(t, n)
	if _, err := ifaces.Create("wg0", newSpec()); err != nil {
		t.Fatalf("create: %v", err)
	}
	if _, err := peers.Create("wg0", newPeer(keyOf(9), "10.100.0.2/32")); err != nil {
		t.Fatalf("create peer: %v", err)
	}
	setHandshake(n, "wg0", keyOf(9), fixedNow.Add(-30*time.Second), 4096, 8192)

	// A peer another writer added, absent from the store.
	d := n.Devices["wg0"]
	d.Peers = append(d.Peers, platform.PeerState{
		PublicKey:     keyOf(3),
		Endpoint:      "203.0.113.55:51820",
		LastHandshake: fixedNow.Add(-5 * time.Second),
		ReceiveBytes:  7,
	})
	n.Devices["wg0"] = d

	got, err := runtimeOver(ifaces).ListPeerStatus("wg0")
	if err != nil {
		t.Fatalf("list: %v", err)
	}
	if len(got) != 2 {
		t.Fatalf("got %d entries, want both peers the kernel holds", len(got))
	}

	byKey := map[string]service.PeerRuntime{}
	for _, p := range got {
		byKey[p.PublicKey] = p
	}
	stored := byKey[keyOf(9)]
	if stored.RxBytes != 4096 || stored.TxBytes != 8192 {
		t.Errorf("counters = %d/%d, want 4096/8192", stored.RxBytes, stored.TxBytes)
	}
	if stored.Revision == "" {
		t.Error("a stored peer must carry its revision")
	}

	foreign := byKey[keyOf(3)]
	if foreign.ResolvedEndpoint != "203.0.113.55:51820" {
		t.Errorf("resolved_endpoint = %q", foreign.ResolvedEndpoint)
	}
	if foreign.Revision != "" {
		t.Error("a peer the store does not describe has no revision to report")
	}
}

func TestRuntime_UnknownInterface_REQ_RCN_067(t *testing.T) {
	n := fake.NewNode()
	ifaces, _, _ := wire(t, n)

	_, err := runtimeOver(ifaces).ListPeerStatus("wg9")
	if got := reasonOf(t, err); got != service.ReasonInterfaceNotFound {
		t.Errorf("reason = %q, want INTERFACE_NOT_FOUND", got)
	}
}

// ── client configuration ────────────────────────────────────────────────────

func configsOver(ifaces service.Interfaces, endpoint string) service.ClientConfigs {
	return service.ClientConfigs{Interfaces: ifaces, NodeEndpoint: endpoint}
}

// REQ-KEY-038: no address the agent could pick would be right, so a request
// with no endpoint and no configured one is refused.
func TestClientConfig_EndpointRequired_REQ_KEY_038(t *testing.T) {
	_, peers, _ := withInterface(t)
	if _, err := peers.Create("wg0", newPeer(keyOf(9), "10.100.0.2/32")); err != nil {
		t.Fatalf("create peer: %v", err)
	}

	_, err := configsOver(peers.Interfaces, "").Generate(service.ClientConfigRequest{
		Interface: "wg0", PublicKey: keyOf(9),
	})
	if got := reasonOf(t, err); got != service.ReasonEndpointRequired {
		t.Errorf("reason = %q, want ENDPOINT_REQUIRED", got)
	}
}

// REQ-KEY-037: the request first, then node.endpoint.
func TestClientConfig_EndpointPrecedence_REQ_KEY_037(t *testing.T) {
	_, peers, _ := withInterface(t)
	if _, err := peers.Create("wg0", newPeer(keyOf(9), "10.100.0.2/32")); err != nil {
		t.Fatalf("create peer: %v", err)
	}
	cfgs := configsOver(peers.Interfaces, "node.example:51820")

	fromConfig, err := cfgs.Generate(service.ClientConfigRequest{
		Interface: "wg0", PublicKey: keyOf(9),
	})
	if err != nil {
		t.Fatalf("generate: %v", err)
	}
	if !strings.Contains(fromConfig.Config, "Endpoint = node.example:51820") {
		t.Errorf("the configured endpoint was not used:\n%s", fromConfig.Config)
	}

	fromRequest, err := cfgs.Generate(service.ClientConfigRequest{
		Interface: "wg0", PublicKey: keyOf(9), Endpoint: "asked.example:51821",
	})
	if err != nil {
		t.Fatalf("generate: %v", err)
	}
	if !strings.Contains(fromRequest.Config, "Endpoint = asked.example:51821") {
		t.Errorf("the request endpoint must win:\n%s", fromRequest.Config)
	}
}

// REQ-KEY-031, the AUTO rule, one arm at a time.
func TestClientConfig_AutoDerivation_REQ_KEY_031(t *testing.T) {
	cases := []struct {
		name  string
		intra model.Axis
		want  []string
	}{
		{"intra deny", model.Deny, []string{"10.100.0.1/32"}},
		{"intra allow", model.Allow, []string{"10.100.0.1/32", "10.100.0.0/24"}},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			n := fake.NewNode()
			ifaces, peers, _ := wire(t, n)
			spec := newSpec()
			spec.ForwardPolicy = model.ForwardPolicySpec{
				IntraInterface: c.intra, InterInterface: model.Deny, External: model.Deny,
			}
			if _, err := ifaces.Create("wg0", spec); err != nil {
				t.Fatalf("create: %v", err)
			}
			if _, err := peers.Create("wg0", newPeer(keyOf(9), "10.100.0.2/32")); err != nil {
				t.Fatalf("create peer: %v", err)
			}

			got, err := configsOver(ifaces, "node.example:51820").
				Generate(service.ClientConfigRequest{Interface: "wg0", PublicKey: keyOf(9)})
			if err != nil {
				t.Fatalf("generate: %v", err)
			}
			if len(got.DerivedAllowedIPs) != len(c.want) {
				t.Fatalf("derived = %v, want %v", got.DerivedAllowedIPs, c.want)
			}
			for i, w := range c.want {
				if got.DerivedAllowedIPs[i] != w {
					t.Errorf("derived[%d] = %q, want %q", i, got.DerivedAllowedIPs[i], w)
				}
			}
			// REQ-KEY-033 — the explanation says how the result was reached.
			if len(got.RoutingExplanation) == 0 {
				t.Error("no routing explanation")
			}
		})
	}
}

// REQ-KEY-032: a default route subsumes the narrower entries, which are
// omitted rather than left as noise.
func TestClientConfig_DefaultRouteSubsumes_REQ_KEY_032(t *testing.T) {
	n := fake.NewNode()
	ifaces, peers, _ := wire(t, n)
	spec := newSpec()
	spec.ForwardPolicy = model.ForwardPolicySpec{
		IntraInterface: model.Allow, InterInterface: model.Deny, External: model.Allow,
	}
	spec.NAT = model.NatSpec{Enabled: true, EnableUplinkForwarding: true}
	if _, err := ifaces.Create("wg0", spec); err != nil {
		t.Fatalf("create: %v", err)
	}
	if _, err := peers.Create("wg0", newPeer(keyOf(9), "10.100.0.2/32")); err != nil {
		t.Fatalf("create peer: %v", err)
	}

	got, err := configsOver(ifaces, "node.example:51820").
		Generate(service.ClientConfigRequest{Interface: "wg0", PublicKey: keyOf(9)})
	if err != nil {
		t.Fatalf("generate: %v", err)
	}
	if len(got.DerivedAllowedIPs) != 1 || got.DerivedAllowedIPs[0] != "0.0.0.0/0" {
		t.Errorf("derived = %v, want [0.0.0.0/0] alone", got.DerivedAllowedIPs)
	}
}

// The explicit modes of REQ-KEY-030.
func TestClientConfig_ExplicitModes_REQ_KEY_030(t *testing.T) {
	_, peers, _ := withInterface(t)
	if _, err := peers.Create("wg0", newPeer(keyOf(9), "10.100.0.2/32")); err != nil {
		t.Fatalf("create peer: %v", err)
	}
	cfgs := configsOver(peers.Interfaces, "node.example:51820")

	for mode, want := range map[service.RoutingMode]string{
		service.RoutingServerOnly:  "10.100.0.1/32",
		service.RoutingLocalSubnet: "10.100.0.0/24",
		service.RoutingFullTunnel:  "0.0.0.0/0",
	} {
		got, err := cfgs.Generate(service.ClientConfigRequest{
			Interface: "wg0", PublicKey: keyOf(9), Mode: mode,
		})
		if err != nil {
			t.Fatalf("%s: %v", mode, err)
		}
		if len(got.DerivedAllowedIPs) != 1 || got.DerivedAllowedIPs[0] != want {
			t.Errorf("%s derived %v, want [%s]", mode, got.DerivedAllowedIPs, want)
		}
	}
}

// REQ-KEY-035 and REQ-KEY-036: the wg-quick format, with an Endpoint. Without
// one the file follows the format and reaches nothing.
func TestClientConfig_WgQuickFormat_REQ_KEY_035(t *testing.T) {
	_, peers, _ := withInterface(t)
	p := newPeer(keyOf(9), "10.100.0.2/32")
	p.Spec.PersistentKeepalive = 25
	if _, err := peers.Create("wg0", p); err != nil {
		t.Fatalf("create peer: %v", err)
	}

	got, err := configsOver(peers.Interfaces, "node.example:51820").
		Generate(service.ClientConfigRequest{
			Interface: "wg0", PublicKey: keyOf(9),
			DNS: []string{"10.100.0.1"},
		})
	if err != nil {
		t.Fatalf("generate: %v", err)
	}
	for _, want := range []string{
		"[Interface]",
		"Address = 10.100.0.2/32",
		"DNS = 10.100.0.1",
		"[Peer]",
		"PublicKey = ",
		"AllowedIPs = ",
		"Endpoint = node.example:51820",
		"PersistentKeepalive = 25",
	} {
		if !strings.Contains(got.Config, want) {
			t.Errorf("the file lacks %q:\n%s", want, got.Config)
		}
	}
	if got.FileName == "" {
		t.Error("no file name")
	}
}

// REQ-KEY-002: the interface private key never reaches the generated file. The
// client's own key does, when the caller supplies one.
func TestClientConfig_NeverCarriesTheInterfaceKey_REQ_KEY_002(t *testing.T) {
	n := fake.NewNode()
	ifaces, peers, _ := wire(t, n)
	spec := newSpec()
	spec.PrivateKey = keyOf(0x44)
	if _, err := ifaces.Create("wg0", spec); err != nil {
		t.Fatalf("create: %v", err)
	}
	if _, err := peers.Create("wg0", newPeer(keyOf(9), "10.100.0.2/32")); err != nil {
		t.Fatalf("create peer: %v", err)
	}

	got, err := configsOver(ifaces, "node.example:51820").
		Generate(service.ClientConfigRequest{Interface: "wg0", PublicKey: keyOf(9)})
	if err != nil {
		t.Fatalf("generate: %v", err)
	}
	if strings.Contains(got.Config, keyOf(0x44)) {
		t.Errorf("the interface private key reached the file:\n%s", got.Config)
	}
}

func TestClientConfig_UnknownPeer_REQ_API_070(t *testing.T) {
	_, peers, _ := withInterface(t)

	_, err := configsOver(peers.Interfaces, "node.example:51820").
		Generate(service.ClientConfigRequest{Interface: "wg0", PublicKey: keyOf(1)})
	if got := reasonOf(t, err); got != service.ReasonPeerNotFound {
		t.Errorf("reason = %q, want PEER_NOT_FOUND", got)
	}
}
