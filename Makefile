# Task runner. Every target is a thin wrapper, so the commands stay readable here.

DOTNET ?= dotnet
CONFIG ?= Release

.PHONY: all build test test-integration test-packaging check clean

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

# Packaging tier: build the real NativeAOT .deb, then install it under systemd in a container.
# NativeAOT needs clang, so the binary is published in the SDK image; the .deb is built on the host.
PACKAGE_DIR := $(CURDIR)/artifacts/package

test-packaging:
	docker run --rm -v "$(CURDIR)":/repo:ro -v "$(PACKAGE_DIR)":/out \
	  mcr.microsoft.com/dotnet/sdk:10.0 bash -c '\
	    set -e; apt-get update -qq >/dev/null && apt-get install -y -qq --no-install-recommends clang zlib1g-dev >/dev/null 2>&1; \
	    mkdir /src && cd /repo && tar --exclude=./.git --exclude="*/bin" --exclude="*/obj" -cf - . | (cd /src && tar -xf -); \
	    cd /src && dotnet publish src/WgAgent.Cli -c $(CONFIG) -r linux-x64 -o /out/pub; \
	    chmod -R a+rwX /out'
	packaging/build-deb.sh "$(PACKAGE_DIR)/pub/wg-agent" "$$(grep -oPm1 '(?<=<Version>)[^<]+' Directory.Build.props)" "$(PACKAGE_DIR)"
	WGAGENT_DEB="$$(ls $(PACKAGE_DIR)/wg-agent_*_amd64.deb | head -1)" \
	  $(DOTNET) test --project tests/WgAgent.IntegrationTests -c $(CONFIG) --filter-class '*PackageTests'

# Documentation tier.
check:
	docs/check-docs.sh --quiet
	docs/check-traceability.sh --quiet
	docs/check-mermaid.sh --quiet

clean:
	$(DOTNET) clean WgAgent.slnx -c $(CONFIG)
