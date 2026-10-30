#!/usr/bin/env bash
# sha256sums.sh: a release's SHA256SUMS.txt (feature #17, spec #20 §8; F17 WU3). The RST tray (WU4) installs
# a download only when its sha256 matches its line here AND the asset digest GitHub computed, so the file must
# list exactly the release's assets, in `sha256sum` format, as the sister repos publish it:
#
#   06f403468ae484526dc7e8ddc9251bf2b34715ddcb14d668049b63c74d1eb18b  RST.msi
#
# Usage:
#   build/sha256sums.sh write <dir> <asset>...
#       Writes <dir>/SHA256SUMS.txt with one line per named asset (bare file names in <dir>), in that order,
#       then checks it: the names are exactly the assets, every line is '<64 hex>  <name>', and every hash
#       verifies (sha256sum --check --strict).
#   build/sha256sums.sh check-release <SHA256SUMS.txt> <assets.json>
#       Checks a published release: <assets.json> is `gh release view <tag> --json assets`. Every asset other
#       than SHA256SUMS.txt has exactly one line, every line names an asset, and each line's hash equals the
#       asset's GitHub digest (sha256:<hex>, computed by GitHub on upload).
#   build/sha256sums.sh self-test <dir> <asset>...
#       CI on every PR: `write`, then `check-release` against an assets list built from the same files, then the
#       same check against a wrong digest, a missing line and an extra line, each of which must fail.
set -euo pipefail

die() { echo "sha256sums: $*" >&2; exit 1; }
LINE='^[0-9a-f]{64}  [^/\\[:space:]][^/\\]*$'

cmd_write() {
  local dir="$1"; shift
  [ $# -gt 0 ] || die "write: name at least one asset"
  [ -d "$dir" ] || die "write: no folder $dir"
  for a in "$@"; do
    case "$a" in */* | *\\* | SHA256SUMS.txt) die "write: '$a' is not an asset file name" ;; esac
    [ -f "$dir/$a" ] || die "write: missing asset $dir/$a"
  done
  ( cd "$dir" && sha256sum -- "$@" > SHA256SUMS.txt )
  local sums="$dir/SHA256SUMS.txt"
  grep -Evq "$LINE" "$sums" && die "write: a line is not '<sha256>  <name>'"
  local names
  names="$(sed -E 's/^[0-9a-f]{64}  //' "$sums" | paste -sd ' ')"
  [ "$names" = "$*" ] || die "write: the lines name '$names', the assets are '$*'"
  ( cd "$dir" && sha256sum --check --strict --quiet SHA256SUMS.txt ) || die "write: a hash does not verify"
  echo "SHA256SUMS.txt ($(wc -l < "$sums") lines):"
  cat "$sums"
}

cmd_check_release() {
  local sums="$1" assets="$2"
  [ -f "$sums" ] || die "check-release: no $sums"
  [ -f "$assets" ] || die "check-release: no $assets"
  grep -Evq "$LINE" "$sums" && die "check-release: a line is not '<sha256>  <name>'"
  local dupes
  dupes="$(sed -E 's/^[0-9a-f]{64}  //' "$sums" | sort | uniq -d)"
  [ -z "$dupes" ] || die "check-release: listed twice: $dupes"

  local published listed
  published="$(jq -r '.assets[] | select(.name != "SHA256SUMS.txt") | .name' "$assets" | sort)"
  listed="$(sed -E 's/^[0-9a-f]{64}  //' "$sums" | sort)"
  [ -n "$published" ] || die "check-release: the release has no assets"
  [ "$published" = "$listed" ] || die "check-release: assets [$(echo $published)] but SHA256SUMS.txt lists [$(echo $listed)]"

  local name hash digest bad=0
  while read -r hash name; do
    digest="$(jq -r --arg n "$name" '.assets[] | select(.name == $n) | .digest // ""' "$assets")"
    if [ "$digest" != "sha256:$hash" ]; then
      echo "sha256sums: $name: SHA256SUMS.txt says $hash, GitHub's digest is '${digest:-none}'" >&2
      bad=1
    else
      echo "ok  $name  $hash"
    fi
  done < "$sums"
  [ "$bad" -eq 0 ] || die "check-release: digests do not match"
  echo "SHA256SUMS.txt matches every published asset and its GitHub digest"
}

# An assets list in `gh release view --json assets` shape, from the files themselves.
assets_json() {
  local dir="$1"; shift
  local items=() a h
  for a in "$@" SHA256SUMS.txt; do
    h="$(sha256sum -- "$dir/$a" | cut -d' ' -f1)"
    items+=("$(jq -cn --arg n "$a" --arg d "sha256:$h" '{name: $n, digest: $d}')")
  done
  printf '%s\n' "${items[@]}" | jq -s '{assets: .}'
}

expect_failure() {
  local what="$1"; shift
  if ( "$@" ) > /dev/null 2>&1; then die "self-test: $what was accepted"; fi
  echo "refused, as it must be: $what"
}

cmd_self_test() {
  local dir="$1"; shift
  cmd_write "$dir" "$@"
  local tmp; tmp="$(mktemp -d)"
  trap 'rm -rf "$tmp"' RETURN
  assets_json "$dir" "$@" > "$tmp/assets.json"
  cmd_check_release "$dir/SHA256SUMS.txt" "$tmp/assets.json"

  jq '.assets[0].digest = "sha256:" + ("0" * 64)' "$tmp/assets.json" > "$tmp/wrong-digest.json"
  expect_failure "a digest that differs from its line" cmd_check_release "$dir/SHA256SUMS.txt" "$tmp/wrong-digest.json"
  jq '.assets += [{name: "Extra.msi", digest: "sha256:\("1" * 64)"}]' "$tmp/assets.json" > "$tmp/extra-asset.json"
  expect_failure "an asset missing from SHA256SUMS.txt" cmd_check_release "$dir/SHA256SUMS.txt" "$tmp/extra-asset.json"
  { cat "$dir/SHA256SUMS.txt"; printf '%s  Ghost.msi\n' "$(printf '2%.0s' {1..64})"; } > "$tmp/extra-line.txt"
  expect_failure "a line for an asset the release does not have" cmd_check_release "$tmp/extra-line.txt" "$tmp/assets.json"
  sed 's/  / /' "$dir/SHA256SUMS.txt" > "$tmp/one-space.txt"
  expect_failure "a malformed line" cmd_check_release "$tmp/one-space.txt" "$tmp/assets.json"
  expect_failure "a missing asset file" cmd_write "$dir" "$@" Missing.msi
  # write refuses before it writes, so the good file is still there.
  cmd_check_release "$dir/SHA256SUMS.txt" "$tmp/assets.json" > /dev/null
  echo "self-test passed"
}

case "${1:-}" in
  write) shift; [ $# -ge 2 ] || die "usage: $0 write <dir> <asset>..."; cmd_write "$@" ;;
  check-release) shift; [ $# -eq 2 ] || die "usage: $0 check-release <SHA256SUMS.txt> <assets.json>"; cmd_check_release "$@" ;;
  self-test) shift; [ $# -ge 2 ] || die "usage: $0 self-test <dir> <asset>..."; cmd_self_test "$@" ;;
  *) die "usage: $0 write|check-release|self-test ..." ;;
esac
