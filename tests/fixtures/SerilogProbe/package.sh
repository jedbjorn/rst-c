#!/usr/bin/env bash
# package.sh — lay the Serilog probes' 2026 and 2027 builds out as the CI artifact "SerilogProbe"
# (VM step V3 of the v3.0.0 resolver hardening, decision #27). rst-c's own (not vendored).
# Build first:
#   for p in AAA ZZZ; do for c in "Release R26" "Release R27"; do
#     dotnet build tests/fixtures/SerilogProbe/$p/$p-SerilogProbe.csproj -c "$c"; done; done
# Usage: tests/fixtures/SerilogProbe/package.sh <out-dir>
#
# Layout:
#   <year>/AAA-SerilogProbe.addin + <year>/AAA-SerilogProbe/{AAA-SerilogProbe.dll, Serilog*.dll, …}
#   <year>/ZZZ-SerilogProbe.addin + <year>/ZZZ-SerilogProbe/{ZZZ-SerilogProbe.dll, Serilog*.dll, …}
#       → %AppData%\Autodesk\Revit\Addins\<year>\   (next to RST.addin)
#   serilog-probe.ps1  the guest helper (install, uninstall, show)

set -euo pipefail

if [ $# -ne 1 ]; then
  echo "usage: $0 <out-dir>" >&2
  exit 1
fi

HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$1"
rm -rf "$OUT"
mkdir -p "$OUT"

for probe in "AAA:3.1.1" "ZZZ:4.2.0"; do
  IFS=: read -r P SERILOG <<<"$probe"
  NAME="$P-SerilogProbe"
  for pair in "2026:net8.0-windows:R26" "2027:net10.0-windows:R27"; do
    IFS=: read -r YEAR TFM MAJOR <<<"$pair"
    BIN="$HERE/$P/bin/Release $MAJOR/$TFM"
    if [ ! -f "$BIN/$NAME.dll" ]; then
      echo "missing $BIN/$NAME.dll: build $P -c \"Release $MAJOR\" first" >&2
      exit 2
    fi
    # The probe must carry the Serilog it was built for (that is the whole test).
    if ! grep -q "\"Serilog/$SERILOG\"" "$BIN/$NAME.deps.json"; then
      echo "$NAME ($MAJOR) does not carry Serilog $SERILOG" >&2
      exit 4
    fi
    mkdir -p "$OUT/$YEAR/$NAME"
    cp "$HERE/$P/$NAME.addin" "$OUT/$YEAR/$NAME.addin"
    cp "$BIN/$NAME.dll" "$BIN/$NAME.deps.json" "$BIN"/Serilog*.dll "$OUT/$YEAR/$NAME/"
  done
done
cp "$HERE/serilog-probe.ps1" "$OUT/serilog-probe.ps1"

# Nothing of Revit's may travel: the Revit API is Revit's own.
if find "$OUT" -name 'RevitAPI*.dll' -o -name 'AdWindows*.dll' | grep -q .; then
  echo "Revit assemblies leaked into the artifact" >&2
  exit 3
fi
find "$OUT" -type f | sort
