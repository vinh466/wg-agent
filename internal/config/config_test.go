package config_test

import (
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"wg-agent/internal/config"
	"wg-agent/internal/model"
)

func write(t *testing.T, body string) string {
	t.Helper()
	p := filepath.Join(t.TempDir(), "config.yaml")
	if err := os.WriteFile(p, []byte(body), 0o600); err != nil {
		t.Fatalf("write config: %v", err)
	}
	return p
}

// REQ-CFG-040: an absent file leaves every key at its default rather than
// failing. That is what lets `token add` issue the first token on a node with
// no configuration yet.
func TestLoad_AbsentFileIsDefaults_REQ_CFG_040(t *testing.T) {
	cfg, err := config.Load(filepath.Join(t.TempDir(), "nothing-here.yaml"))
	if err != nil {
		t.Fatalf("an absent file must not fail: %v", err)
	}
	want := config.Default()
	if cfg.State.Path != want.State.Path {
		t.Errorf("state.path = %q, want the default %q", cfg.State.Path, want.State.Path)
	}
	if cfg.Reconcile.Interval != 30*time.Second {
		t.Errorf("reconcile.interval = %v, want 30s", cfg.Reconcile.Interval)
	}
	if cfg.Server.HTTP.Enabled {
		t.Error("the HTTP listener must default to off — REQ-CFG-022")
	}
}

// REQ-CFG-037: the file supplies the keys it names and leaves the rest alone.
func TestLoad_FileOverridesDefaults_REQ_CFG_037(t *testing.T) {
	p := write(t, `
state:
  path: /tmp/other.db
reconcile:
  interval: 5s
`)
	cfg, err := config.Load(p)
	if err != nil {
		t.Fatalf("load: %v", err)
	}
	if cfg.State.Path != "/tmp/other.db" {
		t.Errorf("state.path = %q", cfg.State.Path)
	}
	if cfg.Reconcile.Interval != 5*time.Second {
		t.Errorf("reconcile.interval = %v, want 5s", cfg.Reconcile.Interval)
	}
	// Untouched keys keep their defaults.
	if cfg.Reconcile.BackoffMax != 60*time.Second {
		t.Errorf("reconcile.backoff_max = %v, want the default 60s", cfg.Reconcile.BackoffMax)
	}
}

// REQ-CFG-002: an unrecognized key refuses the start rather than being ignored.
// A silently ignored key is how an operator comes to believe a setting is in
// effect when it is not.
func TestLoad_UnknownKeyRefusesStart_REQ_CFG_002(t *testing.T) {
	p := write(t, "state:\n  path: /tmp/x.db\n  pathh: /tmp/typo.db\n")
	_, err := config.Load(p)
	if err == nil {
		t.Fatal("an unrecognized key must fail")
	}
	if !errors.Is(err, config.ErrUnknownKey) {
		t.Errorf("error = %v, want it to wrap ErrUnknownKey", err)
	}
	if !strings.Contains(err.Error(), "pathh") {
		t.Errorf("error must name the offending key, got %v", err)
	}
}

// An unrecognized top-level section is refused the same way.
func TestLoad_UnknownSectionRefusesStart_REQ_CFG_002(t *testing.T) {
	p := write(t, "telemetry:\n  enabled: true\n")
	if _, err := config.Load(p); !errors.Is(err, config.ErrUnknownKey) {
		t.Errorf("error = %v, want ErrUnknownKey", err)
	}
}

// An empty file is a file with no keys, which REQ-CFG-040 treats as defaults.
func TestLoad_EmptyFileIsDefaults_REQ_CFG_040(t *testing.T) {
	cfg, err := config.Load(write(t, ""))
	if err != nil {
		t.Fatalf("an empty file must not fail: %v", err)
	}
	if cfg.State.Path != config.Default().State.Path {
		t.Errorf("state.path = %q, want the default", cfg.State.Path)
	}
}

// REQ-CFG-041 fixes the transformation, so two implementations cannot disagree
// about a variable an operator has already set.
func TestEnvName_DottedPathTransformation_REQ_CFG_041(t *testing.T) {
	cases := map[string]string{
		"server.http.address":                     "WG_AGENT_SERVER_HTTP_ADDRESS",
		"state.path":                              "WG_AGENT_STATE_PATH",
		"reconcile.backoff_min":                   "WG_AGENT_RECONCILE_BACKOFF_MIN",
		"metrics.per_peer":                        "WG_AGENT_METRICS_PER_PEER",
		"defaults.forward_policy.intra_interface": "WG_AGENT_DEFAULTS_FORWARD_POLICY_INTRA_INTERFACE",
	}
	for path, want := range cases {
		if got := config.EnvName(path); got != want {
			t.Errorf("EnvName(%q) = %q, want %q", path, got, want)
		}
	}
}

// REQ-CFG-001: every key is overridable. The set is derived from the struct, so
// a field added without an override cannot slip through.
func TestKeys_EveryKeyHasAnOverride_REQ_CFG_001(t *testing.T) {
	keys := config.Keys()
	if len(keys) < 20 {
		t.Fatalf("only %d keys derived, which cannot cover the sample in SPEC-09", len(keys))
	}

	// Spot-check that the set names one key from every section, so a section
	// that stopped being walked would show up.
	for _, want := range []string{
		"node.endpoint",
		"server.unix_socket",
		"server.http.enabled",
		"security.token_file",
		"state.path",
		"reconcile.interval",
		"runtime.peer_online_threshold",
		"defaults.mtu",
		"metrics.address",
		"audit.path",
		"log.level",
	} {
		found := false
		for _, k := range keys {
			if k == want {
				found = true
				break
			}
		}
		if !found {
			t.Errorf("key %q is not overridable", want)
		}
	}
}

// The environment overlays the file — REQ-CFG-001.
func TestLoad_EnvOverridesFile_REQ_CFG_001(t *testing.T) {
	p := write(t, "state:\n  path: /from/file.db\nreconcile:\n  interval: 5s\n")

	t.Setenv("WG_AGENT_STATE_PATH", "/from/env.db")
	t.Setenv("WG_AGENT_RECONCILE_INTERVAL", "45s")
	t.Setenv("WG_AGENT_METRICS_PER_PEER", "false")
	t.Setenv("WG_AGENT_DEFAULTS_MTU", "1380")

	cfg, err := config.Load(p)
	if err != nil {
		t.Fatalf("load: %v", err)
	}
	if cfg.State.Path != "/from/env.db" {
		t.Errorf("state.path = %q, want the environment to win", cfg.State.Path)
	}
	if cfg.Reconcile.Interval != 45*time.Second {
		t.Errorf("reconcile.interval = %v, want 45s", cfg.Reconcile.Interval)
	}
	if cfg.Metrics.PerPeer {
		t.Error("metrics.per_peer must be false")
	}
	if cfg.Defaults.MTU != 1380 {
		t.Errorf("defaults.mtu = %d, want 1380", cfg.Defaults.MTU)
	}
}

// An environment value that cannot be parsed names the variable, so the
// operator can find it.
func TestLoad_UnparseableEnvValueNamesTheVariable_REQ_CFG_001(t *testing.T) {
	t.Setenv("WG_AGENT_RECONCILE_INTERVAL", "half an hour")
	_, err := config.Load(filepath.Join(t.TempDir(), "absent.yaml"))
	if err == nil {
		t.Fatal("an unparseable duration must fail")
	}
	if !strings.Contains(err.Error(), "WG_AGENT_RECONCILE_INTERVAL") {
		t.Errorf("error must name the variable, got %v", err)
	}
}

// REQ-SEC-070, check 7 of REQ-API-050: an enabled HTTP listener on a
// non-loopback address refuses the start with NON_LOOPBACK_BIND.
func TestValidate_NonLoopbackHTTPRefused_REQ_SEC_070(t *testing.T) {
	cases := []string{
		"10.100.0.1:9585",
		"0.0.0.0:9585",
		":9585",
		"example.internal:9585",
	}
	for _, addr := range cases {
		cfg := config.Default()
		cfg.Server.HTTP.Enabled = true
		cfg.Server.HTTP.Address = addr

		err := cfg.Validate()
		if err == nil {
			t.Errorf("%q was accepted", addr)
			continue
		}
		var e *config.Error
		if !errors.As(err, &e) || e.Reason != config.ReasonNonLoopbackBind {
			t.Errorf("%q gave %v, want NON_LOOPBACK_BIND", addr, err)
		}
	}
}

// A loopback address is accepted, in both families.
func TestValidate_LoopbackHTTPAccepted_REQ_SEC_070(t *testing.T) {
	for _, addr := range []string{"127.0.0.1:9585", "127.0.0.53:9585", "[::1]:9585"} {
		cfg := config.Default()
		cfg.Server.HTTP.Enabled = true
		cfg.Server.HTTP.Address = addr
		if err := cfg.Validate(); err != nil {
			t.Errorf("%q was refused: %v", addr, err)
		}
	}
}

// A disabled listener is not checked: the address is not bound, so there is
// nothing for REQ-SEC-070 to refuse.
func TestValidate_DisabledHTTPIsNotChecked_REQ_SEC_070(t *testing.T) {
	cfg := config.Default()
	cfg.Server.HTTP.Enabled = false
	cfg.Server.HTTP.Address = "0.0.0.0:9585"
	if err := cfg.Validate(); err != nil {
		t.Errorf("a disabled listener must not be checked: %v", err)
	}
}

// REQ-SEC-083, check 9: the metrics listener carries the same rule, because
// REQ-OBS-002 makes it name every public key and handshake time.
func TestValidate_NonLoopbackMetricsRefused_REQ_SEC_083(t *testing.T) {
	cfg := config.Default()
	cfg.Metrics.Enabled = true
	cfg.Metrics.Address = "0.0.0.0:9586"

	err := cfg.Validate()
	var e *config.Error
	if !errors.As(err, &e) || e.Reason != config.ReasonNonLoopbackBind {
		t.Errorf("error = %v, want NON_LOOPBACK_BIND", err)
	}
}

// An axis outside the closed set of SPEC-02 section 2 refuses the start rather
// than becoming an interface spec no requirement describes.
func TestValidate_InvalidAxisRefused(t *testing.T) {
	cfg := config.Default()
	cfg.Defaults.ForwardPolicy.IntraInterface = model.Axis("MAYBE")
	if err := cfg.Validate(); err == nil {
		t.Fatal("an invalid axis was accepted")
	}
}

// The default configuration is itself valid. Without this a stock agent would
// refuse to start.
func TestValidate_DefaultIsValid(t *testing.T) {
	if err := config.Default().Validate(); err != nil {
		t.Fatalf("the default configuration must be valid: %v", err)
	}
}

// Every key in the SPEC-09 sample parses. A key in the documentation that the
// build rejects would be worse than a missing one: the operator would be
// following the specification and the agent would refuse to start.
func TestLoad_TheSpecSampleParses_REQ_CFG_037(t *testing.T) {
	p := write(t, `
node:
  endpoint: ""

server:
  unix_socket: /run/wg-agent/wg-agent.sock
  socket_mode: "0660"
  socket_group: wg-agent
  http:
    enabled: false
    address: "127.0.0.1:9585"

security:
  allow_server_generated_keys: true
  token_file: /etc/wg-agent/tokens.yaml

state:
  path: /var/lib/wg-agent/state.db

reconcile:
  interval: 30s
  apply_timeout: 10s
  backoff_min: 1s
  backoff_max: 60s

runtime:
  peer_online_threshold: 180s

defaults:
  forward_policy:
    intra_interface: ALLOW
    inter_interface: DENY
    external: DENY
  mtu: 1420

metrics:
  enabled: true
  address: "127.0.0.1:9586"
  per_peer: true

audit:
  enabled: true
  path: /var/log/wg-agent/audit.jsonl

log:
  level: info
  format: json
`)
	cfg, err := config.Load(p)
	if err != nil {
		t.Fatalf("the sample in SPEC-09 must parse: %v", err)
	}
	if cfg.Defaults.ForwardPolicy.IntraInterface != model.Allow {
		t.Errorf("intra_interface = %q, want ALLOW", cfg.Defaults.ForwardPolicy.IntraInterface)
	}
	if cfg.Runtime.PeerOnlineThreshold != 180*time.Second {
		t.Errorf("peer_online_threshold = %v, want 180s", cfg.Runtime.PeerOnlineThreshold)
	}
}
