package hostfs_test

import (
	"os"
	"path/filepath"
	"testing"

	"wg-agent/internal/platform"
	"wg-agent/internal/platform/hostfs"
)

// tree lays out a systemd and wg-quick layout under a temporary root, so the
// predicate of REQ-DIA-049 is exercised against real directory entries without
// needing systemd or privilege.
func tree(t *testing.T) (string, *hostfs.Host) {
	t.Helper()
	root := t.TempDir()
	for _, d := range []string{
		"etc/systemd/system/multi-user.target.wants",
		"etc/wireguard",
	} {
		if err := os.MkdirAll(filepath.Join(root, d), 0o755); err != nil {
			t.Fatal(err)
		}
	}
	return root, hostfs.NewAt(root)
}

func TestHostFS_EnablementIsASymlink_REQ_DIA_049(t *testing.T) {
	root, h := tree(t)

	// A template unit present but no wants symlink is the disabled case.
	unit := filepath.Join(root, "etc/systemd/system/wg-quick@.service")
	if err := os.WriteFile(unit, []byte("[Unit]\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	got, err := h.WgQuickUnit("wg0")
	if err != nil {
		t.Fatal(err)
	}
	if got != platform.UnitDisabled {
		t.Fatalf("want UnitDisabled with no wants symlink, got %v", got)
	}

	// systemctl enable creates exactly this symlink. Nothing here executes it.
	link := filepath.Join(root, "etc/systemd/system/multi-user.target.wants/wg-quick@wg0.service")
	if err := os.Symlink(unit, link); err != nil {
		t.Fatal(err)
	}
	got, err = h.WgQuickUnit("wg0")
	if err != nil {
		t.Fatal(err)
	}
	if got != platform.UnitEnabled {
		t.Fatalf("want UnitEnabled once the wants symlink exists, got %v", got)
	}

	// The symlink is per instance, so a sibling interface stays disabled.
	got, err = h.WgQuickUnit("wg1")
	if err != nil {
		t.Fatal(err)
	}
	if got != platform.UnitDisabled {
		t.Fatalf("want UnitDisabled for an interface with no symlink, got %v", got)
	}
}

func TestHostFS_DanglingSymlinkStillCountsAsEnabled_REQ_DIA_049(t *testing.T) {
	root, h := tree(t)
	link := filepath.Join(root, "etc/systemd/system/multi-user.target.wants/wg-quick@wg0.service")
	if err := os.Symlink(filepath.Join(root, "absent.service"), link); err != nil {
		t.Fatal(err)
	}
	// Lstat rather than Stat: systemd treats the symlink as the enablement, and
	// a dangling one still starts nothing but still means enabled was asked for.
	got, err := h.WgQuickUnit("wg0")
	if err != nil {
		t.Fatal(err)
	}
	if got != platform.UnitEnabled {
		t.Fatalf("want UnitEnabled for a dangling wants symlink, got %v", got)
	}
}

func TestHostFS_AbsentUnitIsNotUndetermined_REQ_DIA_050(t *testing.T) {
	_, h := tree(t)
	got, err := h.WgQuickUnit("wg0")
	if err != nil {
		t.Fatal(err)
	}
	// Directories that do not exist are an answer: wg-quick is not installed.
	// Undetermined is reserved for roots that exist and cannot be read.
	if got != platform.UnitAbsent {
		t.Fatalf("want UnitAbsent on a node without wg-quick, got %v", got)
	}
}

func TestHostFS_DirectivesDetectedWithoutReadingValues_REQ_DIA_045(t *testing.T) {
	root, h := tree(t)
	conf := filepath.Join(root, "etc/wireguard/wg0.conf")
	body := `[Interface]
Address = 10.0.0.1/24
ListenPort = 51820
PrivateKey = SECRETKEYVALUEMUSTNOTAPPEAR
MTU = 1420
DNS = 10.0.0.53
Table = off
SaveConfig = true
PostUp = iptables -A FORWARD -i %i -j ACCEPT
PostDown = iptables -D FORWARD -i %i -j ACCEPT

[Peer]
PublicKey = abc
AllowedIPs = 10.0.0.2/32
`
	if err := os.WriteFile(conf, []byte(body), 0o600); err != nil {
		t.Fatal(err)
	}

	cfg, err := h.WgQuickConfig("wg0")
	if err != nil {
		t.Fatal(err)
	}
	if !cfg.Present || cfg.Unparseable {
		t.Fatalf("want a present, parseable file, got %+v", cfg)
	}

	want := map[string]bool{"PostUp": false, "PostDown": false, "DNS": false,
		"SaveConfig": false, "Table": false}
	for _, d := range cfg.Directives {
		if _, ok := want[d]; !ok {
			t.Errorf("unexpected directive reported: %q", d)
		}
		want[d] = true
	}
	for d, seen := range want {
		if !seen {
			t.Errorf("directive %q not reported", d)
		}
	}

	// REQ-DIA-045: the file is scanned for directive names, never for values.
	// Nothing the adapter returns may carry one.
	for _, d := range cfg.Directives {
		if d == "SECRETKEYVALUEMUSTNOTAPPEAR" {
			t.Fatal("a value leaked into the result")
		}
	}
	if cfg.Path == "" {
		t.Error("the path is needed so a finding can name the file")
	}
}

func TestHostFS_MappedDirectivesAreNotReported_REQ_DIA_044(t *testing.T) {
	root, h := tree(t)
	conf := filepath.Join(root, "etc/wireguard/wg0.conf")
	// Every directive here has an equivalent in the resource model, so none is
	// a finding: Address, ListenPort, PrivateKey, MTU and FwMark all map.
	body := "[Interface]\nAddress = 10.0.0.1/24\nListenPort = 51820\nMTU = 1420\nFwMark = 0x1\n"
	if err := os.WriteFile(conf, []byte(body), 0o600); err != nil {
		t.Fatal(err)
	}
	cfg, err := h.WgQuickConfig("wg0")
	if err != nil {
		t.Fatal(err)
	}
	if len(cfg.Directives) != 0 {
		t.Errorf("want no findings for directives the model covers, got %v", cfg.Directives)
	}
}

func TestHostFS_AbsentConfigIsNotAFinding_REQ_DIA_046(t *testing.T) {
	_, h := tree(t)
	cfg, err := h.WgQuickConfig("wg0")
	if err != nil {
		t.Fatal(err)
	}
	if cfg.Present || cfg.Unparseable {
		t.Fatalf("an absent configuration is not a finding, got %+v", cfg)
	}
}
