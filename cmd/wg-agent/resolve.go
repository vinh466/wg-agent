package main

import (
	"flag"

	"wg-agent/internal/config"
)

// storeFlags declares the two flags every subcommand that reaches the store
// needs, so all four resolve `state.path` the same way.
//
// Without this an operator who set `state.path` in the configuration file would
// find `serve` reading one store and `adopt` writing another, which is the
// worst shape the bug could take: both commands succeed.
type storeFlags struct {
	config *string
	store  *string
}

func addStoreFlags(fs *flag.FlagSet) storeFlags {
	return storeFlags{
		// REQ-CFG-039.
		config: fs.String("config", config.DefaultPath, "path to the configuration file"),
		// REQ-CFG-042 — empty means the configuration decides.
		store: fs.String("store", "", "override state.path"),
	}
}

// resolveStorePath applies the precedence of REQ-CFG-040, REQ-CFG-001 and
// REQ-CFG-042: the default, then the file, then the environment, then the flag.
//
// A configuration that fails to load is reported, because a command that
// silently fell back to the default path could write desired state somewhere
// the agent will never read.
func (f storeFlags) resolveStorePath() (string, error) {
	if *f.store != "" {
		return *f.store, nil
	}
	cfg, err := config.Load(*f.config)
	if err != nil {
		return "", err
	}
	return cfg.State.Path, nil
}
