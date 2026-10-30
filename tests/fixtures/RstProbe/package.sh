#!/usr/bin/env bash
# package.sh — lay RST-Probe's 2026 and 2027 builds out as the CI artifact "RST-Probe"
# (feature #16, spec #18, AC5). rst-c's own (not vendored). Build first:
#   dotnet build tests/fixtures/RstProbe/RstProbe.csproj -c "Release R26"
#   dotnet build tests/fixtures/RstProbe/RstProbe.csproj -c "Release R27"
# Usage: tests/fixtures/RstProbe/package.sh <out-dir>
#
# Layout:
#   user/<year>/RstProbe.addin + user/<year>/RstProbe/RstProbe.dll
#       → %AppData%\Autodesk\Revit\Addins\<year>\  (tab "RSTProbe")
#   bundle/RstProbe.bundle/{PackageContents.xml, Contents/<year>/{RstProbeBundle.addin, RstProbe.dll}}
#       → %AppData%\Autodesk\ApplicationPlugins\    (tab "RSTProbeBundle")
#   rst-probe.ps1  the guest helper (stage, v3.0 rename, admin lock, show, journal)

set -euo pipefail

if [ $# -ne 1 ]; then
  echo "usage: $0 <out-dir>" >&2
  exit 1
fi

HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$1"
rm -rf "$OUT"
mkdir -p "$OUT"

for pair in "2026:net8.0-windows:R26" "2027:net10.0-windows:R27"; do
  IFS=: read -r YEAR TFM MAJOR <<<"$pair"
  BIN="$HERE/bin/Release $MAJOR/$TFM"
  if [ ! -f "$BIN/RstProbe.dll" ]; then
    echo "missing $BIN/RstProbe.dll: build -c \"Release $MAJOR\" first" >&2
    exit 2
  fi
  mkdir -p "$OUT/user/$YEAR/RstProbe" "$OUT/bundle/RstProbe.bundle/Contents/$YEAR"
  cp "$HERE/RstProbe.addin" "$OUT/user/$YEAR/RstProbe.addin"
  cp "$BIN/RstProbe.dll" "$OUT/user/$YEAR/RstProbe/RstProbe.dll"
  cp "$HERE/RstProbeBundle.addin" "$OUT/bundle/RstProbe.bundle/Contents/$YEAR/RstProbeBundle.addin"
  cp "$BIN/RstProbe.dll" "$OUT/bundle/RstProbe.bundle/Contents/$YEAR/RstProbe.dll"
done
cp "$HERE/PackageContents.xml" "$OUT/bundle/RstProbe.bundle/PackageContents.xml"
cp "$HERE/rst-probe.ps1" "$OUT/rst-probe.ps1"

# Nothing but RstProbe.dll may travel: the Revit API is Revit's own.
if find "$OUT" -name 'RevitAPI*.dll' | grep -q .; then
  echo "RevitAPI assemblies leaked into the artifact" >&2
  exit 3
fi
find "$OUT" -type f | sort
