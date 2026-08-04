#!/usr/bin/env bash
# Documentation integrity and style checks. Run in CI on every PR.
#
# Enforces the mechanical layers described in docs/CONTRIBUTING.md:
#   integrity  1 links · 2 duplicate REQ IDs · 3 orphan REQ IDs
#   style      4 banned vocabulary · 5 requirement format
#   structure  6 length caps · 7 front-matter
#
# Usage: docs/check-docs.sh          exit 0 = pass
#        docs/check-docs.sh --quiet  errors only

set -uo pipefail
cd "$(dirname "$0")"

QUIET=0
[ "${1:-}" = "--quiet" ] && QUIET=1

fail=0
red()  { printf '\033[31m%s\033[0m\n' "$*"; }
grn()  { [ $QUIET -eq 1 ] || printf '\033[32m%s\033[0m\n' "$*"; }
head_() { [ $QUIET -eq 1 ] || printf '\n\033[1m%s\033[0m\n' "$*"; }

# Excluded everywhere: superseded archive, and templates whose placeholders are intentional.
mapfile -t DOCS < <(find . -name '*.md' -not -path './99-archive/*' -not -name '_template.md' | sort)

# Normative requirement definitions. Guidance files use the reserved XXX area for
# examples, so they are excluded from ID accounting.
req_defs() {
  grep -rhoP '^> \*\*REQ-[A-Z]{3}-\d{3}\*\*' 20-spec/ \
    --exclude=_template.md --exclude=README.md \
    | grep -oP 'REQ-[A-Z]{3}-\d{3}'
}

# ── 1. Links ────────────────────────────────────────────────────────────────
head_ "1. Links"
n=0
for f in "${DOCS[@]}"; do
  dir=$(dirname "$f")
  while IFS= read -r link; do
    [ -e "$dir/$link" ] || { red "  BROKEN  $f -> $link"; fail=1; n=$((n+1)); }
  done < <(grep -oP '\]\(\K[^)#]+\.md(?=[)#])' "$f" 2>/dev/null)
done
[ $n -eq 0 ] && grn "  ok — no broken links"

# ── 2. Duplicate REQ IDs ────────────────────────────────────────────────────
head_ "2. Duplicate requirement IDs"
dups=$(req_defs | sort | uniq -d)
if [ -n "$dups" ]; then
  red "  DUPLICATE:"; echo "$dups" | sed 's/^/    /'; fail=1
else
  grn "  ok — each ID defined once"
fi

# ── 3. Orphan REQ IDs ───────────────────────────────────────────────────────
head_ "3. Orphan requirement IDs"
defined=$(req_defs | sort -u)
used=$(grep -rhoP 'REQ-[A-Z]{3}-\d{3}' "${DOCS[@]}" | grep -v 'REQ-XXX-' | sort -u)
orphans=$(comm -13 <(echo "$defined") <(echo "$used"))
if [ -n "$orphans" ]; then
  red "  UNDEFINED:"; echo "$orphans" | sed 's/^/    /'; fail=1
else
  grn "  ok — every reference resolves"
fi

# ── 4. Banned vocabulary ────────────────────────────────────────────────────
# Two rule sets. Normative and explanatory documents must read impersonally.
# Task-oriented guides legitimately address the reader, so the person rule is lifted
# there while temporal and conversational rules still apply.
# Planning documents are exempt entirely: schedule and intent are their subject.
head_ "4. Banned vocabulary"
PERSON='\b(I|we|you|our|us|my)\b|\blet'"'"'s\b'
COMMON='\b(currently|recently|previously|soon)\b|\bat the moment\b|\bjust added\b|\bwill be\b'
COMMON+='|\bas (discussed|mentioned|noted|we saw)\b|\bnote that\b|\bworth noting\b|\bkeep in mind\b'
COMMON+='|\b(obviously|basically|actually)\b|\bof course\b'
COMMON+='|\b(probably|maybe|perhaps|seems|appears to)\b'
COMMON+='|\bthis document will\b|\bin this section\b|\bbelow we\b'
n=0
for f in "${DOCS[@]}"; do
  case "$f" in
    ./60-planning/*|./CONTRIBUTING.md|./README.md) continue ;;   # define the rules, must quote them
    ./50-guides/*) pattern="$COMMON" ;;                          # guides may address the reader
    *)             pattern="$PERSON|$COMMON" ;;
  esac
  while IFS= read -r hit; do
    red "  $f:$hit"; fail=1; n=$((n+1))
  done < <(grep -nioP "$pattern" "$f" 2>/dev/null | head -5)
done
[ $n -eq 0 ] && grn "  ok — no conversational or temporal language"

# ── 5. Requirement format ───────────────────────────────────────────────────
# A requirement may wrap across several blockquote lines, so the whole block is
# collected before counting keywords.
head_ "5. Requirement format"
n=0
while IFS= read -r finding; do
  red "  $finding"; fail=1; n=$((n+1))
done < <(
  for f in 20-spec/SPEC-*.md; do
    awk -v F="$f" '
      function emit() {
        t = buf; k = 0
        while (match(t, /MUST NOT|MUST|SHOULD NOT|SHOULD|MAY/)) {
          k++; t = substr(t, RSTART + RLENGTH)
        }
        if (k != 1) printf "%s:%d — expected exactly 1 RFC 2119 keyword, found %d\n", F, ln, k
        open = 0
      }
      /^> \*\*REQ-[A-Z]{3}-[0-9]{3}\*\*/ { if (open) emit(); open = 1; ln = NR; buf = $0; next }
      open && /^>/                       { buf = buf " " $0; next }
      open                               { emit() }
      END                                { if (open) emit() }
    ' "$f"
  done
)
[ $n -eq 0 ] && grn "  ok — every requirement has one keyword"

# ── 6. Length caps ──────────────────────────────────────────────────────────
head_ "6. Length caps"
cap_for() {
  case "$1" in
    ./20-spec/SPEC-*)     echo 400 ;;
    ./10-decisions/ADR-*) echo 180 ;;
    ./40-concepts/*)      echo 320 ;;
    ./50-guides/*)        echo 280 ;;
    *)                    echo 0   ;;
  esac
}
n=0
for f in "${DOCS[@]}"; do
  cap=$(cap_for "$f"); [ "$cap" -eq 0 ] && continue
  len=$(wc -l < "$f")
  if [ "$len" -gt "$cap" ]; then
    red "  $f — $len lines, cap $cap. Split it, do not raise the cap."; fail=1; n=$((n+1))
  fi
done
[ $n -eq 0 ] && grn "  ok — all documents within caps"

# ── 7. Front-matter ─────────────────────────────────────────────────────────
head_ "7. Front-matter"
n=0
for f in "${DOCS[@]}"; do
  case "$f" in
    ./20-spec/SPEC-*|./10-decisions/ADR-*|./90-rfcs/RFC-*) ;;
    *) continue ;;
  esac
  [ "$(head -1 "$f")" = "---" ] || { red "  $f — missing front-matter"; fail=1; n=$((n+1)); continue; }
  for key in id title status owner created updated; do
    grep -qP "^$key:" "$f" || { red "  $f — missing key '$key'"; fail=1; n=$((n+1)); }
  done
done
[ $n -eq 0 ] && grn "  ok — front-matter complete"

# ── Summary ─────────────────────────────────────────────────────────────────
if [ $QUIET -eq 0 ]; then
  head_ "Summary"
  printf "  requirements : %s\n" "$(echo "$defined" | grep -c .)"
  printf "  by area      : "
  echo "$defined" | grep -oP 'REQ-\K[A-Z]{3}' | sort | uniq -c \
    | awk '{printf "%s=%s ", $2, $1}'; echo
  printf "  documents    : %s\n" "${#DOCS[@]}"
  echo
fi

[ $fail -eq 0 ] && grn "PASS" || red "FAIL"
exit $fail
