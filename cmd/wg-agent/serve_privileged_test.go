//go:build integration && privileged

// Privileged tier: the subcommand is driven the way an operator drives it.
//
// This sits in the privileged tier rather than the integration one because
// `serve` runs check 5 of REQ-API-050 before it applies anything, and
// REQ-FWD-025 makes an unwritable forwarding sysctl a startup failure. Docker
// mounts /proc/sys read-only, so a CAP_NET_ADMIN container is a host the agent
// correctly refuses to run on. Run with `make docker-test-privileged`.
package main

import (
	"bytes"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"

	"wg-agent/internal/store"
)

func openHeldStore(t *testing.T, path string) *store.Store {
	t.Helper()
	s, err := store.Open(path)
	if err != nil {
		t.Fatalf("open store: %v", err)
	}
	return s
}

func mustRun(t *testing.T, name string, args ...string) {
	t.Helper()
	if out, err := exec.Command(name, args...).CombinedOutput(); err != nil {
		t.Fatalf("%s %v: %v: %s", name, args, err, out)
	}
}

// REQ-CLI-008: one pass, then exit. REQ-CLI-009: the four fields that say
// whether the migration worked.
func TestServe_OncePrintsEveryInterface_REQ_CLI_009(t *testing.T) {
	const name = "wgc0"
	t.Cleanup(func() { _ = exec.Command("ip", "link", "del", name).Run() })

	mustRun(t, "ip", "link", "add", name, "type", "wireguard")
	mustRun(t, "wg", "set", name, "listen-port", "51992")
	mustRun(t, "ip", "link", "set", name, "up")

	var stdout, stderr bytes.Buffer
	code := serveCmd([]string{
		"--store", filepath.Join(t.TempDir(), "state.db"),
		"--once",
	}, &stdout, &stderr)

	if code != 0 {
		t.Fatalf("exit = %d, want 0\nstderr: %s", code, stderr.String())
	}

	line := ""
	for _, l := range strings.Split(stdout.String(), "\n") {
		if strings.HasPrefix(l, name) {
			line = l
			break
		}
	}
	if line == "" {
		t.Fatalf("no line for %s in:\n%s", name, stdout.String())
	}
	// The interface is not in desired state, so it is FOREIGN and untouched.
	for _, want := range []string{"FOREIGN", "UP", "peers=0", "READY"} {
		if !strings.Contains(line, want) {
			t.Errorf("line %q lacks %q", line, want)
		}
	}
}

// REQ-CLI-008: a single pass leaves no process behind, and a link outside
// desired state is not touched by it — REQ-RCN-030.
func TestServe_OnceLeavesForeignLinksAlone_REQ_CLI_008(t *testing.T) {
	const name = "wgc1"
	t.Cleanup(func() { _ = exec.Command("ip", "link", "del", name).Run() })

	mustRun(t, "ip", "link", "add", name, "type", "wireguard")
	mustRun(t, "wg", "set", name, "listen-port", "51991")
	mustRun(t, "ip", "addr", "add", "10.96.0.1/24", "dev", name)
	mustRun(t, "ip", "link", "set", name, "up")

	before, err := exec.Command("wg", "show", name, "dump").Output()
	if err != nil {
		t.Fatalf("wg show: %v", err)
	}

	var stdout, stderr bytes.Buffer
	if code := serveCmd([]string{
		"--store", filepath.Join(t.TempDir(), "state.db"),
		"--once",
	}, &stdout, &stderr); code != 0 {
		t.Fatalf("exit = %d\nstderr: %s", code, stderr.String())
	}

	after, err := exec.Command("wg", "show", name, "dump").Output()
	if err != nil {
		t.Fatalf("wg show: %v", err)
	}
	if !bytes.Equal(before, after) {
		t.Errorf("a foreign link changed.\nbefore: %s\nafter:  %s", before, after)
	}
}

// REQ-RCN-006: the lock is exclusive, so a second holder is refused rather
// than allowed to write alongside the first.
func TestServe_RefusesASecondHolderOfTheLock_REQ_RCN_006(t *testing.T) {
	path := filepath.Join(t.TempDir(), "state.db")

	// The first pass releases the lock on return, so hold one explicitly.
	held := openHeldStore(t, path)
	defer func() { _ = held.Close() }()

	var stdout, stderr bytes.Buffer
	code := serveCmd([]string{"--store", path, "--once"}, &stdout, &stderr)

	if code == 0 {
		t.Fatalf("exit = 0; a second holder must be refused\nstdout: %s", stdout.String())
	}
	if !strings.Contains(stderr.String(), "already running") {
		t.Errorf("stderr = %q, want it to name the running agent", stderr.String())
	}
}
