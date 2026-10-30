#!/usr/bin/env bash
# release-draft.sh: the GitHub side of release.yml, one draft per tag, addressed by its id (feature #17, spec #20
# §8; F17 WU4, WU3 review D1). release.yml creates the release as a DRAFT, verifies the draft's assets against
# SHA256SUMS.txt (build/sha256sums.sh check-release) and only then publishes it. Re-running a failed job used to
# create a SECOND draft for the same tag, and `gh release view/edit <tag>` then picked one of the two. Now:
#
#   build/release-draft.sh prepare <repo> <tag>
#       Refuses (exit 1) when a PUBLISHED release already exists for <tag>: a published release is never touched.
#       Deletes, by id, every unpublished draft for <tag> that an earlier, failed run left behind. Drafts of other
#       tags are left alone. Deleting a draft never deletes the tag.
#   build/release-draft.sh create <repo> <tag> <dir> [<asset>...]
#       gh release create <tag> <dir>/<asset>... <dir>/SHA256SUMS.txt --draft --verify-tag --title <tag>
#       --generate-notes [--prerelease for a hyphenated tag], then prints the id of THE one draft for <tag> (exit 1
#       when there is not exactly one). Without assets it takes $RELEASE_ASSETS, release.yml's one list (adding
#       RST-Machine.msi is a one-line change there).
#   build/release-draft.sh verify <repo> <id> <tag> <SHA256SUMS.txt>
#       That exact release must be a draft for <tag>; then sha256sums.sh check-release against its assets.
#   build/release-draft.sh publish <repo> <id> <tag>
#       Publishes that exact release (PATCH draft=false), only if it is still a draft for <tag>.
#   build/release-draft.sh self-test
#       Every path above against a fake `gh` (no network, nothing on GitHub): a clean run, a re-run over one or two
#       stale drafts, a re-run after publication (refused), another tag's draft left alone, a second draft appearing
#       before `find` (refused), and publish refusing a non-draft or another tag.
#
# $GH is the gh command (default: gh); the self-test points it at its fake.
set -euo pipefail

GH="${GH:-gh}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
die() { echo "release-draft: $*" >&2; exit 1; }
need_tag() { [[ "$1" =~ ^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]] || die "'$1' is not a release tag"; }
need_repo() { [[ "$1" =~ ^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$ ]] || die "'$1' is not owner/name"; }
need_id() { [[ "$1" =~ ^[0-9]+$ ]] || die "'$1' is not a release id"; }

# Every release (drafts included: the job's token can see them) for the tag, as "<id>\t<draft>" lines.
releases_for() {
  local repo="$1" tag="$2"
  "$GH" api "repos/$repo/releases" --paginate --jq ".[] | select(.tag_name == \"$tag\") | \"\(.id)\t\(.draft)\""
}

cmd_prepare() {
  local repo="$1" tag="$2" id draft deleted=0
  need_repo "$repo"; need_tag "$tag"
  local lines; lines="$(releases_for "$repo" "$tag")"
  while IFS=$'\t' read -r id draft; do
    [ -n "$id" ] || continue
    [ "$draft" = "true" ] || die "$tag is already published (release $id); a published release is never replaced. Bump the version and tag again."
  done <<< "$lines"
  while IFS=$'\t' read -r id draft; do
    [ -n "$id" ] || continue
    need_id "$id"
    # Review N7: look again right before the DELETE. An overlapping run may have published it since the list.
    local now; now="$("$GH" api "repos/$repo/releases/$id" --jq '"\(.draft)\t\(.tag_name)"')"
    [ "$now" = "true"$'\t'"$tag" ] || die "release $id for $tag is no longer an unpublished draft ($now); not deleted"
    "$GH" api -X DELETE "repos/$repo/releases/$id" > /dev/null
    echo "deleted the unpublished draft $id for $tag (left by an earlier run)"
    deleted=$((deleted + 1))
  done <<< "$lines"
  echo "$tag: no release yet; $deleted stale draft(s) removed"
}

# The id of the one draft for the tag.
cmd_find() {
  local repo="$1" tag="$2"
  local ids; ids="$("$GH" api "repos/$repo/releases" --paginate --jq ".[] | select(.tag_name == \"$tag\" and .draft == true) | .id")"
  local count; count="$(printf '%s\n' "$ids" | grep -c '[0-9]' || true)"
  [ "$count" = "1" ] || die "expected exactly one draft for $tag, found $count ($(echo $ids))"
  need_id "$ids"
  echo "$ids"
}

cmd_create() {
  local repo="$1" tag="$2" dir="$3"; shift 3
  need_repo "$repo"; need_tag "$tag"
  [ -d "$dir" ] || die "create: no folder $dir"
  # The assets: named here, or (release.yml) the one list in $RELEASE_ASSETS. SHA256SUMS.txt is always added.
  local assets=("$@")
  if [ ${#assets[@]} -eq 0 ]; then read -r -a assets <<< "${RELEASE_ASSETS:-}"; fi
  [ ${#assets[@]} -gt 0 ] || die "create: no assets (name them, or set RELEASE_ASSETS)"
  local files=() a
  for a in "${assets[@]}" SHA256SUMS.txt; do
    case "$a" in */* | *\\*) die "create: '$a' is not a file name" ;; esac
    [ -f "$dir/$a" ] || die "create: missing $dir/$a"
    files+=("$dir/$a")
  done
  local prerel=()
  [[ "$tag" == *-* ]] && prerel=(--prerelease)
  "$GH" release create "$tag" "${files[@]}" -R "$repo" --draft --verify-tag --title "$tag" --generate-notes "${prerel[@]}" > /dev/null
  cmd_find "$repo" "$tag"
}

# The release as `gh release view --json isDraft,assets` shapes it, plus its tag.
release_json() {
  local repo="$1" id="$2"
  "$GH" api "repos/$repo/releases/$id" --jq '{isDraft: .draft, tagName: .tag_name, assets: [.assets[] | {name: .name, digest: .digest}]}'
}

cmd_verify() {
  local repo="$1" id="$2" tag="$3" sums="$4"
  need_repo "$repo"; need_id "$id"; need_tag "$tag"
  local tmp; tmp="$(mktemp)"
  trap 'rm -f "$tmp"' RETURN
  release_json "$repo" "$id" > "$tmp"
  jq -e --arg t "$tag" '.isDraft == true and .tagName == $t' "$tmp" > /dev/null || die "release $id is not the draft for $tag"
  "$HERE/sha256sums.sh" check-release "$sums" "$tmp"
}

cmd_publish() {
  local repo="$1" id="$2" tag="$3"
  need_repo "$repo"; need_id "$id"; need_tag "$tag"
  local state; state="$("$GH" api "repos/$repo/releases/$id" --jq '"\(.draft)\t\(.tag_name)"')"
  [ "$state" = "true"$'\t'"$tag" ] || die "release $id is not the draft for $tag (draft/tag: $state); not published"
  "$GH" api -X PATCH "repos/$repo/releases/$id" -F draft=false --jq '"published: \(.html_url) (draft \(.draft), pre-release \(.prerelease))"'
}

# ---- self-test: a fake gh over a JSON file of releases
write_fake_gh() {
  cat > "$1" <<'FAKE'
#!/usr/bin/env bash
# A fake gh for release-draft.sh's self-test: releases live in $FAKE_GH_STATE (a JSON array); every call is logged.
set -euo pipefail
echo "gh $*" >> "$FAKE_GH_LOG"
state="$FAKE_GH_STATE"
if [ "$1" = "release" ] && [ "$2" = "create" ]; then
  tag="$3"; shift 3; files=(); pre=false
  while [ $# -gt 0 ]; do
    case "$1" in
      -R|--title) shift 2 ;;
      --prerelease) pre=true; shift ;;
      --*) shift ;;
      *) files+=("$1"); shift ;;
    esac
  done
  assets="$(for f in "${files[@]}"; do jq -cn --arg n "$(basename "$f")" --arg d "sha256:$(sha256sum "$f" | cut -d' ' -f1)" '{name: $n, digest: $d}'; done | jq -s .)"
  # GitHub never reuses a release id.
  id=$(( $(cat "$state.lastid" 2>/dev/null || echo 100) + 1 )); echo "$id" > "$state.lastid"
  jq --argjson id "$id" --arg t "$tag" --argjson a "$assets" --argjson p "$pre" \
     '. + [{id: $id, tag_name: $t, draft: true, prerelease: $p, html_url: "https://github.com/o/r/releases/tag/\($t)", assets: $a}]' "$state" > "$state.new"
  mv "$state.new" "$state"
  exit 0
fi
[ "$1" = "api" ] || { echo "fake gh: unsupported: $*" >&2; exit 2; }
shift
method=GET; path=""; jqx="."; fields=()
while [ $# -gt 0 ]; do
  case "$1" in
    -X) method="$2"; shift 2 ;;
    --paginate) shift ;;
    --jq) jqx="$2"; shift 2 ;;
    -F) fields+=("$2"); shift 2 ;;
    *) path="$1"; shift ;;
  esac
done
if [[ "$path" =~ ^repos/[^/]+/[^/]+/releases$ ]]; then
  jq -r "$jqx" "$state"; exit 0
fi
[[ "$path" =~ ^repos/[^/]+/[^/]+/releases/([0-9]+)$ ]] || { echo "fake gh: unsupported path $path" >&2; exit 2; }
id="${BASH_REMATCH[1]}"
jq -e --argjson id "$id" 'any(.[]; .id == $id)' "$state" > /dev/null || { echo "HTTP 404: Not Found" >&2; exit 1; }
case "$method" in
  GET)
    # The self-test's race: the release is published by someone else just before this GET answers.
    if [ "${FAKE_GH_PUBLISH_ON_GET:-}" = "$id" ]; then jq --argjson id "$id" 'map(if .id == $id then .draft = false else . end)' "$state" > "$state.new"; mv "$state.new" "$state"; fi
    jq -r --argjson id "$id" ".[] | select(.id == \$id) | $jqx" "$state" ;;
  DELETE) jq --argjson id "$id" 'map(select(.id != $id))' "$state" > "$state.new"; mv "$state.new" "$state" ;;
  PATCH)
    for f in "${fields[@]}"; do [ "$f" = "draft=false" ] || { echo "fake gh: unsupported field $f" >&2; exit 2; }; done
    jq --argjson id "$id" 'map(if .id == $id then .draft = false else . end)' "$state" > "$state.new"; mv "$state.new" "$state"
    jq -r --argjson id "$id" ".[] | select(.id == \$id) | $jqx" "$state" ;;
  *) echo "fake gh: unsupported method $method" >&2; exit 2 ;;
esac
FAKE
  chmod +x "$1"
}

expect_failure() {
  local what="$1"; shift
  if ( "$@" ) > /dev/null 2>&1; then die "self-test: $what was accepted"; fi
  echo "refused, as it must be: $what"
}

cmd_self_test() {
  local tmp; tmp="$(mktemp -d)"
  trap 'rm -rf "$tmp"' RETURN
  write_fake_gh "$tmp/gh"
  export GH="$tmp/gh" FAKE_GH_STATE="$tmp/releases.json" FAKE_GH_LOG="$tmp/gh.log"
  local repo=jedbjorn/rst-c tag=v9.9.9 a="$tmp/assets"
  mkdir -p "$a"
  printf 'msi bytes' > "$a/RST.msi"; printf 'exe bytes' > "$a/RST-Setup.exe"
  "$HERE/sha256sums.sh" write "$a" RST.msi RST-Setup.exe > /dev/null
  export RELEASE_ASSETS="RST.msi RST-Setup.exe"   # release.yml's one list of assets
  count() { jq --arg t "$2" "[.[] | select(.tag_name == \$t and ($1))] | length" "$FAKE_GH_STATE"; }
  check() { [ "$1" = "$2" ] || die "self-test: $3 (expected $2, got $1)"; echo "ok  $3"; }

  # 1. A clean run: one draft, verified by id, published by id.
  echo '[{"id": 7, "tag_name": "v1.0.0", "draft": false, "prerelease": false, "html_url": "x", "assets": []}]' > "$FAKE_GH_STATE"
  ( cmd_prepare "$repo" "$tag" ) > /dev/null
  local id; id="$( cmd_create "$repo" "$tag" "$a" )"
  ( cmd_verify "$repo" "$id" "$tag" "$a/SHA256SUMS.txt" ) > /dev/null
  ( cmd_publish "$repo" "$id" "$tag" ) > /dev/null
  check "$(count 'true' "$tag")" 1 "a clean run makes one release for the tag"
  check "$(count '.draft == false' "$tag")" 1 "and publishes it"

  # 2. A re-run after a failed verification: the stale draft is deleted by id, not doubled.
  echo '[]' > "$FAKE_GH_STATE"
  ( "$GH" release create "$tag" "$a/RST.msi" --draft )   # the earlier run's draft, missing two assets
  local stale; stale="$(jq '.[0].id' "$FAKE_GH_STATE")"
  expect_failure "verifying the stale draft (it lacks assets)" cmd_verify "$repo" "$stale" "$tag" "$a/SHA256SUMS.txt"
  ( cmd_prepare "$repo" "$tag" ) > /dev/null
  grep -q "api -X DELETE repos/$repo/releases/$stale" "$FAKE_GH_LOG" || die "self-test: the stale draft was not deleted by its id"
  id="$( cmd_create "$repo" "$tag" "$a" )"
  [ "$id" != "$stale" ] || die "self-test: the new draft has the stale draft's id"
  check "$(count '.draft == true' "$tag")" 1 "a re-run leaves exactly one draft for the tag"
  ( cmd_verify "$repo" "$id" "$tag" "$a/SHA256SUMS.txt" ) > /dev/null
  ( cmd_publish "$repo" "$id" "$tag" ) > /dev/null
  check "$(count 'true' "$tag")" 1 "and one release once published"

  # 3. Two stale drafts (two failed runs): both go; another tag's draft stays.
  echo '[]' > "$FAKE_GH_STATE"
  ( "$GH" release create "$tag" "$a/RST.msi" --draft ); ( "$GH" release create "$tag" "$a/RST.msi" --draft )
  ( "$GH" release create v9.9.8 "$a/RST.msi" --draft )
  ( cmd_prepare "$repo" "$tag" ) > /dev/null
  check "$(count 'true' "$tag")" 0 "both stale drafts for the tag are deleted"
  check "$(count '.draft == true' v9.9.8)" 1 "another tag's draft is left alone"

  # 4. A re-run after publication: refused, nothing created or deleted.
  echo '[]' > "$FAKE_GH_STATE"
  id="$( cmd_create "$repo" "$tag" "$a" )"; ( cmd_publish "$repo" "$id" "$tag" ) > /dev/null
  ( "$GH" release create "$tag" "$a/RST.msi" --draft )   # even with a stray draft beside it
  expect_failure "preparing a tag that is already published" cmd_prepare "$repo" "$tag"
  check "$(count 'true' "$tag")" 2 "the published release (and the stray draft) are untouched"

  # 5. A second draft appearing between prepare and find: refused, not guessed.
  echo '[]' > "$FAKE_GH_STATE"
  ( "$GH" release create "$tag" "$a/RST.msi" --draft )
  expect_failure "creating while another draft for the tag exists" cmd_create "$repo" "$tag" "$a"

  # 5b. Review N7: an overlapping run publishes a stale draft between prepare's list and its DELETE.
  echo '[]' > "$FAKE_GH_STATE"
  ( "$GH" release create "$tag" "$a/RST.msi" --draft )
  local racing; racing="$(jq '.[0].id' "$FAKE_GH_STATE")"
  FAKE_GH_PUBLISH_ON_GET="$racing" expect_failure "deleting a draft that was published meanwhile" cmd_prepare "$repo" "$tag"
  grep -q "api -X DELETE repos/$repo/releases/$racing" "$FAKE_GH_LOG" && die "self-test: a release published meanwhile was deleted"
  check "$(count '.draft == false' "$tag")" 1 "the release published meanwhile is untouched"

  # 5c. The assets named explicitly instead of $RELEASE_ASSETS; a missing one is refused before anything is created.
  echo '[]' > "$FAKE_GH_STATE"
  expect_failure "an asset file that is missing" cmd_create "$repo" "$tag" "$a" RST.msi RST-Machine.msi
  check "$(count 'true' "$tag")" 0 "nothing was created"
  id="$( cmd_create "$repo" "$tag" "$a" RST.msi RST-Setup.exe )"
  ( cmd_verify "$repo" "$id" "$tag" "$a/SHA256SUMS.txt" ) > /dev/null

  # 6. Publish only the draft for this tag.
  echo '[]' > "$FAKE_GH_STATE"
  id="$( cmd_create "$repo" "$tag" "$a" )"
  expect_failure "publishing the draft under another tag" cmd_publish "$repo" "$id" v9.9.7
  ( cmd_publish "$repo" "$id" "$tag" ) > /dev/null
  expect_failure "publishing a release that is no longer a draft" cmd_publish "$repo" "$id" "$tag"
  expect_failure "verifying a release that is no longer a draft" cmd_verify "$repo" "$id" "$tag" "$a/SHA256SUMS.txt"
  expect_failure "a release id that does not exist" cmd_publish "$repo" 424242 "$tag"
  echo "self-test passed ($(wc -l < "$FAKE_GH_LOG") fake gh calls; nothing sent to GitHub)"
}

case "${1:-}" in
  prepare) shift; [ $# -eq 2 ] || die "usage: $0 prepare <repo> <tag>"; cmd_prepare "$@" ;;
  create) shift; [ $# -ge 3 ] || die "usage: $0 create <repo> <tag> <dir> [<asset>...]"; cmd_create "$@" ;;
  verify) shift; [ $# -eq 4 ] || die "usage: $0 verify <repo> <id> <tag> <SHA256SUMS.txt>"; cmd_verify "$@" ;;
  publish) shift; [ $# -eq 3 ] || die "usage: $0 publish <repo> <id> <tag>"; cmd_publish "$@" ;;
  self-test) shift; cmd_self_test ;;
  *) die "usage: $0 prepare|create|verify|publish|self-test ..." ;;
esac
