#!/usr/bin/env bash
#
# Print one version's section of a Keep-a-Changelog file.
#
#   changelog-section.sh <version> [changelog-path]
#
# Writes the section body (everything after the version heading, up to the next
# level-2 heading) to stdout. Exits 1 and writes nothing to stdout when the
# version has no section, so a caller can tell "no notes" from "empty notes".
#
# Heading formats in use across this org are not uniform, so the match is
# deliberately loose — a level-2 heading whose first token is the version, with
# the brackets optional:
#
#   ## [1.19.0] — 2026-11-04 — title     (em dash, with a title)
#   ## [0.2.0] - 2026-11-05 - title      (hyphen)
#   ## [0.1.3] — 2026-09-27              (no title)
#   ## 0.1.2                             (no brackets at all)
#
# This script is intended to be copied verbatim into the satellite repos, whose
# release workflows are near-identical. Keep it dependency-free: coreutils and
# bash only, no jq, no python.
#
# tamp#159

set -uo pipefail

version="${1:-}"
changelog="${2:-CHANGELOG.md}"

if [[ -z "$version" ]]; then
  echo "usage: $0 <version> [changelog-path]" >&2
  exit 2
fi

if [[ ! -f "$changelog" ]]; then
  echo "changelog-section: no such file: $changelog" >&2
  exit 2
fi

# Strip a leading "v" so both `v1.19.0` (tag) and `1.19.0` (heading) work.
version="${version#v}"

# Make each dot literal with a character class rather than a backslash escape. awk warns on a
# dynamic-regex "\." and treats it as a plain dot, which matches ANY character — so 1.19.0 would
# also match a heading for 1a19b0. "[.]" is unambiguous and warning-free.
escaped="${version//./[.]}"

section="$(
  awk -v pattern="^## \\\\[?${escaped}\\\\]?([^0-9.]|$)" '
    # The heading that opens our section: start capturing, do not print it.
    !inside && $0 ~ pattern { inside = 1; next }
    # Any later level-2 heading closes it.
    inside && /^## / { exit }
    inside { print }
  ' "$changelog"
)"

# Trim leading and trailing blank lines.
section="$(printf '%s\n' "$section" | sed -e '/./,$!d' -e ':a' -e '/^\n*$/{$d;N;ba' -e '}')"

if [[ -z "$section" ]]; then
  echo "changelog-section: no section for version '$version' in $changelog" >&2
  exit 1
fi

printf '%s\n' "$section"
