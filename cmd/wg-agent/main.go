// Command wg-agent is the node-level control plane agent for WireGuard.
//
// The subcommand surface is SPEC-12. Only the commands whose requirements are
// implemented are dispatched here; the rest report that plainly rather than
// pretending to work.
package main

import (
	"fmt"
	"io"
	"os"
	"runtime"
)

// version is set at build time with -ldflags. REQ-CLI-001 lists `version` as a
// subcommand that needs no running agent.
var (
	version = "dev"
	commit  = "unknown"
)

func main() {
	os.Exit(run(os.Args[1:], os.Stdout, os.Stderr))
}

func run(args []string, stdout, stderr io.Writer) int {
	if len(args) == 0 {
		usage(stderr)
		return 2
	}

	switch args[0] {
	case "doctor":
		return doctorCmd(args[1:], stdout, stderr)
	case "adopt":
		return adoptCmd(args[1:], stdout, stderr)
	case "release":
		return releaseCmd(args[1:], stdout, stderr)
	case "version":
		fmt.Fprintf(stdout, "wg-agent %s (%s, %s)\n", version, commit, runtime.Version())
		return 0
	case "-h", "--help", "help":
		usage(stdout)
		return 0
	default:
		fmt.Fprintf(stderr, "unknown subcommand %q\n\n", args[0])
		usage(stderr)
		return 2
	}
}

func usage(w io.Writer) {
	fmt.Fprintln(w, "usage: wg-agent <command> [flags]")
	fmt.Fprintln(w)
	fmt.Fprintln(w, "Implemented:")
	fmt.Fprintln(w, "  doctor    report what adopting each existing interface would produce")
	fmt.Fprintln(w, "  adopt     bring an existing interface under management, keeping its key")
	fmt.Fprintln(w, "  release   stop managing an interface, leaving its link running")
	fmt.Fprintln(w, "  version   print version, commit and Go version")
	fmt.Fprintln(w)
	fmt.Fprintln(w, "Specified, not yet built — see docs/20-spec/SPEC-12-cli.md:")
	fmt.Fprintln(w, "  serve, token, export, import, overview")
}
