// Package config loads the configuration file of SPEC-09 section 2.
//
// Three layers, in increasing precedence: the defaults below, the YAML file,
// and the environment variables of REQ-CFG-001. A flag naming a key sits above
// all three under REQ-CFG-042, which the command layer applies.
package config

import (
	"bytes"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"os"
	"strings"
	"time"

	"gopkg.in/yaml.v3"

	"wg-agent/internal/model"
)

// DefaultPath is the path REQ-CFG-037 fixes. REQ-CFG-039 lets `--config`
// override it.
const DefaultPath = "/etc/wg-agent/config.yaml"

// EnvPrefix is the prefix of REQ-CFG-001.
const EnvPrefix = "WG_AGENT_"

// Config is the file of SPEC-09 section 2. Every field carries a yaml tag,
// because the tag is what REQ-CFG-041 derives an environment variable name
// from — a field without one would have no override.
type Config struct {
	Node      Node      `yaml:"node"`
	Server    Server    `yaml:"server"`
	Security  Security  `yaml:"security"`
	State     State     `yaml:"state"`
	Reconcile Reconcile `yaml:"reconcile"`
	Runtime   Runtime   `yaml:"runtime"`
	Defaults  Defaults  `yaml:"defaults"`
	Metrics   Metrics   `yaml:"metrics"`
	Audit     Audit     `yaml:"audit"`
	Log       Log       `yaml:"log"`
}

// Node carries node-level identity. Endpoint is empty by default because no
// value the agent could choose would be right — REQ-KEY-038 asks the caller.
type Node struct {
	Endpoint string `yaml:"endpoint"`
}

type Server struct {
	UnixSocket  string `yaml:"unix_socket"`
	SocketMode  string `yaml:"socket_mode"`
	SocketGroup string `yaml:"socket_group"`
	HTTP        HTTP   `yaml:"http"`
}

type HTTP struct {
	Enabled bool   `yaml:"enabled"`
	Address string `yaml:"address"`
}

type Security struct {
	AllowServerGeneratedKeys bool   `yaml:"allow_server_generated_keys"`
	TokenFile                string `yaml:"token_file"`
}

type State struct {
	Path string `yaml:"path"`
}

type Reconcile struct {
	Interval     time.Duration `yaml:"interval"`
	ApplyTimeout time.Duration `yaml:"apply_timeout"`
	BackoffMin   time.Duration `yaml:"backoff_min"`
	BackoffMax   time.Duration `yaml:"backoff_max"`
}

type Runtime struct {
	PeerOnlineThreshold time.Duration `yaml:"peer_online_threshold"`
}

// Defaults are applied to a new interface when the caller omits them. They are
// not applied to an adopted one: REQ-RCN-066 requires the adoption request to
// supply them, because a default would be a guess about a live node.
type Defaults struct {
	ForwardPolicy ForwardPolicy `yaml:"forward_policy"`
	MTU           int           `yaml:"mtu"`
}

type ForwardPolicy struct {
	IntraInterface model.Axis `yaml:"intra_interface"`
	InterInterface model.Axis `yaml:"inter_interface"`
	External       model.Axis `yaml:"external"`
}

type Metrics struct {
	Enabled bool   `yaml:"enabled"`
	Address string `yaml:"address"`
	PerPeer bool   `yaml:"per_peer"`
}

type Audit struct {
	Enabled bool   `yaml:"enabled"`
	Path    string `yaml:"path"`
}

type Log struct {
	Level  string `yaml:"level"`
	Format string `yaml:"format"`
}

// Default returns the values of the sample in SPEC-09 section 2. REQ-CFG-040
// requires an absent file to leave every key here rather than fail.
func Default() Config {
	return Config{
		Server: Server{
			UnixSocket:  "/run/wg-agent/wg-agent.sock",
			SocketMode:  "0660",
			SocketGroup: "wg-agent",
			HTTP: HTTP{
				// REQ-CFG-022 keeps the listener off until an operator turns it
				// on, so the default is false and the address is loopback —
				// REQ-SEC-070 refuses anything else.
				Enabled: false,
				Address: "127.0.0.1:9585",
			},
		},
		Security: Security{
			AllowServerGeneratedKeys: true,
			TokenFile:                "/etc/wg-agent/tokens.yaml",
		},
		State: State{Path: "/var/lib/wg-agent/state.db"},
		Reconcile: Reconcile{
			Interval:     30 * time.Second,
			ApplyTimeout: 10 * time.Second,
			BackoffMin:   1 * time.Second,
			BackoffMax:   60 * time.Second,
		},
		Runtime: Runtime{PeerOnlineThreshold: 180 * time.Second},
		Defaults: Defaults{
			ForwardPolicy: ForwardPolicy{
				IntraInterface: model.Allow,
				InterInterface: model.Deny,
				External:       model.Deny,
			},
			MTU: 1420,
		},
		Metrics: Metrics{
			Enabled: true,
			Address: "127.0.0.1:9586",
			PerPeer: true,
		},
		Audit: Audit{Enabled: true, Path: "/var/log/wg-agent/audit.jsonl"},
		Log:   Log{Level: "info", Format: "json"},
	}
}

// ForwardPolicySpec converts the configured defaults into the shape SPEC-01
// stores. REQ-FWD-001 fixes the values; this carries whatever the operator
// configured in their place.
func (d Defaults) ForwardPolicySpec() model.ForwardPolicySpec {
	return model.ForwardPolicySpec{
		IntraInterface: d.ForwardPolicy.IntraInterface,
		InterInterface: d.ForwardPolicy.InterInterface,
		External:       d.ForwardPolicy.External,
	}
}

// ErrUnknownKey reports a key the build does not recognise — REQ-CFG-002.
var ErrUnknownKey = errors.New("unrecognized configuration key")

// Load reads path, applies the environment overrides and validates the result.
//
// An absent file is not an error: REQ-CFG-040 makes it the ordinary case, which
// is what lets `wg-agent token add` issue the first token on a node that has no
// configuration yet.
func Load(path string) (Config, error) {
	cfg := Default()

	b, err := os.ReadFile(path)
	switch {
	case err == nil:
		if err := decodeStrict(b, &cfg); err != nil {
			return Config{}, fmt.Errorf("parse %s: %w", path, err)
		}
	case errors.Is(err, fs.ErrNotExist):
		// REQ-CFG-040.
	default:
		return Config{}, fmt.Errorf("read %s: %w", path, err)
	}

	if err := applyEnv(&cfg, os.LookupEnv); err != nil {
		return Config{}, err
	}
	if err := cfg.Validate(); err != nil {
		return Config{}, err
	}
	return cfg, nil
}

// decodeStrict refuses an unrecognized key rather than ignoring it —
// REQ-CFG-002. A silently ignored key is how an operator ends up believing a
// setting is in effect when it is not.
func decodeStrict(b []byte, cfg *Config) error {
	dec := yaml.NewDecoder(bytes.NewReader(b))
	dec.KnownFields(true)
	if err := dec.Decode(cfg); err != nil {
		if errors.Is(err, io.EOF) {
			// An empty file leaves every default in place.
			return nil
		}
		// yaml.v3 reports an unknown key as a type error naming the field.
		// Wrapping it gives the caller one sentinel to test rather than a
		// string to match.
		if strings.Contains(err.Error(), "field") &&
			strings.Contains(err.Error(), "not found in type") {
			return fmt.Errorf("%w: %s", ErrUnknownKey, err)
		}
		return err
	}
	return nil
}
