// Package platform declares the ports the core depends on and the types that
// cross them. Adapters under this directory implement them against the kernel;
// package fake implements them in memory.
//
// The dependency points inwards: nothing here imports netlink or wgctrl, so a
// caller above this layer can be tested without privilege. The library split is
// the one docs/00-overview/architecture.md fixes — netlink owns the link,
// wgctrl owns the device.
package platform

import (
	"encoding/base64"
	"net/netip"
	"time"
)

// Key is a WireGuard key. Its String method redacts, so a key cannot reach a
// log line, an audit record or an API response through ordinary formatting —
// REQ-SEC-050. Reaching the value is deliberately explicit.
type Key struct {
	b       [32]byte
	present bool
}

// KeyFromBytes wraps a 32-byte key. A zero-length slice yields an absent key.
func KeyFromBytes(b []byte) Key {
	if len(b) != 32 {
		return Key{}
	}
	var k Key
	copy(k.b[:], b)
	k.present = true
	return k
}

// Present reports whether a key is set. It is the only thing a diagnostic may
// disclose about one — REQ-DIA-047.
func (k Key) Present() bool { return k.present }

// String redacts. Use Base64 when the value is genuinely needed.
func (k Key) String() string {
	if !k.present {
		return ""
	}
	return "[redacted]"
}

// Base64 returns the key itself. Callers are the store and the client-config
// generator; no response path may use it.
func (k Key) Base64() string {
	if !k.present {
		return ""
	}
	return base64.StdEncoding.EncodeToString(k.b[:])
}

// DeviceState is one WireGuard device as the kernel holds it. A single read
// returns every field adoption stores under REQ-RCN-061 and REQ-RCN-062.
type DeviceState struct {
	Name       string
	PrivateKey Key
	PublicKey  string
	ListenPort int
	Fwmark     uint32
	Peers      []PeerState
}

// PeerState is one peer as the kernel holds it.
type PeerState struct {
	PublicKey    string
	PresharedKey Key
	AllowedIPs   []netip.Prefix
	// Endpoint is kernel-owned: the kernel learns it from a handshake and does
	// not distinguish that from one an operator configured. REQ-RCN-063 keeps
	// it out of desired state; REQ-DIA-048 warns when one is present.
	Endpoint            string
	PersistentKeepalive time.Duration
}

// LinkState is one network interface as netlink holds it.
type LinkState struct {
	Name string
	// Type is the netlink link type. "wireguard" is what REQ-DIA-042 needs in
	// order to reject a link the resource model does not describe.
	Type string
	MTU  int
	// AdminUp is the administrative flag. REQ-RES-019 requires oper_state to
	// come from here, because a WireGuard link reports its operational state as
	// unknown even while it is up.
	AdminUp   bool
	Addresses []netip.Prefix
}

// Device reads and configures a WireGuard device. It cannot create or destroy
// the interface itself.
type Device interface {
	// Names lists the WireGuard devices the kernel holds.
	Names() ([]string, error)
	// Snapshot returns one device and all of its peers.
	Snapshot(name string) (DeviceState, error)
}

// Link owns interface lifecycle and addressing.
type Link interface {
	// Names lists every network interface, whatever its type.
	Names() ([]string, error)
	// State returns one interface, including its addresses.
	State(name string) (LinkState, error)
}

// UnitState is what can be learned about a systemd unit without executing
// anything. REQ-SEC-041 forbids a child process in a production path, so
// enablement is read as a symlink under a target's wants directory —
// REQ-DIA-049.
type UnitState int

const (
	// UnitUndetermined means the search roots could not be read. REQ-DIA-050
	// requires this to surface as UNKNOWN rather than as a pass.
	UnitUndetermined UnitState = iota
	UnitAbsent
	UnitDisabled
	UnitEnabled
)

// WgQuickConfig reports the directives a wg-quick configuration file carries.
// Values are never read from it: REQ-DIA-045 keeps the file out of the spec,
// and every field comes from the kernel under REQ-RCN-061.
type WgQuickConfig struct {
	Path string
	// Present is false when no file exists for the interface, which is not a
	// finding of any kind.
	Present bool
	// Unparseable is true when the file exists but could not be read. It is a
	// WARN under REQ-DIA-046, never a blocking finding.
	Unparseable bool
	// Directives holds the names of the directives found that the resource
	// model has no equivalent for, in the order encountered.
	Directives []string
}

// HostFS answers the filesystem questions the readiness report asks. It exists
// as a port so the report can be tested without a systemd tree on disk.
type HostFS interface {
	// WgQuickUnit reports whether wg-quick@<iface>.service is enabled.
	WgQuickUnit(iface string) (UnitState, error)
	// WgQuickConfig reports the unsupported directives of the interface's
	// configuration file.
	WgQuickConfig(iface string) (WgQuickConfig, error)
}

// DesiredState is the part of the store the readiness report needs. doctor
// reads it directly under REQ-CLI-004, and an absent store is an empty desired
// state rather than an error, which is what makes the command usable on a node
// where the agent has never run.
type DesiredState interface {
	// Describes reports whether desired state describes the interface, which is
	// what REQ-RES-017 calls MANAGED.
	Describes(name string) bool
	// DeletionRecord reports whether a deletion record names the interface,
	// which is what REQ-RCN-034 calls ORPHANED.
	DeletionRecord(name string) bool
}
