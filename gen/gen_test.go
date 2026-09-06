// Package gen holds no code. This file asserts that the generated contract
// carries the properties the requirements fix, because those are the ones
// REQ-API-061 freezes: a wrong field cardinality cannot be corrected within v1.
//
// The assertions read the generated Go rather than the .proto, so they check
// what a client actually receives.
package gen_test

import (
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

func read(t *testing.T, rel string) string {
	t.Helper()
	b, err := os.ReadFile(filepath.Join("..", rel))
	if err != nil {
		t.Fatalf("read %s: %v — run `make proto` first", rel, err)
	}
	return string(b)
}

// structOf returns the body of one generated struct.
func structOf(t *testing.T, src, name string) string {
	t.Helper()
	m := regexp.MustCompile(`(?s)type ` + name + ` struct.*?\n}`).FindString(src)
	if m == "" {
		t.Fatalf("struct %s not found in the generated code", name)
	}
	return m
}

func fieldType(t *testing.T, block, field string) string {
	t.Helper()
	m := regexp.MustCompile(`(?m)^\t` + field + `\s+(\S+)`).FindStringSubmatch(block)
	if m == nil {
		return ""
	}
	return m[1]
}

// REQ-API-076 — a field whose default differs from its zero value must be
// encoded so an absent value is distinguishable from that zero value. In
// generated Go that is a pointer. Without it a caller omitting `enabled` would
// take the link down, because REQ-API-064 replaces the whole spec.
func TestGenerated_DefaultBearingFieldsHavePresence_REQ_API_076(t *testing.T) {
	spec := structOf(t, read(t, "gen/go/wgagent/v1/resources.pb.go"), "InterfaceSpec")

	for _, f := range []string{"Mtu", "ManageRoutes", "Enabled"} {
		if got := fieldType(t, spec, f); !strings.HasPrefix(got, "*") {
			t.Errorf("InterfaceSpec.%s is %q; a default other than the zero value needs presence", f, got)
		}
	}
	// The converse: a field whose default is its zero value needs no marker,
	// which is the distinction SPEC-04 section 5.1 draws.
	for _, f := range []string{"ListenPort", "Fwmark"} {
		if got := fieldType(t, spec, f); strings.HasPrefix(got, "*") {
			t.Errorf("InterfaceSpec.%s is %q; its zero value is also its default", f, got)
		}
	}
}

// REQ-RES-034 — identity lives outside spec and status, so an interface
// returned with its spec absent is still addressable.
func TestGenerated_IdentityIsOutsideTheSpec_REQ_RES_034(t *testing.T) {
	res := read(t, "gen/go/wgagent/v1/resources.pb.go")

	if fieldType(t, structOf(t, res, "Interface"), "Name") != "string" {
		t.Error("Interface must carry Name")
	}
	if fieldType(t, structOf(t, res, "InterfaceSpec"), "Name") != "" {
		t.Error("InterfaceSpec must not carry Name")
	}

	peer := structOf(t, res, "Peer")
	for _, f := range []string{"InterfaceName", "PublicKey"} {
		if fieldType(t, peer, f) != "string" {
			t.Errorf("Peer must carry %s", f)
		}
	}
	spec := structOf(t, res, "PeerSpec")
	for _, f := range []string{"InterfaceName", "PublicKey"} {
		if fieldType(t, spec, f) != "" {
			t.Errorf("PeerSpec must not carry %s", f)
		}
	}
}

// REQ-RES-023 — null rather than an epoch-zero value when no handshake has
// occurred.
func TestGenerated_HandshakeAgeIsNullable_REQ_RES_023(t *testing.T) {
	status := structOf(t, read(t, "gen/go/wgagent/v1/resources.pb.go"), "PeerStatus")
	if got := fieldType(t, status, "HandshakeAgeSeconds"); !strings.HasPrefix(got, "*") {
		t.Errorf("PeerStatus.HandshakeAgeSeconds is %q; REQ-RES-023 requires null", got)
	}
}

// REQ-RCN-066 — adoption rejects a request that omits any of the three policy
// fields, which the wire format has to be able to express.
func TestGenerated_AdoptRequestCanExpressOmission_REQ_RCN_066(t *testing.T) {
	req := structOf(t, read(t, "gen/go/wgagent/v1/service.pb.go"), "AdoptInterfaceRequest")
	for _, f := range []string{"ForwardPolicy", "Nat", "ManageRoutes"} {
		if got := fieldType(t, req, f); !strings.HasPrefix(got, "*") {
			t.Errorf("AdoptInterfaceRequest.%s is %q; omission must be expressible", f, got)
		}
	}
}

// REQ-API-063 — every RPC in the service surface carries its REST mapping.
func TestGenerated_EveryRestRouteReachedTheGateway_REQ_API_063(t *testing.T) {
	gw := read(t, "gen/go/wgagent/v1/service.pb.gw.go")
	for _, route := range []string{
		"v1", "interfaces", "peers", "rotateKey", "adopt", "release",
		"status", "diagnose", "batchUpdate", "generateConfig",
		"keys", "reconcile", "version", "overview", "health",
	} {
		if !strings.Contains(gw, route) {
			t.Errorf("route segment %q absent from the gateway", route)
		}
	}
}

// WatchPeerStatus left the surface deliberately: nothing specified its request
// scope, its stream element or what emitted an event, and REQ-API-061 would
// freeze a wrong shape permanently. It returns with B-05.
func TestGenerated_WatchPeerStatusIsAbsent_REQ_API_010(t *testing.T) {
	for _, f := range []string{
		"gen/go/wgagent/v1/service.pb.go",
		"gen/go/wgagent/v1/service_grpc.pb.go",
		"gen/go/wgagent/v1/service.pb.gw.go",
	} {
		if strings.Contains(read(t, f), "WatchPeerStatus") {
			t.Errorf("%s names WatchPeerStatus, which is not specified", f)
		}
	}
}
