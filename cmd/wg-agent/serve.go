package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"io"
	"log/slog"
	"os/signal"
	"syscall"

	"wg-agent/internal/platform/hostfs"
	"wg-agent/internal/platform/link"
	"wg-agent/internal/platform/wg"
	"wg-agent/internal/reconcile"
	"wg-agent/internal/store"
)

// serveCmd implements the `serve` subcommand of REQ-CLI-001.
//
// It holds the store lock for as long as it runs — REQ-RCN-006 — which is why
// `adopt` and `release` refuse to write while it is up.
//
// The API listeners are not here. Until they exist this command is the
// reconcile loop alone, which is the part that makes an adopted interface
// survive a reboot: without it the store is written and read by nothing.
func serveCmd(args []string, stdout, stderr io.Writer) int {
	fs := flag.NewFlagSet("serve", flag.ContinueOnError)
	fs.SetOutput(stderr)
	storePath := fs.String("store", store.DefaultPath, "path to the desired-state store")
	interval := fs.Duration("interval", reconcile.DefaultInterval,
		"periodic reconcile interval — reconcile.interval in SPEC-09")
	backoffMin := fs.Duration("backoff-min", reconcile.DefaultBackoffMin,
		"lower bound of the retry backoff")
	backoffMax := fs.Duration("backoff-max", reconcile.DefaultBackoffMax,
		"upper bound of the retry backoff")
	once := fs.Bool("once", false, "run a single reconcile pass and exit")
	logLevel := fs.String("log-level", "info", "debug, info, warn or error")

	fs.Usage = func() {
		fmt.Fprintln(stderr, "usage: wg-agent serve [flags]")
		fmt.Fprintln(stderr, "\nRuns the reconcile loop, holding an exclusive lock on the store.")
		fmt.Fprintln(stderr, "Stops on SIGTERM or SIGINT without touching any interface it manages.")
		fmt.Fprintln(stderr)
		fs.PrintDefaults()
	}
	if err := fs.Parse(args); err != nil {
		return 2
	}

	log := newLogger(stderr, *logLevel)

	st, err := store.Open(*storePath)
	if err != nil {
		if errors.Is(err, store.ErrLocked) {
			// REQ-RCN-007: the lock is the answer to "is the agent running",
			// and it guards the resource rather than standing in for it.
			fmt.Fprintf(stderr, "another process holds the store lock at %s; "+
				"an agent is already running\n", *storePath)
			return 1
		}
		fmt.Fprintf(stderr, "open store: %v\n", err)
		return 1
	}
	// REQ-API-073: the lock is released before exit, so a subcommand that
	// writes the store directly can run once the agent stops.
	defer func() { _ = st.Close() }()

	dev, err := wg.New()
	if err != nil {
		fmt.Fprintf(stderr, "open wgctrl: %v\n", err)
		return 1
	}
	defer func() { _ = dev.Close() }()

	nl := link.New()
	engine := &reconcile.Engine{
		Store:  st,
		Link:   nl,
		Device: dev,
		HostFS: hostfs.New(),
	}

	if *once {
		if err := engine.Pass(); err != nil {
			fmt.Fprintf(stderr, "reconcile: %v\n", err)
			printStatuses(stdout, engine)
			return 1
		}
		printStatuses(stdout, engine)
		return 0
	}

	runner := &reconcile.Runner{
		Engine:     engine,
		Events:     nl,
		Log:        log,
		Interval:   *interval,
		BackoffMin: *backoffMin,
		BackoffMax: *backoffMax,
	}

	// REQ-API-073 handles SIGTERM. SIGINT is here too so an operator running
	// the command in a terminal stops it the way they expect.
	ctx, stop := signal.NotifyContext(context.Background(),
		syscall.SIGTERM, syscall.SIGINT)
	defer stop()

	log.Info("reconcile loop starting",
		"store", *storePath, "interval", interval.String())

	err = runner.Run(ctx)
	if err != nil && !errors.Is(err, context.Canceled) {
		fmt.Fprintf(stderr, "serve: %v\n", err)
		return 1
	}

	// REQ-API-074: nothing below alters the kernel state of any interface.
	log.Info("reconcile loop stopped; managed interfaces are left running")
	return 0
}

func printStatuses(w io.Writer, e *reconcile.Engine) {
	for _, s := range e.Statuses() {
		fmt.Fprintf(w, "%-12s %-9s %-8s peers=%-3d %s",
			s.Name, s.Ownership, s.OperState, s.PeerCount, s.Condition.State)
		if s.Condition.Reason != "" {
			fmt.Fprintf(w, " %s: %s", s.Condition.Reason, s.Condition.Message)
		}
		fmt.Fprintln(w)
		for _, warn := range s.Warnings {
			fmt.Fprintf(w, "             warning %s: %s\n", warn.Reason, warn.Message)
		}
	}
}

func newLogger(w io.Writer, level string) *slog.Logger {
	var l slog.Level
	if err := l.UnmarshalText([]byte(level)); err != nil {
		l = slog.LevelInfo
	}
	return slog.New(slog.NewTextHandler(w, &slog.HandlerOptions{Level: l}))
}
