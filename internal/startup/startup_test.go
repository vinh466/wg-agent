package startup_test

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"wg-agent/internal/startup"
	"wg-agent/internal/store"
)

// tree builds a fake procfs. Every check reads a file, which is what makes the
// whole of REQ-API-050 testable without privilege.
type tree struct {
	root string
}

func newTree(t *testing.T) *tree {
	return &tree{root: t.TempDir()}
}

func (x *tree) write(t *testing.T, rel, body string) string {
	t.Helper()
	p := filepath.Join(x.root, rel)
	if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
		t.Fatalf("mkdir: %v", err)
	}
	if err := os.WriteFile(p, []byte(body), 0o644); err != nil {
		t.Fatalf("write %s: %v", rel, err)
	}
	return p
}

// checker returns one wired to the fake tree, with every check passing unless a
// test breaks it.
func (x *tree) checker(t *testing.T) *startup.Checker {
	t.Helper()
	x.write(t, "osrelease", "6.1.0-18-amd64\n")
	x.write(t, "status", "Name:\twg-agent\nCapEff:\t0000000000001000\n")
	x.write(t, "conf/default/forwarding", "0\n")
	return &startup.Checker{
		OSRelease:  filepath.Join(x.root, "osrelease"),
		ProcStatus: filepath.Join(x.root, "status"),
		SysctlDir:  filepath.Join(x.root, "conf"),
		Devices:    func() ([]string, error) { return []string{"wg0"}, nil },
	}
}

func reasonOf(t *testing.T, err error) string {
	t.Helper()
	var e *startup.Error
	if !errors.As(err, &e) {
		t.Fatalf("error %v is not a startup.Error", err)
	}
	return e.Reason
}

// A host that meets every requirement passes.
func TestRun_HealthyHostPasses_REQ_API_050(t *testing.T) {
	x := newTree(t)
	if err := x.checker(t).Run(); err != nil {
		t.Fatalf("a healthy host must pass: %v", err)
	}
}

// Check 1: a kernel that carries WireGuard but has not loaded it.
func TestRun_ModuleNotLoaded_REQ_API_050(t *testing.T) {
	x := newTree(t)
	c := x.checker(t)
	c.Devices = func() ([]string, error) { return nil, fmt.Errorf("file does not exist") }

	err := c.Run()
	if got := reasonOf(t, err); got != startup.ReasonModuleNotLoaded {
		t.Errorf("reason = %q, want WG_MODULE_NOT_LOADED", got)
	}
}

// Check 1, the other code: a kernel too old to carry WireGuard in tree. The
// distinction matters, because telling this operator to load a module sends
// them the wrong way.
func TestRun_KernelTooOld_REQ_API_050(t *testing.T) {
	for _, release := range []string{"4.19.0-21-amd64", "5.4.0-170-generic", "5.5.13"} {
		x := newTree(t)
		c := x.checker(t)
		x.write(t, "osrelease", release+"\n")
		c.Devices = func() ([]string, error) { return nil, fmt.Errorf("no such family") }

		if got := reasonOf(t, c.Run()); got != startup.ReasonKernelTooOld {
			t.Errorf("%s gave %q, want KERNEL_TOO_OLD", release, got)
		}
	}
}

// A kernel at or past the boundary is not too old.
func TestRun_KernelAtTheBoundaryIsNotTooOld_REQ_API_050(t *testing.T) {
	for _, release := range []string{"5.6.0", "5.15.0-91-generic", "6.8.0-31-generic"} {
		x := newTree(t)
		c := x.checker(t)
		x.write(t, "osrelease", release+"\n")
		c.Devices = func() ([]string, error) { return nil, fmt.Errorf("no such family") }

		if got := reasonOf(t, c.Run()); got != startup.ReasonModuleNotLoaded {
			t.Errorf("%s gave %q, want WG_MODULE_NOT_LOADED", release, got)
		}
	}
}

// Check 2: CAP_NET_ADMIN absent from the effective set.
func TestRun_MissingCapNetAdmin_REQ_API_050(t *testing.T) {
	x := newTree(t)
	c := x.checker(t)
	// CapEff without bit 12.
	x.write(t, "status", "Name:\twg-agent\nCapEff:\t0000000000000800\n")

	if got := reasonOf(t, c.Run()); got != startup.ReasonMissingCap {
		t.Errorf("reason = %q, want MISSING_CAP_NET_ADMIN", got)
	}
}

// A full capability set passes, which is what running as root looks like.
func TestRun_RootCapabilitiesPass_REQ_API_050(t *testing.T) {
	x := newTree(t)
	c := x.checker(t)
	x.write(t, "status", "Name:\twg-agent\nCapEff:\t000001ffffffffff\n")

	if err := c.Run(); err != nil {
		t.Errorf("a full capability set must pass: %v", err)
	}
}

// REQ-FWD-025: an unwritable sysctl fails at startup rather than surfacing as
// a DEGRADED interface later. This is the hardened-unit case: a unit with
// ProtectKernelTunables=yes and no ReadWritePaths carve-out.
func TestRun_SysctlNotWritable_REQ_FWD_025(t *testing.T) {
	x := newTree(t)
	c := x.checker(t)

	// The real condition is a read-only mount, which a unit test cannot
	// produce. A directory in the file's place reaches the same branch:
	// opening it for writing fails with EISDIR for every user, root included,
	// so the assertion holds in the container as well as on a workstation.
	// Mode bits would not — root ignores them.
	p := filepath.Join(x.root, "conf", "default", "forwarding")
	if err := os.Remove(p); err != nil {
		t.Fatalf("remove: %v", err)
	}
	if err := os.Mkdir(p, 0o755); err != nil {
		t.Fatalf("mkdir: %v", err)
	}

	err := c.Run()
	if got := reasonOf(t, err); got != startup.ReasonSysctlWriteDenied {
		t.Errorf("reason = %q, want SYSCTL_WRITE_DENIED", got)
	}
	var e *startup.Error
	_ = errors.As(err, &e)
	if e.Check != 5 {
		t.Errorf("check = %d, want 5", e.Check)
	}
	if e.Hint == "" {
		t.Error("the hint must name the ReadWritePaths carve-out of REQ-CFG-011")
	}
}

// The probe does not change the value it opens. A check with a side effect
// would make starting the agent alter the host.
func TestRun_SysctlProbeWritesNothing_REQ_FWD_025(t *testing.T) {
	x := newTree(t)
	c := x.checker(t)
	p := filepath.Join(x.root, "conf", "default", "forwarding")

	if err := c.Run(); err != nil {
		t.Fatalf("run: %v", err)
	}
	b, err := os.ReadFile(p)
	if err != nil {
		t.Fatalf("read back: %v", err)
	}
	if string(b) != "0\n" {
		t.Errorf("the probe changed the value to %q", b)
	}
}

// An absent IPv4 conf subtree is not a failure: there is nothing to enforce,
// and the reconcile path tolerates an absent node.
func TestRun_AbsentSysctlSubtreePasses_REQ_FWD_025(t *testing.T) {
	x := newTree(t)
	c := x.checker(t)
	c.SysctlDir = filepath.Join(x.root, "no-such-dir")

	if err := c.Run(); err != nil {
		t.Errorf("an absent subtree must not fail the start: %v", err)
	}
}

// Check 4: a store from a newer build. REQ-RCN-005 requires refusal rather
// than misinterpretation, and the code is distinct from a corrupt store
// because the two call for opposite actions.
func TestStore_SchemaTooNew_REQ_API_050(t *testing.T) {
	err := startup.Store(fmt.Errorf("wrapping: %w", store.ErrSchemaTooNew))
	if got := reasonOf(t, err); got != startup.ReasonSchemaTooNew {
		t.Errorf("reason = %q, want STORE_SCHEMA_TOO_NEW", got)
	}
}

// Check 3: any other failure to open the store.
func TestStore_UnreadableIsCorrupt_REQ_API_050(t *testing.T) {
	err := startup.Store(fmt.Errorf("parse store: unexpected end of JSON input"))
	if got := reasonOf(t, err); got != startup.ReasonStoreCorrupt {
		t.Errorf("reason = %q, want STORE_CORRUPT", got)
	}
}

// A held lock is REQ-RCN-007's case, not a startup check. It passes through
// unchanged so the caller can report it in the terms the operator needs.
func TestStore_HeldLockPassesThrough_REQ_RCN_007(t *testing.T) {
	err := startup.Store(store.ErrLocked)
	if !errors.Is(err, store.ErrLocked) {
		t.Errorf("error = %v, want ErrLocked unchanged", err)
	}
	var e *startup.Error
	if errors.As(err, &e) {
		t.Error("a held lock must not be reported as a startup check failure")
	}
}

func TestStore_NilIsNil(t *testing.T) {
	if err := startup.Store(nil); err != nil {
		t.Errorf("Store(nil) = %v, want nil", err)
	}
}

// The error names the row of the table it came from, so a unit log can be
// traced back to the check.
func TestError_NamesTheCheckAndTheHint_REQ_API_050(t *testing.T) {
	e := &startup.Error{Check: 5, Reason: "SYSCTL_WRITE_DENIED", Message: "m", Hint: "h"}
	got := e.Error()
	for _, want := range []string{"startup check 5", "SYSCTL_WRITE_DENIED", "m", "h"} {
		if !strings.Contains(got, want) {
			t.Errorf("Error() = %q, missing %q", got, want)
		}
	}
}
