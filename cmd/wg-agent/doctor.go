package main

import (
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"os"

	"wg-agent/internal/model"
	"wg-agent/internal/platform"
	"wg-agent/internal/platform/hostfs"
	"wg-agent/internal/platform/link"
	"wg-agent/internal/platform/wg"
	"wg-agent/internal/service"
	"wg-agent/internal/store"
)

// doctorCmd implements the `doctor` subcommand of REQ-CLI-001.
//
// REQ-CLI-004 requires it to compute the report of REQ-DIA-040 from the kernel,
// the store and the filesystem directly rather than through the API, so it
// works on a node where the agent has never started: no listener, no token and
// no running process are involved.
func doctorCmd(args []string, stdout, stderr io.Writer) int {
	fs := flag.NewFlagSet("doctor", flag.ContinueOnError)
	fs.SetOutput(stderr)
	output := fs.String("output", "text", "output format: text or json")
	sf := addStoreFlags(fs)
	fs.Usage = func() {
		fmt.Fprintln(stderr, "usage: wg-agent doctor [--output text|json] [--store PATH]")
		fmt.Fprintln(stderr, "\nReports what adopting each existing WireGuard interface would produce,")
		fmt.Fprintln(stderr, "and what stands in the way.")
		fs.PrintDefaults()
	}
	if err := fs.Parse(args); err != nil {
		return 2
	}
	if *output != "text" && *output != "json" {
		fmt.Fprintf(stderr, "unknown output format %q\n", *output)
		return 2
	}

	device, err := wg.New()
	if err != nil {
		fmt.Fprintf(stderr, "cannot read WireGuard devices: %v\n", err)
		fmt.Fprintln(stderr, "The wireguard module must be loaded and CAP_NET_ADMIN held.")
		return 1
	}
	defer device.Close()

	storePath, err := sf.resolveStorePath()
	if err != nil {
		fmt.Fprintf(stderr, "configuration: %v\n", err)
		return 1
	}

	desired, err := store.Read(storePath)
	if err != nil {
		fmt.Fprintf(stderr, "cannot read the store: %v\n", err)
		return 1
	}

	report, err := service.Readiness{
		Device:  device,
		Link:    link.New(),
		HostFS:  hostfs.New(),
		Desired: desired,
	}.Build()
	if err != nil {
		fmt.Fprintf(stderr, "cannot build the readiness report: %v\n", err)
		return 1
	}

	if *output == "json" {
		enc := json.NewEncoder(stdout)
		enc.SetIndent("", "  ")
		if err := enc.Encode(report); err != nil {
			fmt.Fprintf(stderr, "cannot write the report: %v\n", err)
			return 1
		}
	} else {
		writeText(stdout, report)
	}

	// REQ-CLI-021 requires a non-zero status on failure. A blocking finding is
	// the command reporting that adoption is refused, so the exit status says
	// so too and a script does not have to parse the output.
	if report.Blocking() {
		return 1
	}
	return 0
}

// writeText renders the report for a human. REQ-CLI-005 requires the hint of
// every finding printed, and REQ-CLI-022 forbids a key or token in the output —
// no key value reaches this function, because platform.Key does not render one.
func writeText(w io.Writer, r service.Report) {
	if len(r.Interfaces) == 0 {
		fmt.Fprintln(w, "No WireGuard interface found on this node.")
		return
	}

	for i, iface := range r.Interfaces {
		if i > 0 {
			fmt.Fprintln(w)
		}
		fmt.Fprintf(w, "%s  [%s]\n", iface.Name, iface.Ownership)

		switch iface.Ownership {
		case model.Managed:
			fmt.Fprintln(w, "  Already under management. Adoption does not apply.")
			continue
		case model.Orphaned:
			fmt.Fprintln(w, "  A deletion record names this link. Remove it with `ip link del`,")
			fmt.Fprintln(w, "  and the record clears itself on the next reconcile pass.")
			continue
		}

		if s := iface.Spec; s != nil {
			fmt.Fprintln(w, "  Adoption would read from the kernel:")
			fmt.Fprintf(w, "    private key   %s\n", present(s.PrivateKeyPresent))
			fmt.Fprintf(w, "    listen port   %d\n", s.ListenPort)
			if s.Fwmark != 0 {
				fmt.Fprintf(w, "    fwmark        %#x\n", s.Fwmark)
			}
			fmt.Fprintf(w, "    addresses     %s\n", join(s.Addresses))
			fmt.Fprintf(w, "    mtu           %d\n", s.MTU)
			fmt.Fprintf(w, "    enabled       %t\n", s.Enabled)
			fmt.Fprintf(w, "    peers         %d\n", len(s.Peers))
			for _, p := range s.Peers {
				fmt.Fprintf(w, "      %s  allowed-ips %s  psk %s%s\n",
					p.PublicKey, join(p.AllowedIPs), present(p.PresharedKeyPresent),
					endpointNote(p.EndpointPresent))
			}
			fmt.Fprintln(w, "    forward policy, nat and manage_routes come from the adopt request")
		}

		fmt.Fprintln(w, "  Findings:")
		for _, f := range iface.Findings {
			fmt.Fprintf(w, "    %-7s %s\n", f.Result, f.Observed)
			fmt.Fprintf(w, "            → %s\n", f.Hint)
		}
	}

	fmt.Fprintln(w)
	if r.Blocking() {
		fmt.Fprintln(w, "At least one interface is blocked. Clear every FAIL above, then adopt.")
		return
	}
	fmt.Fprintln(w, "No blocking finding.")
}

func present(b bool) string {
	if b {
		return "present"
	}
	return "absent"
}

func endpointNote(b bool) string {
	if b {
		return "  endpoint present (not stored)"
	}
	return ""
}

func join(v []string) string {
	if len(v) == 0 {
		return "none"
	}
	out := v[0]
	for _, s := range v[1:] {
		out += ", " + s
	}
	return out
}

// interface assertions: the adapters satisfy the ports the report declares.
var (
	_ platform.Link         = (*link.Netlink)(nil)
	_ platform.HostFS       = (*hostfs.Host)(nil)
	_ platform.DesiredState = (*store.Snapshot)(nil)
	_                       = os.Stdout
)
