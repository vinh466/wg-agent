package service_test

import (
	"encoding/base64"
	"encoding/json"
	"strings"
	"testing"

	"wg-agent/internal/model"
	"wg-agent/internal/platform/fake"
	"wg-agent/internal/service"
	"wg-agent/internal/validate"
)

func keyOf(seed byte) string {
	b := make([]byte, 32)
	for i := range b {
		b[i] = seed
	}
	return base64.StdEncoding.EncodeToString(b)
}

func newPeer(pub, allowed string) model.Peer {
	return model.Peer{
		PublicKey: pub,
		Spec:      model.PeerSpec{AllowedIPs: []string{allowed}},
	}
}

func mustJSON(t *testing.T, v any) string {
	t.Helper()
	b, err := json.Marshal(v)
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}
	return string(b)
}

func contains(s, sub string) bool { return strings.Contains(s, sub) }

// withInterface returns a wired service layer holding one managed interface.
func withInterface(t *testing.T) (service.Interfaces, service.Peers, *fake.Node) {
	t.Helper()
	n := fake.NewNode()
	ifaces, peers, _ := wire(t, n)
	if _, err := ifaces.Create("wg0", newSpec()); err != nil {
		t.Fatalf("create interface: %v", err)
	}
	return ifaces, peers, n
}

// A create stores the peer and applies it to the kernel.
func TestPeers_CreateAppliesToTheKernel_REQ_RCN_020(t *testing.T) {
	_, peers, n := withInterface(t)

	got, err := peers.Create("wg0", newPeer(keyOf(9), "10.100.0.2/32"))
	if err != nil {
		t.Fatalf("create: %v", err)
	}
	if got.PublicKey != keyOf(9) || got.InterfaceName != "wg0" {
		t.Errorf("identity = %+v", got)
	}
	if got.Status.Revision == "" {
		t.Error("no revision was produced")
	}
	if len(n.Devices["wg0"].Peers) != 1 {
		t.Fatalf("the kernel holds %d peers, want 1", len(n.Devices["wg0"].Peers))
	}
	if n.Devices["wg0"].Peers[0].PublicKey != keyOf(9) {
		t.Error("the wrong peer reached the kernel")
	}
}

// REQ-API-071: a repeated create is a mistake far more often than an intent to
// replace.
func TestPeers_CreateRefusesADuplicate_REQ_API_071(t *testing.T) {
	_, peers, _ := withInterface(t)
	p := newPeer(keyOf(9), "10.100.0.2/32")

	if _, err := peers.Create("wg0", p); err != nil {
		t.Fatalf("first create: %v", err)
	}
	_, err := peers.Create("wg0", p)
	if got := reasonOf(t, err); got != service.ReasonPeerExists {
		t.Errorf("reason = %q, want PEER_EXISTS", got)
	}
}

// REQ-API-070: an update to a peer desired state does not describe is refused.
func TestPeers_UpdateRefusesAnUnknownPeer_REQ_API_070(t *testing.T) {
	_, peers, _ := withInterface(t)

	_, err := peers.Update("wg0", newPeer(keyOf(7), "10.100.0.7/32"))
	if got := reasonOf(t, err); got != service.ReasonPeerNotFound {
		t.Errorf("reason = %q, want PEER_NOT_FOUND", got)
	}
}

// REQ-API-070 for a delete, so a repeated delete reports the absence rather
// than succeeding twice.
func TestPeers_DeleteRefusesAnUnknownPeer_REQ_API_070(t *testing.T) {
	_, peers, _ := withInterface(t)
	p := newPeer(keyOf(9), "10.100.0.2/32")

	if _, err := peers.Create("wg0", p); err != nil {
		t.Fatalf("create: %v", err)
	}
	if err := peers.Delete("wg0", p.PublicKey); err != nil {
		t.Fatalf("delete: %v", err)
	}
	if got := reasonOf(t, peers.Delete("wg0", p.PublicKey)); got != service.ReasonPeerNotFound {
		t.Errorf("reason = %q, want PEER_NOT_FOUND", got)
	}
}

// A delete reaches the kernel: step 4 of REQ-RCN-022 removes what the store no
// longer describes.
func TestPeers_DeleteReachesTheKernel_REQ_RCN_022(t *testing.T) {
	_, peers, n := withInterface(t)
	p := newPeer(keyOf(9), "10.100.0.2/32")

	if _, err := peers.Create("wg0", p); err != nil {
		t.Fatalf("create: %v", err)
	}
	if err := peers.Delete("wg0", p.PublicKey); err != nil {
		t.Fatalf("delete: %v", err)
	}
	if len(n.Devices["wg0"].Peers) != 0 {
		t.Errorf("the kernel still holds %d peers", len(n.Devices["wg0"].Peers))
	}
}

// An update applies the change and keeps a preshared key the caller omitted:
// it is write-only under REQ-RES-022, so the caller could not send it back.
func TestPeers_UpdateKeepsTheStoredPresharedKey_REQ_RES_022(t *testing.T) {
	_, peers, n := withInterface(t)

	p := newPeer(keyOf(9), "10.100.0.2/32")
	p.Spec.PresharedKey = keyOf(0x55)
	if _, err := peers.Create("wg0", p); err != nil {
		t.Fatalf("create: %v", err)
	}

	next := newPeer(keyOf(9), "10.100.0.3/32") // no preshared key
	got, err := peers.Update("wg0", next)
	if err != nil {
		t.Fatalf("update: %v", err)
	}
	if !got.Spec.PresharedKeyPresent {
		t.Error("the stored preshared key was dropped")
	}
	if n.Devices["wg0"].Peers[0].PresharedKey.Base64() != keyOf(0x55) {
		t.Error("the kernel lost the preshared key")
	}
	if len(n.Devices["wg0"].Peers[0].AllowedIPs) != 1 ||
		n.Devices["wg0"].Peers[0].AllowedIPs[0].String() != "10.100.0.3/32" {
		t.Errorf("allowed_ips not applied: %v", n.Devices["wg0"].Peers[0].AllowedIPs)
	}
}

// REQ-RES-022: no response carries a preshared key, only its presence.
func TestPeers_ResponseNeverCarriesThePresharedKey_REQ_RES_022(t *testing.T) {
	_, peers, _ := withInterface(t)

	p := newPeer(keyOf(9), "10.100.0.2/32")
	p.Spec.PresharedKey = keyOf(0x55)
	got, err := peers.Create("wg0", p)
	if err != nil {
		t.Fatalf("create: %v", err)
	}
	blob := mustJSON(t, got)
	if contains(blob, keyOf(0x55)) {
		t.Error("the response carries the preshared key")
	}
	if !got.Spec.PresharedKeyPresent {
		t.Error("its presence must still be reported")
	}
}

// REQ-VAL-012 across the stored set: a peer claiming an entry another already
// holds is refused, because cryptokey routing would be ambiguous.
func TestPeers_DuplicateAllowedIPsRefused_REQ_VAL_012(t *testing.T) {
	_, peers, _ := withInterface(t)

	if _, err := peers.Create("wg0", newPeer(keyOf(8), "10.100.0.2/32")); err != nil {
		t.Fatalf("first: %v", err)
	}
	_, err := peers.Create("wg0", newPeer(keyOf(9), "10.100.0.2/32"))
	if err == nil {
		t.Fatal("the duplicate was accepted")
	}
	var ve *validate.Error
	if !asVErr(err, &ve) || ve.Reason() != validate.ReasonAllowedIPsDup {
		t.Errorf("error = %v, want ALLOWED_IPS_DUPLICATE", err)
	}
}

// REQ-VAL-011: a public key that is not base64 of 32 bytes never reaches the
// store.
func TestPeers_InvalidPublicKeyRefused_REQ_VAL_011(t *testing.T) {
	_, peers, n := withInterface(t)

	_, err := peers.Create("wg0", newPeer("not-a-key", "10.100.0.2/32"))
	if err == nil {
		t.Fatal("an invalid public key was accepted")
	}
	if len(n.Devices["wg0"].Peers) != 0 {
		t.Error("the kernel was touched")
	}
}

// A peer on an interface desired state does not describe cannot be stored:
// REQ-RCN-030 leaves a foreign link alone.
func TestPeers_RefusesAForeignInterface_REQ_RCN_030(t *testing.T) {
	n := fake.NewNode().AddInterface("wg1", "10.101.0.1/24", 51821)
	_, peers, _ := wire(t, n)

	_, err := peers.Create("wg1", newPeer(keyOf(9), "10.101.0.2/32"))
	if got := reasonOf(t, err); got != service.ReasonInterfaceNotManaged {
		t.Errorf("reason = %q, want INTERFACE_NOT_MANAGED", got)
	}
}

func TestPeers_ListAndGet_REQ_RES_020(t *testing.T) {
	_, peers, _ := withInterface(t)

	for i, allowed := range []string{"10.100.0.2/32", "10.100.0.3/32"} {
		if _, err := peers.Create("wg0", newPeer(keyOf(byte(8+i)), allowed)); err != nil {
			t.Fatalf("create %d: %v", i, err)
		}
	}
	list, err := peers.List("wg0")
	if err != nil {
		t.Fatalf("list: %v", err)
	}
	if len(list) != 2 {
		t.Fatalf("list has %d entries, want 2", len(list))
	}

	got, err := peers.Get("wg0", keyOf(9))
	if err != nil {
		t.Fatalf("get: %v", err)
	}
	if got.PublicKey != keyOf(9) {
		t.Errorf("get returned %q", got.PublicKey)
	}
	if _, err := peers.Get("wg0", keyOf(1)); err == nil {
		t.Error("get of an unknown peer must fail")
	}
}

func asVErr(err error, target **validate.Error) bool {
	if e, ok := err.(*validate.Error); ok {
		*target = e
		return true
	}
	return false
}
