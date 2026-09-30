# Task runner. Every target is a thin wrapper, so the commands stay readable here.

DOTNET ?= dotnet
CONFIG ?= Release

.PHONY: all build test test-integration check clean

all: check build test

build:
	$(DOTNET) build WgAgent.slnx -c $(CONFIG)

# Unit tier: no privilege, the in-memory platform.
test:
	$(DOTNET) test --project tests/WgAgent.Tests -c $(CONFIG)

# Integration tier: real wg and wg-quick@ units in a privileged systemd container.
test-integration:
	$(DOTNET) test --project tests/WgAgent.IntegrationTests -c $(CONFIG)

# Documentation tier.
check:
	docs/check-docs.sh --quiet
	docs/check-traceability.sh --quiet
	docs/check-mermaid.sh --quiet

clean:
	$(DOTNET) clean WgAgent.slnx -c $(CONFIG)
