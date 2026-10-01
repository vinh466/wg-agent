#!/usr/bin/env bash
# Build wg-agent_<version>_amd64.deb from a published linux-x64 binary (REQ-CFG-021, REQ-CFG-051).
# Usage: packaging/build-deb.sh <path-to-wg-agent-binary> [version] [outdir]
set -euo pipefail

BINARY=${1:?usage: build-deb.sh <path-to-wg-agent-binary> [version] [outdir]}
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION=${2:-$(grep -oPm1 '(?<=<Version>)[^<]+' "$ROOT/Directory.Build.props")}
OUTDIR=${3:-"$ROOT/artifacts"}
ARCH=amd64
PKG="$ROOT/packaging"

stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT

install -D -m 0755 "$BINARY"                       "$stage/usr/bin/wg-agent"
install -D -m 0644 "$PKG/systemd/wg-agent.service" "$stage/lib/systemd/system/wg-agent.service"
install -D -m 0644 "$PKG/default/wg-agent"         "$stage/etc/default/wg-agent"

install -d -m 0755 "$stage/DEBIAN"
sed -e "s/@VERSION@/$VERSION/" -e "s/@ARCH@/$ARCH/" "$PKG/debian/control" > "$stage/DEBIAN/control"
install -m 0644 "$PKG/debian/conffiles" "$stage/DEBIAN/conffiles"
install -m 0755 "$PKG/debian/postinst" "$PKG/debian/prerm" "$PKG/debian/postrm" "$stage/DEBIAN/"

mkdir -p "$OUTDIR"
deb="$OUTDIR/wg-agent_${VERSION}_${ARCH}.deb"
dpkg-deb --root-owner-group --build "$stage" "$deb" >/dev/null
echo "$deb"
