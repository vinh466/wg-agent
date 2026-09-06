# wg-agent — test and check entry points.
#
# Task-oriented documentation: docs/50-guides/running-tests.md
#
#   make proto                  regenerate gen/ and the OpenAPI document
#   make proto-check            lint the contract and check it for breaking changes
#   make check                  documentation integrity and REQ-ID traceability
#   make probe                  drive real WireGuard in a container, no host toolchain
#   make docker-test            unit and integration tests in a container
#   make docker-test-privileged the tiers that need a writable /proc/sys
#
# Targets prefixed docker- need only Docker on the host. The bare go targets
# need a Go toolchain and, for the integration tier, CAP_NET_ADMIN.

GO      ?= go
DOCKER  ?= docker
IMAGE   ?= wg-agent-test
PROTOIMG ?= wg-agent-proto
ROOT    := $(CURDIR)
CAPS    := --cap-add=NET_ADMIN
# Named volumes hold the build and module caches, so a repeated run takes under
# a second instead of recompiling from scratch. They are volumes rather than a
# directory in the tree because the integration tier runs as root — it needs
# CAP_NET_ADMIN — and a root-owned cache inside the repository would then block
# every target that runs as the invoking user.
CACHE   := -v wg-agent-gobuild:/tmp/gocache -v wg-agent-gomod:/tmp/gomodcache
MOUNT   := -v $(ROOT):/src $(CACHE)
USER    := --user $(shell id -u):$(shell id -g)

.PHONY: help check test test-integration bench docker-image docker-test \
        docker-test-privileged docker-shell fmt-docker unit-docker race-docker probe \
        proto-image proto proto-check fmt vet clean

help:
	@sed -n 's/^# \{0,2\}//p' $(MAKEFILE_LIST) | sed -n '1,14p'

# ── Checks that need no toolchain ───────────────────────────────────────────
check:
	docs/check-docs.sh
	docs/check-traceability.sh

# ── Host targets ────────────────────────────────────────────────────────────
test:
	$(GO) test ./...

# Real WireGuard. Run inside a dedicated network namespace, never on a host
# whose interfaces matter.
test-integration:
	$(GO) test -tags=integration -count=1 ./...

bench:
	$(GO) test -tags=integration -run='^$$' -bench=. -benchtime=1x ./...

fmt:
	$(GO) fmt ./...

vet:
	$(GO) vet ./...

# ── Code generation ─────────────────────────────────────────────────────────
#
# ADR-0003 makes api/proto the source of truth and REQ-API-060 generates the
# clients from it. gen/ is committed so a build needs no network and a contract
# change is visible in review.
proto-image:
	$(DOCKER) build -q -t $(PROTOIMG) -f test/docker/Dockerfile.proto .

proto: proto-image
	$(DOCKER) run --rm $(MOUNT) -w /src $(PROTOIMG) buf generate
	$(DOCKER) run --rm -v $(ROOT):/src alpine:3.20 \
	  chown -R $(shell id -u):$(shell id -g) /src/gen /src/docs/30-api

# REQ-API-061 blocks a compatibility-breaking change within a package version.
# The comparison is against main, which is the baseline gen/ was built from.
proto-check: proto-image
	$(DOCKER) run --rm $(MOUNT) -w /src $(PROTOIMG) buf lint
	$(DOCKER) run --rm $(MOUNT) -w /src $(PROTOIMG) \
	  buf breaking --against '.git#branch=main'

# ── Container targets ───────────────────────────────────────────────────────
docker-image:
	$(DOCKER) build -q -t $(IMAGE) -f test/docker/Dockerfile .

# The container's own network namespace is the isolation boundary. CAP_NET_ADMIN
# is enough to create a WireGuard link, configure it and read it back.
docker-test: docker-image
	$(DOCKER) run --rm $(CAPS) $(MOUNT) -w /src $(IMAGE) \
	  sh -c '$(GO) test ./... && $(GO) test -tags=integration -count=1 ./...'

# Docker mounts /proc/sys read-only, so the forwarding sysctl of REQ-FWD-020 and
# the nftables table of REQ-FWD-010 need a privileged container. Kept separate
# because a privileged container is not isolated from the host.
docker-test-privileged: docker-image
	$(DOCKER) run --rm --privileged $(MOUNT) -w /src $(IMAGE) \
	  $(GO) test -tags='integration privileged' -count=1 ./...

docker-shell: docker-image
	$(DOCKER) run --rm -it $(CAPS) $(MOUNT) -w /src $(IMAGE) sh

# gofmt writes to the tree, so it runs as the invoking user and without the
# shared cache. Every other container target only reads the tree.
fmt-docker: docker-image
	$(DOCKER) run --rm $(USER) -v $(ROOT):/src -w /src $(IMAGE) gofmt -w .

# The unit tier alone, for a fast inner loop.
unit-docker: docker-image
	$(DOCKER) run --rm $(MOUNT) -w /src $(IMAGE) $(GO) test ./...

# The same tier under the race detector. REQ-RCN-042 requires the agent to
# serialize operations on one interface, and the test that asserts it is only
# meaningful here: without -race a missing lock passes silently.
race-docker: docker-image
	$(DOCKER) run --rm -e CGO_ENABLED=1 $(MOUNT) -w /src $(IMAGE) $(GO) test -race ./...

# Verifies the environment before any product code exists: that a container can
# create a WireGuard link through netlink, configure it through wgctrl, and read
# the interface key and every peer back — the premise of REQ-RCN-061 and
# REQ-RCN-062.
probe: docker-image
	$(DOCKER) run --rm $(CAPS) $(MOUNT) -w /src/test/probe $(IMAGE) $(GO) run .

clean:
	$(DOCKER) rmi -f $(IMAGE) 2>/dev/null || true
	$(DOCKER) volume rm -f wg-agent-gobuild wg-agent-gomod 2>/dev/null || true
