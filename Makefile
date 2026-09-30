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

# Integration tier: the published binary against real wg and wg-quick@ units, in a privileged
# systemd container. Published self-contained rather than NativeAOT, which needs clang; the code
# is the same either way.
INTEGRATION_BINARY := $(CURDIR)/artifacts/integration/wg-agent

test-integration:
	$(DOTNET) publish src/WgAgent.Cli -c $(CONFIG) -r linux-x64 --self-contained -p:PublishAot=false -o $(INTEGRATION_BINARY)
	WGAGENT_TEST_BINARY=$(INTEGRATION_BINARY) $(DOTNET) test --project tests/WgAgent.IntegrationTests -c $(CONFIG)

# Documentation tier.
check:
	docs/check-docs.sh --quiet
	docs/check-traceability.sh --quiet
	docs/check-mermaid.sh --quiet

clean:
	$(DOTNET) clean WgAgent.slnx -c $(CONFIG)
