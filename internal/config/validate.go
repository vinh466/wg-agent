package config

import (
	"fmt"
	"net"
	"net/netip"

	"wg-agent/internal/model"
)

// Reason codes this package produces, from the closed set of REQ-API-041.
const (
	ReasonNonLoopbackBind = "NON_LOOPBACK_BIND"
	ReasonInvalidArgument = "INVALID_ARGUMENT"
)

// Error carries a reason code, so an installer or a unit log can branch on the
// cause of a startup failure without matching message text — the purpose
// REQ-API-050 states for the codes in its table.
type Error struct {
	Reason  string
	Message string
}

func (e *Error) Error() string { return e.Reason + ": " + e.Message }

func failf(reason, format string, args ...any) error {
	return &Error{Reason: reason, Message: fmt.Sprintf(format, args...)}
}

// Validate performs the configuration half of the startup checks of
// REQ-API-050: check 7 for the HTTP listener and check 9 for the metrics
// listener. The checks that read the kernel or the store live in
// internal/startup, because they are not questions about the file.
func (c Config) Validate() error {
	// Check 7 — REQ-SEC-070.
	if c.Server.HTTP.Enabled {
		if err := requireLoopback("server.http.address", c.Server.HTTP.Address); err != nil {
			return err
		}
	}
	// Check 9 — REQ-SEC-083. The metrics endpoint names every public key and
	// handshake time under REQ-OBS-002, which is why it carries the same rule.
	if c.Metrics.Enabled {
		if err := requireLoopback("metrics.address", c.Metrics.Address); err != nil {
			return err
		}
	}

	for key, axis := range map[string]model.Axis{
		"defaults.forward_policy.intra_interface": c.Defaults.ForwardPolicy.IntraInterface,
		"defaults.forward_policy.inter_interface": c.Defaults.ForwardPolicy.InterInterface,
		"defaults.forward_policy.external":        c.Defaults.ForwardPolicy.External,
	} {
		if !axis.Valid() {
			return failf(ReasonInvalidArgument,
				"%s is %q; SPEC-02 section 2 fixes the set to ALLOW, DENY and ALLOW_LIST",
				key, axis)
		}
	}

	if c.State.Path == "" {
		return failf(ReasonInvalidArgument, "state.path must not be empty")
	}
	return nil
}

// requireLoopback implements the bind test of REQ-SEC-070 and REQ-SEC-083.
//
// An empty host is rejected rather than treated as loopback: `:9585` binds
// every address, which is the case the requirement exists to refuse, and the
// shorthand is easy to write by accident.
func requireLoopback(key, addr string) error {
	host, port, err := net.SplitHostPort(addr)
	if err != nil {
		return failf(ReasonInvalidArgument, "%s is %q: %v", key, addr, err)
	}
	if port == "" {
		return failf(ReasonInvalidArgument, "%s is %q: no port", key, addr)
	}
	if host == "" {
		return failf(ReasonNonLoopbackBind,
			"%s is %q, which binds every address; only a loopback address is permitted",
			key, addr)
	}

	ip, err := netip.ParseAddr(host)
	if err != nil {
		// A hostname could resolve anywhere, and could resolve differently
		// later. The requirement is about the address bound, so a name that
		// cannot be checked once and for all is refused.
		return failf(ReasonNonLoopbackBind,
			"%s is %q; a literal loopback address is required rather than a hostname",
			key, addr)
	}
	if !ip.IsLoopback() {
		return failf(ReasonNonLoopbackBind,
			"%s is %q, which is not a loopback address", key, addr)
	}
	return nil
}
