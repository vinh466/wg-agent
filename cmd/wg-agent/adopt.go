package main

import (
	"crypto/rand"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"strings"
	"time"

	"wg-agent/internal/model"
	"wg-agent/internal/platform/hostfs"
	"wg-agent/internal/platform/link"
	"wg-agent/internal/platform/wg"
	"wg-agent/internal/service"
	"wg-agent/internal/store"
)

// adoptCmd implements the `adopt` subcommand of REQ-CLI-001.
//
// REQ-CLI-002 puts it among the subcommands that reach the store directly, so
// it works before the agent has ever started. REQ-RCN-007 keeps that safe: the
// store lock fails while the agent holds it.
func adoptCmd(args []string, stdout, stderr io.Writer) int {
	fs := flag.NewFlagSet("adopt", flag.ContinueOnError)
	fs.SetOutput(stderr)
	sf := addStoreFlags(fs)
	output := fs.String("output", "text", "output format: text or json")
	dryRun := fs.Bool("dry-run", false, "print the spec that would be stored and write nothing")
	intra := fs.String("intra", "", "intra_interface axis: allow or deny")
	inter := fs.String("inter", "", "inter_interface axis: allow, deny or allow-list")
	external := fs.String("external", "", "external axis: allow or deny")
	peerIfaces := fs.String("allowed-peer-interfaces", "",
		"comma-separated interfaces, when --inter=allow-list")
	manageRoutes := fs.String("manage-routes", "", "true or false")
	nat := fs.Bool("nat", false, "enable source NAT for traffic leaving the interface")
	natOut := fs.String("nat-out-interface", "", "uplink for the masquerade rule")
	uplink := fs.Bool("enable-uplink-forwarding", false,
		"permit writing the uplink forwarding sysctl, required by --external=allow")

	fs.Usage = func() {
		fmt.Fprintln(stderr, "usage: wg-agent adopt <interface> --intra=... --inter=... --external=... --manage-routes=...")
		fmt.Fprintln(stderr, "\nBrings an existing WireGuard interface under management, keeping its key")
		fmt.Fprintln(stderr, "and its peers. The policy flags are required: the kernel holds no forward")
		fmt.Fprintln(stderr, "policy and no routing intent, so adoption cannot infer them, and a default")
		fmt.Fprintln(stderr, "would change what a live node does. Run `wg-agent doctor` first.")
		fmt.Fprintln(stderr)
		fs.PrintDefaults()
	}
	name, rest := splitName(args)
	if err := fs.Parse(rest); err != nil {
		return 2
	}
	if name == "" {
		name = fs.Arg(0)
	}
	if name == "" || fs.NArg() > 1 {
		fmt.Fprintln(stderr, "adopt takes exactly one interface name")
		fs.Usage()
		return 2
	}

	req, code := adoptRequest(name, *intra, *inter, *external, *peerIfaces, *manageRoutes,
		*nat, *natOut, *uplink, stderr)
	if code != 0 {
		return code
	}

	device, err := wg.New()
	if err != nil {
		fmt.Fprintf(stderr, "cannot read WireGuard devices: %v\n", err)
		return 1
	}
	defer device.Close()

	storePath, err := sf.resolveStorePath()
	if err != nil {
		fmt.Fprintf(stderr, "configuration: %v\n", err)
		return 1
	}

	st, code := openStoreForWrite(storePath, stderr)
	if st == nil {
		return code
	}
	defer st.Close()

	res, err := service.Adopt{
		Device: device,
		Link:   link.New(),
		HostFS: hostfs.New(),
		Now:    func() string { return time.Now().UTC().Format(time.RFC3339) },
		NewID:  newInstanceID,
	}.Do(st, req, *dryRun)
	if err != nil {
		return reportServiceError(err, stderr)
	}

	if *output == "json" {
		return writeJSON(stdout, stderr, res)
	}
	writeAdoptText(stdout, res)
	return 0
}

// releaseCmd implements the `release` subcommand of REQ-CLI-001, which is
// REQ-RCN-069: the interface leaves desired state and its link keeps running.
func releaseCmd(args []string, stdout, stderr io.Writer) int {
	fs := flag.NewFlagSet("release", flag.ContinueOnError)
	fs.SetOutput(stderr)
	sf := addStoreFlags(fs)
	output := fs.String("output", "text", "output format: text or json")
	fs.Usage = func() {
		fmt.Fprintln(stderr, "usage: wg-agent release <interface>")
		fmt.Fprintln(stderr, "\nStops managing an interface, leaving its link in the kernel. Only an")
		fmt.Fprintln(stderr, "adopted interface can be released; one the agent created leaves desired")
		fmt.Fprintln(stderr, "state through delete.")
		fs.PrintDefaults()
	}
	name, rest := splitName(args)
	if err := fs.Parse(rest); err != nil {
		return 2
	}
	if name == "" {
		name = fs.Arg(0)
	}
	if name == "" || fs.NArg() > 1 {
		fmt.Fprintln(stderr, "release takes exactly one interface name")
		fs.Usage()
		return 2
	}

	storePath, err := sf.resolveStorePath()
	if err != nil {
		fmt.Fprintf(stderr, "configuration: %v\n", err)
		return 1
	}

	st, code := openStoreForWrite(storePath, stderr)
	if st == nil {
		return code
	}
	defer st.Close()

	res, err := service.Release{
		RestoreForwarding: hostfs.New().SetForwardingSysctl,
	}.Do(st, name)
	if err != nil {
		return reportServiceError(err, stderr)
	}

	if *output == "json" {
		return writeJSON(stdout, stderr, res)
	}
	fmt.Fprintf(stdout, "%s released. Its link is still running and reports FOREIGN.\n", res.Name)
	if res.PeersRemoved > 0 {
		fmt.Fprintf(stdout, "  %d peer spec(s) removed from desired state; the kernel keeps its peers.\n",
			res.PeersRemoved)
	}
	if res.ForwardingRestored != "" {
		fmt.Fprintf(stdout, "  forwarding sysctl restored to %s\n", res.ForwardingRestored)
	}
	return 0
}

// openStoreForWrite acquires the exclusive lock of REQ-RCN-006. A held lock
// means the agent is serving, which is the case REQ-RCN-007 refuses.
func openStoreForWrite(path string, stderr io.Writer) (*store.Store, int) {
	st, err := store.Open(path)
	if err == nil {
		return st, 0
	}
	if errors.Is(err, store.ErrLocked) {
		fmt.Fprintf(stderr, "the store at %s is locked by another process.\n", path)
		fmt.Fprintln(stderr, "This command writes desired state directly, so the agent must be stopped:")
		fmt.Fprintln(stderr, "  systemctl stop wg-agent")
		return nil, 1
	}
	fmt.Fprintf(stderr, "cannot open the store: %v\n", err)
	return nil, 1
}

// reportServiceError prints a reason code and the findings behind it, so an
// operator sees what to fix rather than a bare failure — REQ-CLI-021 supplies
// the non-zero status.
func reportServiceError(err error, stderr io.Writer) int {
	var se *service.Error
	if !errors.As(err, &se) {
		fmt.Fprintf(stderr, "%v\n", err)
		return 1
	}
	fmt.Fprintf(stderr, "%s: %s\n", se.Reason, se.Message)
	for _, f := range se.Findings {
		if f.Result != service.Fail {
			continue
		}
		fmt.Fprintf(stderr, "  %-7s %s\n", f.Result, f.Observed)
		fmt.Fprintf(stderr, "          → %s\n", f.Hint)
	}
	return 1
}

func writeJSON(stdout, stderr io.Writer, v any) int {
	enc := json.NewEncoder(stdout)
	enc.SetIndent("", "  ")
	if err := enc.Encode(v); err != nil {
		fmt.Fprintf(stderr, "cannot write output: %v\n", err)
		return 1
	}
	return 0
}

// writeAdoptText renders the result. REQ-CLI-022 forbids a key in the output,
// so the stored private and preshared keys are reported as present only.
func writeAdoptText(w io.Writer, res *service.AdoptResult) {
	verb := "adopted"
	if res.DryRun {
		verb = "would adopt"
	}
	fmt.Fprintf(w, "%s %s\n", verb, res.Name)
	fmt.Fprintf(w, "  private key   %s (kept, not regenerated)\n", present(res.Spec.PrivateKey != ""))
	fmt.Fprintf(w, "  listen port   %d\n", res.Spec.ListenPort)
	fmt.Fprintf(w, "  addresses     %s\n", join(res.Spec.Addresses))
	fmt.Fprintf(w, "  mtu           %d\n", res.Spec.MTU)
	fmt.Fprintf(w, "  enabled       %t\n", res.Spec.Enabled)
	fmt.Fprintf(w, "  manage routes %t\n", res.Spec.ManageRoutes)
	fp := res.Spec.ForwardPolicy
	fmt.Fprintf(w, "  forward       intra=%s inter=%s external=%s\n",
		fp.IntraInterface, fp.InterInterface, fp.External)
	fmt.Fprintf(w, "  peers         %d\n", len(res.Peers))
	for _, p := range res.Peers {
		fmt.Fprintf(w, "    %s  allowed-ips %s  psk %s\n",
			p.PublicKey, join(p.Spec.AllowedIPs), present(p.Spec.PresharedKey != ""))
	}

	var warnings []service.Finding
	for _, f := range res.Findings {
		if f.Result == service.Warn || f.Result == service.Unknown {
			warnings = append(warnings, f)
		}
	}
	if len(warnings) > 0 {
		fmt.Fprintln(w, "  Carried over with warnings:")
		for _, f := range warnings {
			fmt.Fprintf(w, "    %-7s %s\n", f.Result, f.Observed)
			fmt.Fprintf(w, "            → %s\n", f.Hint)
		}
	}
	if res.DryRun {
		fmt.Fprintln(w, "\nNothing was written. Repeat without --dry-run to adopt.")
	}
}

// adoptRequest turns the flags into the request of REQ-RCN-066. A missing flag
// is left nil so the service layer produces ADOPTION_FIELD_REQUIRED rather than
// the CLI inventing a default.
func adoptRequest(
	name, intra, inter, external, peerIfaces, manageRoutes string,
	nat bool, natOut string, uplink bool, stderr io.Writer,
) (service.AdoptRequest, int) {
	req := service.AdoptRequest{Name: name}

	if intra != "" || inter != "" || external != "" {
		fp := &model.ForwardPolicySpec{}
		for _, f := range []struct {
			flag  string
			value string
			dst   *model.Axis
		}{
			{"--intra", intra, &fp.IntraInterface},
			{"--inter", inter, &fp.InterInterface},
			{"--external", external, &fp.External},
		} {
			axis, ok := parseAxis(f.value)
			if !ok {
				fmt.Fprintf(stderr, "%s: %q is not allow, deny or allow-list\n", f.flag, f.value)
				return req, 2
			}
			*f.dst = axis
		}
		fp.AllowedPeerInterfaces = splitList(peerIfaces)
		req.ForwardPolicy = fp
	}

	switch manageRoutes {
	case "true":
		v := true
		req.ManageRoutes = &v
	case "false":
		v := false
		req.ManageRoutes = &v
	case "":
		// Left nil deliberately.
	default:
		fmt.Fprintf(stderr, "--manage-routes: %q is not true or false\n", manageRoutes)
		return req, 2
	}

	// NAT is a struct rather than a pointer field on the command line: its
	// absence cannot be distinguished from its zero value, so the flags always
	// produce one and REQ-RCN-066's rejection rests on the policy and routing
	// flags.
	req.NAT = &model.NatSpec{
		Enabled:                nat,
		MasqueradeOutInterface: natOut,
		EnableUplinkForwarding: uplink,
	}
	return req, 0
}

func parseAxis(v string) (model.Axis, bool) {
	switch v {
	case "allow":
		return model.Allow, true
	case "deny":
		return model.Deny, true
	case "allow-list":
		return model.AllowList, true
	}
	return "", false
}

func splitList(v string) []string {
	if v == "" {
		return nil
	}
	var out []string
	start := 0
	for i := 0; i <= len(v); i++ {
		if i == len(v) || v[i] == ',' {
			if s := v[start:i]; s != "" {
				out = append(out, s)
			}
			start = i + 1
		}
	}
	return out
}

// newInstanceID returns a version 4 UUID, which SPEC-01 gives as the type of
// instance_id. crypto/rand keeps the dependency list at the two libraries
// docs/00-overview/architecture.md names.
func newInstanceID() string {
	var b [16]byte
	if _, err := rand.Read(b[:]); err != nil {
		// crypto/rand does not fail on Linux. Falling back to a timestamp would
		// break the uniqueness REQ-RES-026 reads, so the error is fatal.
		panic("cannot read random bytes for instance_id: " + err.Error())
	}
	b[6] = (b[6] & 0x0f) | 0x40 // version 4
	b[8] = (b[8] & 0x3f) | 0x80 // variant 10
	return fmt.Sprintf("%x-%x-%x-%x-%x", b[0:4], b[4:6], b[6:8], b[8:10], b[10:16])
}

// splitName lifts a leading interface name out of the argument list.
//
// Go's flag package stops parsing at the first argument that is not a flag, so
// an interface name written before the flags would leave every one of them
// unparsed. Taking the name first lets it appear on either side, which is what
// an operator will type.
func splitName(args []string) (string, []string) {
	if len(args) > 0 && !strings.HasPrefix(args[0], "-") {
		return args[0], args[1:]
	}
	return "", args
}
