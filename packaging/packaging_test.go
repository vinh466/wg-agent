// Package packaging holds no code. The test asserts that the unit file carries
// the directives the requirements name, which is the part of REQ-CFG-010 and
// REQ-CFG-011 that can be checked without systemd as PID 1.
//
// REQ-CFG-013 wants the ProtectKernelTunables combination exercised against
// every systemd version the tested distributions ship. That needs a virtual
// machine and stays deferred under B-03; a directive silently dropped from this
// file is the failure this test does catch.
package packaging_test

import (
	"os"
	"strings"
	"testing"
)

func unit(t *testing.T) string {
	t.Helper()
	b, err := os.ReadFile("systemd/wg-agent.service")
	if err != nil {
		t.Fatalf("read unit: %v", err)
	}
	return string(b)
}

// REQ-CFG-010 — a dedicated account holding CAP_NET_ADMIN.
func TestUnit_RunsUnderADedicatedAccount_REQ_CFG_010(t *testing.T) {
	u := unit(t)
	for _, want := range []string{
		"User=wg-agent",
		"Group=wg-agent",
		"AmbientCapabilities=CAP_NET_ADMIN",
		"CapabilityBoundingSet=CAP_NET_ADMIN",
		"NoNewPrivileges=yes",
	} {
		if !strings.Contains(u, want) {
			t.Errorf("the unit lacks %q", want)
		}
	}
	if strings.Contains(u, "User=root") {
		t.Error("the unit must not run as root")
	}
}

// REQ-CFG-011 — ProtectKernelTunables=yes with the carve-out that lets
// REQ-FWD-020 write the per-interface forwarding sysctl. One without the other
// is the combination the requirement exists to prevent.
func TestUnit_CarvesOutTheForwardingSysctl_REQ_CFG_011(t *testing.T) {
	u := unit(t)
	if !strings.Contains(u, "ProtectKernelTunables=yes") {
		t.Error("the unit must set ProtectKernelTunables=yes")
	}
	rw := ""
	for _, line := range strings.Split(u, "\n") {
		if strings.HasPrefix(line, "ReadWritePaths=") {
			rw = line
			break
		}
	}
	if rw == "" {
		t.Fatal("the unit declares no ReadWritePaths")
	}
	for _, want := range []string{
		"/proc/sys/net/ipv4/conf",
		"/var/lib/wg-agent",
		"/run/wg-agent",
	} {
		if !strings.Contains(rw, want) {
			t.Errorf("ReadWritePaths lacks %q: %s", want, rw)
		}
	}
	// The whole of /proc/sys would defeat ProtectKernelTunables.
	if strings.Contains(rw, "/proc/sys ") || strings.HasSuffix(rw, "/proc/sys") {
		t.Errorf("ReadWritePaths opens the whole of /proc/sys: %s", rw)
	}
}

// REQ-CFG-004 and REQ-CFG-005 — the per-host overrides, tolerating an absent
// file. The leading dash is what REQ-CFG-005 requires.
func TestUnit_LoadsTheEnvironmentFileTolerantly_REQ_CFG_005(t *testing.T) {
	u := unit(t)
	if !strings.Contains(u, "EnvironmentFile=-/etc/default/wg-agent") {
		t.Error("the unit must load /etc/default/wg-agent with a leading dash")
	}
}

// The hardening set of SPEC-09 section 4, in full. A directive dropped by an
// edit is silent otherwise: the service still starts, with less protection.
func TestUnit_CarriesTheHardeningSet_REQ_CFG_010(t *testing.T) {
	u := unit(t)
	for _, want := range []string{
		"ProtectSystem=strict",
		"ProtectHome=yes",
		"PrivateTmp=yes",
		"PrivateDevices=yes",
		"ProtectControlGroups=yes",
		"RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6 AF_NETLINK",
		"RestrictNamespaces=yes",
		"LockPersonality=yes",
		"MemoryDenyWriteExecute=yes",
		"SystemCallArchitectures=native",
	} {
		if !strings.Contains(u, want) {
			t.Errorf("the unit lacks %q", want)
		}
	}
}

// AF_NETLINK has to be in the restricted set: without it the agent cannot
// reach the kernel at all, which would make the unit start and then fail every
// reconcile pass.
func TestUnit_PermitsNetlink_REQ_CFG_010(t *testing.T) {
	if !strings.Contains(unit(t), "AF_NETLINK") {
		t.Error("RestrictAddressFamilies must permit AF_NETLINK")
	}
}

// The companion files exist. The unit runs as an account and writes a runtime
// directory that nothing else creates yet, so a missing one is a unit that
// fails to start.
func TestPackaging_CompanionFilesExist_REQ_CFG_021(t *testing.T) {
	for _, p := range []string{
		"systemd/wg-agent.sysusers",
		"systemd/wg-agent.tmpfiles",
	} {
		if _, err := os.Stat(p); err != nil {
			t.Errorf("%s: %v", p, err)
		}
	}
	b, err := os.ReadFile("systemd/wg-agent.tmpfiles")
	if err != nil {
		t.Fatalf("read tmpfiles: %v", err)
	}
	for _, want := range []string{"/run/wg-agent", "/var/lib/wg-agent"} {
		if !strings.Contains(string(b), want) {
			t.Errorf("tmpfiles lacks %q", want)
		}
	}
}
