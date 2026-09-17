#!/usr/bin/env bash
# Renders every mermaid block in docs/ so a diagram that does not parse fails
# here rather than in a reader's browser.
#
# A diagram exists to be looked at. One that silently fails to render is worse
# than prose, because the page still looks complete.
#
# Requires Docker. Skips with a zero exit when Docker is absent, so the check is
# safe to chain after docs/check-docs.sh in any environment.
set -uo pipefail

cd "$(dirname "$0")/.."

red() { printf '\033[31m%s\033[0m\n' "$1"; }
grn() { printf '\033[32m%s\033[0m\n' "$1"; }

if ! command -v docker >/dev/null 2>&1; then
  grn "mermaid: docker absent — skipped"
  exit 0
fi

IMAGE=${MERMAID_IMAGE:-minlag/mermaid-cli:latest}
WORK=$(mktemp -d)
# The image runs as its own user, so the bind mount has to be writable by it.
chmod 777 "$WORK"
trap 'rm -rf "$WORK"' EXIT

# Split every fenced mermaid block into its own file, named for where it came
# from so a failure points at the source line.
n=0
while IFS= read -r file; do
  awk -v src="$file" -v out="$WORK" '
    /^```mermaid[[:space:]]*$/ { inblk=1; start=NR; body=""; next }
    inblk && /^```[[:space:]]*$/ {
      inblk=0
      gsub(/[^A-Za-z0-9]/, "_", src)
      name = out "/" src "__line" start ".mmd"
      printf "%s", body > name
      close(name)
      next
    }
    inblk { body = body $0 "\n" }
  ' "$file"
done < <(grep -rl '^```mermaid' docs/)

mapfile -t BLOCKS < <(find "$WORK" -name '*.mmd' | sort)
if [ ${#BLOCKS[@]} -eq 0 ]; then
  grn "mermaid: no diagrams found"
  exit 0
fi

fail=0
for f in "${BLOCKS[@]}"; do
  name=$(basename "$f" .mmd)
  if out=$(docker run --rm -v "$WORK":/data "$IMAGE" \
             -i "/data/$(basename "$f")" -o "/data/$name.svg" 2>&1); then
    n=$((n + 1))
  else
    red "  BROKEN  $name"
    printf '%s\n' "$out" | grep -iE 'error|expect' | head -3 | sed 's/^/          /'
    fail=1
  fi
done

if [ $fail -eq 0 ]; then
  grn "mermaid: $n diagram(s) render"
  exit 0
fi
red "mermaid: at least one diagram does not parse"
exit 1
