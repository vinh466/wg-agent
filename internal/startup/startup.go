// Package startup performs the checks of REQ-API-050.
//
// Every check reads a file or a netlink socket. REQ-SEC-041 forbids a child
// process in a production path, so none of them shells out to `modprobe`, `wg`
// or `sysctl` — which also means a check cannot succeed for a reason the agent
// itself could not reproduce.
package startup

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"strings"

	"wg-agent/internal/store"
)

// Reason codes, from the closed set of REQ-API-041. The numbering of the table
// in REQ-API-050 is kept in the check names so a log line can be traced to the
// row that produced it.
const (
	ReasonModuleNotLoaded   = "WG_MODULE_NOT_LOADED"
	ReasonKernelTooOld      = "KERNEL_TOO_OLD"
	ReasonMissingCap        = "MISSING_CAP_NET_ADMIN"
	ReasonStoreCorrupt      = "STORE_CORRUPT"
	ReasonSchemaTooNew      = "STORE_SCHEMA_TOO_NEW"
	ReasonSysctlWriteDenied = "SYSCTL_WRITE_DENIED"
)

// Error carries the reason code of the check that failed, so an installer or a
// unit log can branch on the cause without matching message text.
type Error struct {
	Check   int
	Reason  string
	Message string
	Hint    string
}

func (e *Error) Error() string {
	if e.Hint == "" {
		return fmt.Sprintf("startup check %d: %s: %s", e.Check, e.Reason, e.Message)
	}
	return fmt.Sprintf("startup check %d: %s: %s\n  %s",
		e.Check, e.Reason, e.Message, e.Hint)
}

// minKernel is the version SPEC-09 section 3 names for in-tree WireGuard.
var minKernel = [2]int{5, 6}

// Checker holds the paths the checks read. The defaults are the real ones; a
// test points them at a temporary tree.
type Checker struct {
	// OSRelease is the file carrying the kernel version.
	OSRelease string
	// ProcStatus carries the effective capability set.
	ProcStatus string
	// SysctlDir is the subtree REQ-CFG-011 carves out of ProtectKernelTunables.
	SysctlDir string
	// Devices lists the WireGuard devices, which is the side-effect-free test
	// of whether the kernel carries WireGuard at all.
	Devices func() ([]string, error)
}

// New returns a checker over the running host.
func New(devices func() ([]string, error)) *Checker {
	return &Checker{
		OSRelease:  "/proc/sys/kernel/osrelease",
		ProcStatus: "/proc/self/status",
		SysctlDir:  "/proc/sys/net/ipv4/conf",
		Devices:    devices,
	}
}

// Run performs checks 1, 2 and 5 of REQ-API-050, in order, stopping at the
// first failure — the requirement says fail early.
//
// Checks 3 and 4 are the store, which Store performs, because the caller has to
// open it anyway and a second open would race the first. Checks 7 and 9 are
// config.Validate, which answers them from the file alone. Check 6 is nftables,
// deferred with the rest of step 10 under B-04.
func (c *Checker) Run() error {
	if err := c.checkWireGuard(); err != nil {
		return err
	}
	if err := c.checkCapNetAdmin(); err != nil {
		return err
	}
	return c.checkSysctlWritable()
}

// checkWireGuard is check 1. Listing devices touches the generic netlink family
// the WireGuard module registers, so a failure means the kernel does not carry
// it — whether it was never loaded or cannot be.
func (c *Checker) checkWireGuard() error {
	if c.Devices == nil {
		return nil
	}
	if _, err := c.Devices(); err == nil {
		return nil
	}

	// The kernel version distinguishes the two codes the table gives. A kernel
	// older than 5.6 cannot carry in-tree WireGuard at all, so telling the
	// operator to load a module would send them the wrong way.
	if old, version := c.kernelTooOld(); old {
		return &Error{
			Check:   1,
			Reason:  ReasonKernelTooOld,
			Message: fmt.Sprintf("kernel %s predates in-tree WireGuard", version),
			Hint: "Install wireguard-dkms, or run a kernel 5.6 or later. " +
				"See SPEC-09 section 3.",
		}
	}
	return &Error{
		Check:   1,
		Reason:  ReasonModuleNotLoaded,
		Message: "the kernel does not expose the WireGuard netlink family",
		Hint:    "Load the module with `modprobe wireguard` on the host.",
	}
}

// kernelTooOld reports whether the running kernel predates in-tree WireGuard.
// An unreadable or unparseable version is not treated as too old: the agent
// would then blame the kernel for a condition it could not establish.
func (c *Checker) kernelTooOld() (bool, string) {
	b, err := os.ReadFile(c.OSRelease)
	if err != nil {
		return false, ""
	}
	version := strings.TrimSpace(string(b))

	// A release is `5.15.0-91-generic`; only the first two components decide.
	parts := strings.SplitN(version, ".", 3)
	if len(parts) < 2 {
		return false, version
	}
	major, err1 := strconv.Atoi(parts[0])
	minor, err2 := strconv.Atoi(strings.SplitN(parts[1], "-", 2)[0])
	if err1 != nil || err2 != nil {
		return false, version
	}
	if major != minKernel[0] {
		return major < minKernel[0], version
	}
	return minor < minKernel[1], version
}

// capNetAdmin is the bit CAP_NET_ADMIN occupies in a capability mask.
const capNetAdmin = 12

// checkCapNetAdmin is check 2. The effective set is read from /proc/self/status
// rather than probed by attempting a privileged operation, so the check has no
// side effect on a host where it passes.
func (c *Checker) checkCapNetAdmin() error {
	b, err := os.ReadFile(c.ProcStatus)
	if err != nil {
		// Without the file the capability cannot be established. Refusing to
		// start would make the agent unusable wherever procfs is not mounted,
		// and the first netlink write reports the truth in any case.
		return nil
	}

	for _, line := range strings.Split(string(b), "\n") {
		rest, ok := strings.CutPrefix(line, "CapEff:")
		if !ok {
			continue
		}
		mask, err := strconv.ParseUint(strings.TrimSpace(rest), 16, 64)
		if err != nil {
			return nil
		}
		if mask&(1<<capNetAdmin) != 0 {
			return nil
		}
		return &Error{
			Check:   2,
			Reason:  ReasonMissingCap,
			Message: "CAP_NET_ADMIN is not in the effective capability set",
			Hint: "The systemd unit sets AmbientCapabilities=CAP_NET_ADMIN under " +
				"REQ-CFG-010. Outside systemd, run as root or grant the capability.",
		}
	}
	return nil
}

// checkSysctlWritable is check 5, which REQ-FWD-025 requires to happen here
// rather than during reconcile.
//
// The probe opens `conf/default/forwarding` for writing and closes it without
// writing, so nothing changes. That file is inside the subtree REQ-CFG-011
// carves out of `ProtectKernelTunables=yes`, which makes the check answer
// exactly the question a hardened unit raises. A per-interface node cannot be
// probed instead: it exists only while its link does, and at startup there may
// be no managed interface at all.
func (c *Checker) checkSysctlWritable() error {
	path := filepath.Join(c.SysctlDir, "default", "forwarding")
	f, err := os.OpenFile(path, os.O_WRONLY, 0)
	if err == nil {
		return f.Close()
	}
	if errors.Is(err, os.ErrNotExist) {
		// No IPv4 conf subtree at all. Nothing to enforce and nothing to
		// mis-report; the reconcile path tolerates an absent node.
		return nil
	}
	return &Error{
		Check:   5,
		Reason:  ReasonSysctlWriteDenied,
		Message: fmt.Sprintf("%s is not writable: %v", path, err),
		Hint: "REQ-FWD-020 sets net.ipv4.conf.<iface>.forwarding. A unit with " +
			"ProtectKernelTunables=yes needs ReadWritePaths=/proc/sys/net/ipv4/conf, " +
			"per REQ-CFG-011.",
	}
}

// Store performs checks 3 and 4 by classifying the error from opening the
// store. The caller opens it, because it needs the handle and because a second
// open would contend for the lock of REQ-RCN-006 with the first.
func Store(err error) error {
	if err == nil {
		return nil
	}
	if errors.Is(err, store.ErrSchemaTooNew) {
		return &Error{
			Check:   4,
			Reason:  ReasonSchemaTooNew,
			Message: err.Error(),
			Hint: "This store was written by a newer build. Run that build, or " +
				"restore a store this one understands. Downgrading the file is " +
				"not supported.",
		}
	}
	if errors.Is(err, store.ErrLocked) {
		// Not a check failure. Another holder is REQ-RCN-007's case, and the
		// caller reports it in the terms the operator needs.
		return err
	}
	return &Error{
		Check:   3,
		Reason:  ReasonStoreCorrupt,
		Message: err.Error(),
		Hint:    "The store must be readable and writable by the account running the agent.",
	}
}
