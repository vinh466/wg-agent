#!/usr/bin/env bash
# REQ-ID traceability between the specification and the source tree.
#
# docs/20-spec/README.md states that CI verifies two properties:
#   1. every REQ ID referenced in code exists in the spec
#   2. every requirement marked Implemented has at least one referencing test
#
# This script is that check. It is deliberately separate from check-docs.sh,
# which cds into docs/ and therefore can never see a source file.
#
# A requirement a backlog entry defers is not expected to have a test: a module
# is Implemented when every requirement of it outside the backlog is.
#
# Usage: docs/check-traceability.sh          exit 0 = pass
#        docs/check-traceability.sh --quiet  errors only

set -uo pipefail
cd "$(dirname "$0")/.."

QUIET=0
[ "${1:-}" = "--quiet" ] && QUIET=1

fail=0
red() { printf '\033[31m%s\033[0m\n' "$*"; }
grn() { [ $QUIET -eq 1 ] || printf '\033[32m%s\033[0m\n' "$*"; }
hdr() { [ $QUIET -eq 1 ] || printf '\n\033[1m%s\033[0m\n' "$*"; }

# Requirement definitions, live and retired. Mirrors req_defs() in check-docs.sh:
# a retired ID stays defined so that a surviving reference is not an error.
defined=$(
  grep -rhoP '^(> \*\*|~~\*\*)REQ-[A-Z]{3}-\d{3}' docs/20-spec/ \
    --exclude=_template.md --exclude=README.md 2>/dev/null \
  | grep -oP 'REQ-[A-Z]{3}-\d{3}' | sort -u
)

mapfile -t SRCFILES < <(find . -name '*.cs' \
  -not -path '*/bin/*' -not -path '*/obj/*' -not -path './gen/*' | sort)

if [ ${#SRCFILES[@]} -eq 0 ]; then
  hdr "Traceability"
  grn "  no C# files yet — nothing to trace"
  [ $QUIET -eq 1 ] || printf '\n\033[32m%s\033[0m\n' "PASS"
  exit 0
fi

# ── 1. Every ID referenced from code is defined ─────────────────────────────
# Test method names carry the ID with underscores: Thing_Does_REQ_FWD_012.
hdr "1. Code references resolve to a requirement"
used=$(
  { grep -rhoP 'REQ-[A-Z]{3}-\d{3}' "${SRCFILES[@]}" 2>/dev/null
    grep -rhoP 'REQ_[A-Z]{3}_\d{3}' "${SRCFILES[@]}" 2>/dev/null | tr '_' '-'
  } | grep -v 'REQ-XXX-' | sort -u
)
n=0
while IFS= read -r id; do
  [ -z "$id" ] && continue
  grep -qx "$id" <<<"$defined" || { red "  UNDEFINED  $id referenced in code"; fail=1; n=$((n+1)); }
done <<<"$used"
[ $n -eq 0 ] && grn "  ok — every referenced ID exists in docs/20-spec/"

# ── 2. Every Implemented requirement has a referencing test ─────────────────
# Scope is per module: a requirement is Implemented when its module says so,
# unless an entry of docs/60-planning/backlog.md defers it.
hdr "2. Implemented requirements are covered by a test"
deferred=$(
  awk '/^\*\*Defers:\*\*/{f=1} f{print} /^$/{f=0}' docs/60-planning/backlog.md \
  | tr -d '`' | tr '\n' ' ' \
  | grep -oP 'REQ-[A-Z]{3}-\d{3}( to REQ-[A-Z]{3}-\d{3})?' \
  | while read -r first _ last; do
      if [ -n "${last:-}" ]; then
        area=${first%-*}
        for ((k=10#${first##*-}; k<=10#${last##*-}; k++)); do printf '%s-%03d\n' "$area" "$k"; done
      else
        echo "$first"
      fi
    done | sort -u
)
mapfile -t TESTFILES < <(find ./tests -name '*.cs' \
  -not -path '*/bin/*' -not -path '*/obj/*' 2>/dev/null | sort)
tested=""
if [ ${#TESTFILES[@]} -gt 0 ]; then
  tested=$(
    { grep -rhoP 'REQ-[A-Z]{3}-\d{3}' "${TESTFILES[@]}" 2>/dev/null
      grep -rhoP 'REQ_[A-Z]{3}_\d{3}' "${TESTFILES[@]}" 2>/dev/null | tr '_' '-'
    } | sort -u
  )
fi
n=0
for f in docs/20-spec/SPEC-*.md; do
  grep -qP '^status:\s*Implemented' "$f" || continue
  while IFS= read -r id; do
    [ -z "$id" ] && continue
    grep -qx "$id" <<<"$deferred" && continue
    grep -qx "$id" <<<"$tested" || {
      red "  UNTESTED  $id ($(basename "$f") is Implemented)"; fail=1; n=$((n+1)); }
  done < <(grep -hoP '^> \*\*\KREQ-[A-Z]{3}-\d{3}' "$f")
done
[ $n -eq 0 ] && grn "  ok — no Implemented module has an uncovered requirement"

# ── 3. Test names embed the ID in the documented form ───────────────────────
hdr "3. Test naming convention"
n=0
if [ ${#TESTFILES[@]} -gt 0 ]; then
  while IFS= read -r hit; do
    red "  $hit"; fail=1; n=$((n+1))
  done < <(grep -rnP '^\s*public\s+(async\s+)?(void|Task)\s+\w*REQ[-_][A-Z]{3}[-_]\d{3}' \
             "${TESTFILES[@]}" 2>/dev/null \
             | grep -vP 'REQ_[A-Z]{3}_\d{3}' \
             | sed 's/:\s*public.*\s/: /' | head -10)
fi
[ $n -eq 0 ] && grn "  ok — REQ IDs in test names use the REQ_AAA_NNN form"

if [ $QUIET -eq 0 ]; then
  hdr "Summary"
  printf "  source files    : %s\n" "${#SRCFILES[@]}"
  printf "  test files      : %s\n" "${#TESTFILES[@]}"
  printf "  IDs referenced  : %s\n" "$(echo "$used" | grep -c . )"
  printf "  IDs under test  : %s\n" "$(echo "$tested" | grep -c . )"
  echo
fi

[ $fail -eq 0 ] && grn "PASS" || red "FAIL"
exit $fail
