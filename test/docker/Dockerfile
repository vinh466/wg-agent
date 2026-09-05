# Test image: the Go toolchain plus a real WireGuard userspace, so that driving
# the kernel in a test needs nothing installed on the host except Docker.
#
# The container gets its own network namespace, which is what makes an
# integration test safe: an interface it creates cannot collide with one on the
# host. The `wireguard` kernel module is shared with the host and must be loaded
# there — a container cannot load it.
#
# See docs/50-guides/running-tests.md.
FROM golang:1.23-alpine

RUN apk add --no-cache \
      wireguard-tools \
      iproute2 \
      nftables \
      git \
      make

# REQ-CFG-020 builds the release binary statically; keeping the test build the
# same avoids a difference between what is tested and what ships.
ENV CGO_ENABLED=0
ENV GOCACHE=/tmp/gocache
ENV GOMODCACHE=/tmp/gomodcache

WORKDIR /src
