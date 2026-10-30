#!/usr/bin/env bash
# check-shipped-dlls.sh — decision #33: RST ships no third-party DLL into Revit's load context.
#
# Revit loads every add-in into one shared .NET load context, and Revit 2027 binds a same-named
# assembly from another add-in's folder before any resolver runs. A third-party DLL in RST's
# app folder (Serilog, System.Management, …) can therefore silently be replaced by a neighbour's
# copy of another version, or stop RST from starting. So the staged folders hold only RST's own
# assemblies (RST.dll, RST.*.dll), plus whatever build/third-party-allowlist.txt names with a
# justification. A new dependency is then a deliberate choice: it fails here until it is listed.
#
# Usage: build/check-shipped-dlls.sh <staged dir>...
#   e.g. build/check-shipped-dlls.sh build/R25     (addins/ and app/ of one Revit year)
#        build/check-shipped-dlls.sh build/Tray    (the tray: not in Revit, the same rule)
# Exit 0 when every DLL is RST's or allowlisted; 1 (listing the offenders) otherwise.

set -euo pipefail

if [ $# -lt 1 ]; then
  echo "usage: $0 <staged dir>..." >&2
  exit 2
fi

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ALLOWLIST="$ROOT/build/third-party-allowlist.txt"

# The allowlist: one DLL file name per line (case-insensitive), then "#" and why. Blank lines and
# lines starting with "#" are comments.
allowed=()
if [ -f "$ALLOWLIST" ]; then
  while IFS= read -r line || [ -n "$line" ]; do
    name="${line%%#*}"
    name="$(echo "$name" | tr -d '[:space:]')"
    [ -n "$name" ] && allowed+=("$(echo "$name" | tr '[:upper:]' '[:lower:]')")
  done < "$ALLOWLIST"
fi

fail=0
for dir in "$@"; do
  if [ ! -d "$dir" ]; then
    echo "check-shipped-dlls: $dir is not a directory" >&2
    exit 2
  fi
  total=0
  bad=()
  while IFS= read -r -d '' f; do
    total=$((total + 1))
    base="$(basename "$f")"
    lower="$(echo "$base" | tr '[:upper:]' '[:lower:]')"
    case "$lower" in
      rst.dll|rst.*.dll) continue ;;
    esac
    ok=0
    for a in "${allowed[@]+"${allowed[@]}"}"; do
      [ "$a" = "$lower" ] && ok=1 && break
    done
    [ $ok -eq 1 ] && continue
    bad+=("${f#"$dir"/}")
  done < <(find "$dir" -type f -iname '*.dll' -print0 | sort -z)

  if [ ${#bad[@]} -gt 0 ]; then
    echo "FAIL: $dir ships ${#bad[@]} third-party DLL(s) (decision #33; allowlist: build/third-party-allowlist.txt):" >&2
    printf '  %s\n' "${bad[@]}" >&2
    fail=1
  else
    echo "OK: $dir ships no third-party DLL ($total DLLs, all RST's own or allowlisted)"
    find "$dir" -type f -iname '*.dll' | sed "s|^$dir/|  |" | sort
  fi
done
exit $fail
