#!/usr/bin/env bash
#
# Comment the shipped version on every issue this release carries.
#
#   announce-release.sh <tag> <notes-file>
#
# Closes the loop that a closing comment cannot: an issue closed *before* its
# release is cut can only promise a version, and the promise is what gets
# forgotten (tamp#164). The release notes already name the issues, so the
# version can be announced from the release itself rather than from memory.
#
# An issue is announced only when BOTH hold:
#
#   1. its number appears in this release's notes, and
#   2. it carries the `awaiting-release` label.
#
# The intersection is deliberate. The CHANGELOG entry citing an issue is the
# authoritative statement that this release carries it, and the label is the
# author's statement that it is waiting for one. Either alone is wrong: notes
# also cite issues for context ("tracked in #162", "see the epic #58"), and
# commenting "shipped in X" on those would be a false claim, which is worse
# than a missing comment. A missing comment is recoverable and the label keeps
# it visible; a wrong one is not.
#
# Never fails the release. By this point the packages are public and
# irreversible, so a problem here is reported as a warning and nothing more.
#
# Dependency-free by design (gh, grep, sort) so it can be copied into the
# satellites, whose release workflows are near-identical.

set -uo pipefail

tag="${1:-}"
notes="${2:-}"

if [[ -z "$tag" || -z "$notes" ]]; then
  echo "usage: $0 <tag> <notes-file>" >&2
  exit 2
fi

if [[ ! -f "$notes" ]]; then
  echo "::warning::announce-release: no notes file at '$notes'; skipping issue announcements."
  exit 0
fi

version="${tag#v}"
repo="${GITHUB_REPOSITORY:-}"
if [[ -z "$repo" ]]; then
  echo "::warning::announce-release: GITHUB_REPOSITORY is unset; skipping issue announcements."
  exit 0
fi

# Same-repo references only. A bare "#123" is this repo; a full URL is matched
# only when it points at this repo, so "tamp-grype#6" or another repo's issue
# URL is left alone — commenting there would be announcing a version of the
# wrong package.
same_repo_urls="$(grep -oE "https://github\.com/${repo}/issues/[0-9]+" "$notes" 2>/dev/null | grep -oE '[0-9]+$')"
bare_refs="$(grep -oE '(^|[^A-Za-z0-9_/-])#[0-9]+' "$notes" 2>/dev/null | grep -oE '[0-9]+')"

cited="$(printf '%s\n%s\n' "$same_repo_urls" "$bare_refs" | grep -E '^[0-9]+$' | sort -u -n)"

if [[ -z "$cited" ]]; then
  echo "announce-release: the notes cite no issues in $repo; nothing to announce."
  exit 0
fi

announced=0
skipped=0

for number in $cited; do
  labels="$(gh issue view "$number" --repo "$repo" --json labels,state \
              --jq '"\(.state) \([.labels[].name] | join(","))"' 2>/dev/null)" || {
    # Not an issue (a PR number, or deleted). Not a problem.
    skipped=$((skipped + 1))
    continue
  }

  case "$labels" in
    *awaiting-release*) : ;;
    *) skipped=$((skipped + 1)); continue ;;
  esac

  body="Shipped in **${version}** — https://github.com/${repo}/releases/tag/${tag}

Announced automatically when the release was published (tamp#164), so an issue closed before its release still gets its version."

  if gh issue comment "$number" --repo "$repo" --body "$body" >/dev/null 2>&1; then
    gh issue edit "$number" --repo "$repo" --remove-label awaiting-release >/dev/null 2>&1 || true
    echo "announce-release: commented $version on #$number"
    announced=$((announced + 1))
  else
    echo "::warning::announce-release: could not comment on #$number; it still carries awaiting-release."
  fi
done

echo "announce-release: $announced announced, $skipped not awaiting a release."
