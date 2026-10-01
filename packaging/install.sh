#!/usr/bin/env bash
# wg-agent installer — install, update or uninstall the agent from its GitHub release.
# REQ-CFG-052 (install/update/uninstall), REQ-CFG-053 (verify the SHA256 before installing).
#
#   curl -fsSL https://raw.githubusercontent.com/vinh466/wg-agent/main/packaging/install.sh | sudo bash
#   sudo ./install.sh --uninstall
#
# Options: --uninstall, --version <tag>, --repo <owner/name>.
set -euo pipefail

REPO=${WG_AGENT_REPO:-vinh466/wg-agent}
ACTION=install
VERSION=latest

while [ $# -gt 0 ]; do
  case "$1" in
    --uninstall) ACTION=uninstall ;;
    --version) VERSION=$2; shift ;;
    --repo) REPO=$2; shift ;;
    -h|--help) grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "wg-agent: unknown argument '$1'" >&2; exit 2 ;;
  esac
  shift
done

die() { echo "wg-agent: $*" >&2; exit 1; }
[ "$(id -u)" = 0 ] || die "run as root (try: sudo $0 $*)."

if [ "$ACTION" = uninstall ]; then
  # Removal goes through the package manager, so REQ-CFG-027 and REQ-CFG-028 govern the links.
  apt-get purge -y wg-agent
  echo "wg-agent: removed. Any WireGuard interfaces it created were left running."
  exit 0
fi

command -v curl >/dev/null || die "curl is required."
[ "$(dpkg --print-architecture)" = amd64 ] || die "only amd64 (linux-x64) is published."

if [ "$VERSION" = latest ]; then
  api="https://api.github.com/repos/$REPO/releases/latest"
else
  api="https://api.github.com/repos/$REPO/releases/tags/$VERSION"
fi
json=$(curl -fsSL "$api") || die "cannot reach the release metadata at $api."
url_of() { printf '%s' "$json" | grep -oE '"browser_download_url"[: ]*"[^"]*'"$1"'"' | head -1 | sed -E 's/.*"(https:[^"]*)"/\1/'; }
deb_url=$(url_of '\.deb')
sums_url=$(url_of 'SHA256SUMS')
[ -n "$deb_url" ] && [ -n "$sums_url" ] || die "the release has no .deb and SHA256SUMS asset."

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
deb="$work/$(basename "$deb_url")"
echo "wg-agent: downloading $(basename "$deb_url")"
curl -fsSL -o "$deb" "$deb_url"
curl -fsSL -o "$work/SHA256SUMS" "$sums_url"

# REQ-CFG-053: verify the downloaded .deb against its published SHA256 before installing it.
( cd "$work" && grep " $(basename "$deb")\$" SHA256SUMS | sha256sum -c - ) || die "checksum verification failed."

apt-get install -y "$deb"
echo "wg-agent: installed. Service status:"
systemctl --no-pager --lines=0 status wg-agent 2>/dev/null || true
echo "wg-agent: the API listens on 127.0.0.1:9585 by default; set WG_AGENT_LISTEN_ADDRESS in /etc/default/wg-agent to expose it on your private network."
