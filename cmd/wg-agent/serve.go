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

	"wg-agent/internal/config"
	"wg-agent/internal/platform/hostfs"
	"wg-agent/internal/platform/link"
	"wg-agent/internal/platform/wg"
	"wg-agent/internal/reconcile"
	"wg-agent/internal/startup"
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

	// REQ-CFG-039 for --config; every other flag here is the REQ-CFG-042 layer
	// over the same key, so its zero value means "the configuration decides".
	configPath := fs.String("config", config.DefaultPath, "path to the configuration file")
	storePath := fs.String("store", "", "override state.path")
	interval := fs.Duration("interval", 0, "override reconcile.interval")
	backoffMin := fs.Duration("backoff-min", 0, "override reconcile.backoff_min")
	backoffMax := fs.Duration("backoff-max", 0, "override reconcile.backoff_max")
	logLevel := fs.String("log-level", "", "override log.level: debug, info, warn or error")
	once := fs.Bool("once", false, "run a single reconcile pass and exit")

	fs.Usage = func() {
		fmt.Fprintln(stderr, "usage: wg-agent serve [flags]")
		fmt.Fprintln(stderr, "\nRuns the reconcile loop, holding an exclusive lock on the store.")
		fmt.Fprintln(stderr, "Stops on SIGTERM or SIGINT without touching any interface it manages.")
		fmt.Fprintln(stderr, "\nEvery flag below overrides the configuration file and the")
		fmt.Fprintln(stderr, "WG_AGENT_* environment variable for the same key.")
		fmt.Fprintln(stderr)
		fs.PrintDefaults()
	}
	if err := fs.Parse(args); err != nil {
		return 2
	}

	cfg, err := config.Load(*configPath)
	if err != nil {
		fmt.Fprintf(stderr, "configuration: %v\n", err)
		return 1
	}

	// REQ-CFG-042 — the flag layer, applied last.
	if *storePath != "" {
		cfg.State.Path = *storePath
	}
	if *interval != 0 {
		cfg.Reconcile.Interval = *interval
	}
	if *backoffMin != 0 {
		cfg.Reconcile.BackoffMin = *backoffMin
	}
	if *backoffMax != 0 {
		cfg.Reconcile.BackoffMax = *backoffMax
	}
	if *logLevel != "" {
		cfg.Log.Level = *logLevel
	}

	log := newLogger(stderr, cfg.Log.Level)

	dev, err := wg.New()
	if err != nil {
		fmt.Fprintf(stderr, "open wgctrl: %v\n", err)
		return 1
	}
	defer func() { _ = dev.Close() }()

	// REQ-API-050 checks 1, 2 and 5, before anything is served or applied.
	// Checks 7 and 9 were answered by config.Load, and check 6 is nftables,
	// deferred under B-04.
	if err := startup.New(dev.Names).Run(); err != nil {
		fmt.Fprintf(stderr, "%v\n", err)
		return 1
	}

	// REQ-API-050 checks 3 and 4.
	st, err := store.Open(cfg.State.Path)
	if err != nil {
		if errors.Is(err, store.ErrLocked) {
			// REQ-RCN-007: the lock is the answer to "is the agent running",
			// and it guards the resource rather than standing in for it.
			fmt.Fprintf(stderr, "another process holds the store lock at %s; "+
				"an agent is already running\n", cfg.State.Path)
			return 1
		}
		fmt.Fprintf(stderr, "%v\n", startup.Store(err))
		return 1
	}
	// REQ-API-073: the lock is released before exit, so a subcommand that
	// writes the store directly can run once the agent stops.
	defer func() { _ = st.Close() }()

	nl := link.New()
	host := hostfs.New()
	engine := &reconcile.Engine{Store: st, Link: nl, Device: dev, HostFS: host}

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
		Interval:   cfg.Reconcile.Interval,
		BackoffMin: cfg.Reconcile.BackoffMin,
		BackoffMax: cfg.Reconcile.BackoffMax,
	}

	// REQ-API-073 handles SIGTERM. SIGINT is here too so an operator running
	// the command in a terminal stops it the way they expect.
	ctx, stop := signal.NotifyContext(context.Background(),
		syscall.SIGTERM, syscall.SIGINT)
	defer stop()

	log.Info("reconcile loop starting",
		"store", cfg.State.Path,
		"interval", cfg.Reconcile.Interval.String(),
		"config", *configPath)

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
