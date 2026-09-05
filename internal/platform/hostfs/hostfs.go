// Package hostfs answers the readiness report's filesystem questions.
//
// REQ-SEC-041 forbids executing a child process in a production path, so
// nothing here shells out to systemctl. Unit enablement is a symlink, which
// REQ-DIA-049 fixes as the predicate, and a wg-quick configuration file is
// scanned for directive names only — REQ-DIA-045 keeps its values out of any
// spec.
package hostfs

import (
	"bufio"
	"errors"
	"io/fs"
	"os"
	"path/filepath"
	"strings"

	"wg-agent/internal/platform"
)

// unitRoots are the directories systemd reads unit symlinks from, in the order
// a reader should consider them. An enablement symlink lives under a target's
// .wants directory in one of these.
var unitRoots = []string{
	"/etc/systemd/system",
	"/run/systemd/system",
	"/usr/lib/systemd/system",
	"/lib/systemd/system",
}

// wgQuickDir is where wg-quick keeps per-interface configuration.
const wgQuickDir = "/etc/wireguard"

// unsupported lists the wg-quick directives the resource model has no
// equivalent for. REQ-DIA-044 reports each one found as a WARN.
//
// PostUp and PostDown are the costly ones: ADR-0007 forbids the agent from
// reproducing them, so disabling wg-quick after adoption loses their effect at
// the next boot. PreUp and PreDown are the same class of directive.
var unsupported = []string{
	"PreUp", "PostUp", "PreDown", "PostDown",
	"DNS", "SaveConfig", "Table",
}

// sysctlDir holds the per-interface IPv4 tunables.
const sysctlDir = "/proc/sys/net/ipv4/conf"

// Host implements platform.HostFS against the real filesystem.
type Host struct {
	unitRoots  []string
	wgQuickDir string
	sysctlDir  string
}

// New returns an adapter over the host's filesystem.
func New() *Host {
	return &Host{unitRoots: unitRoots, wgQuickDir: wgQuickDir, sysctlDir: sysctlDir}
}

// NewAt returns an adapter rooted at prefix, for tests that lay out a tree.
func NewAt(prefix string) *Host {
	roots := make([]string, 0, len(unitRoots))
	for _, r := range unitRoots {
		roots = append(roots, filepath.Join(prefix, r))
	}
	return &Host{
		unitRoots:  roots,
		wgQuickDir: filepath.Join(prefix, wgQuickDir),
		sysctlDir:  filepath.Join(prefix, sysctlDir),
	}
}

// WgQuickUnit reports whether wg-quick@<iface>.service is enabled, determined
// from the presence of a symlink under a target's .wants directory.
//
// When no root can be read the answer is UnitUndetermined, which REQ-DIA-050
// requires the report to surface as UNKNOWN rather than as a pass.
func (h *Host) WgQuickUnit(iface string) (platform.UnitState, error) {
	unit := "wg-quick@" + iface + ".service"
	readable := false
	templatePresent := false

	for _, root := range h.unitRoots {
		entries, err := os.ReadDir(root)
		if err != nil {
			if errors.Is(err, fs.ErrNotExist) {
				// A root that does not exist is an answer, not a failure.
				readable = true
				continue
			}
			continue
		}
		readable = true

		for _, e := range entries {
			if e.Name() == "wg-quick@.service" {
				templatePresent = true
			}
			if !strings.HasSuffix(e.Name(), ".wants") {
				continue
			}
			if _, err := os.Lstat(filepath.Join(root, e.Name(), unit)); err == nil {
				return platform.UnitEnabled, nil
			}
		}
	}

	switch {
	case !readable:
		return platform.UnitUndetermined, nil
	case templatePresent:
		return platform.UnitDisabled, nil
	default:
		return platform.UnitAbsent, nil
	}
}

// WgQuickConfig reports the unsupported directives of the interface's
// configuration file. It never returns a value read from the file.
func (h *Host) WgQuickConfig(iface string) (platform.WgQuickConfig, error) {
	path := filepath.Join(h.wgQuickDir, iface+".conf")
	out := platform.WgQuickConfig{Path: path}

	f, err := os.Open(path)
	if err != nil {
		if errors.Is(err, fs.ErrNotExist) {
			return out, nil
		}
		// The file exists and cannot be read. REQ-DIA-046 makes that a warning:
		// every field comes from the kernel, so the only loss is the directive
		// warnings themselves.
		out.Present = true
		out.Unparseable = true
		return out, nil
	}
	defer f.Close()
	out.Present = true

	seen := map[string]bool{}
	sc := bufio.NewScanner(f)
	for sc.Scan() {
		line := strings.TrimSpace(sc.Text())
		if line == "" || strings.HasPrefix(line, "#") || strings.HasPrefix(line, "[") {
			continue
		}
		key, _, ok := strings.Cut(line, "=")
		if !ok {
			continue
		}
		key = strings.TrimSpace(key)
		for _, u := range unsupported {
			if strings.EqualFold(key, u) && !seen[u] {
				seen[u] = true
				out.Directives = append(out.Directives, u)
			}
		}
	}
	if err := sc.Err(); err != nil {
		out.Unparseable = true
	}
	return out, nil
}

// ForwardingSysctl reads the interface's forwarding value. The node exists only
// while the link does, so an absent file is reported as an empty baseline
// rather than an error: there is then nothing for REQ-FWD-024 to restore.
func (h *Host) ForwardingSysctl(iface string) (string, error) {
	b, err := os.ReadFile(filepath.Join(h.sysctlDir, iface, "forwarding"))
	if err != nil {
		if errors.Is(err, fs.ErrNotExist) {
			return "", nil
		}
		return "", nil
	}
	return strings.TrimSpace(string(b)), nil
}
