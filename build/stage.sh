#!/usr/bin/env bash
# stage.sh — produce the two-tree bundle under build/R<NN>/ for one Revit major.
#
# Layout produced (RST-033 split):
#   build/R<NN>/addins/{RST.addin, RST.Bootstrap.dll, RST.Bootstrap.pdb}
#       → drops into %AppData%\Autodesk\Revit\Addins\<ver>\
#   build/R<NN>/app/{RST.Engine.dll, RST.UI.dll, RST.Core.dll, Serilog*,
#                    System.Management.dll, runtimes/, Assets/,
#                    *.deps.json, *.pdb}
#       → drops into %AppData%\RST\R<NN>\app\
#
# Usage: build/stage.sh R25|R26|R27 [Release|Debug]
#        build/stage.sh tray [Release|Debug]
#   default config is Release.
#
# The tray target (feature #17, spec #20 §1) publishes RST.Tray.exe ONCE, not
# per Revit year, into build/Tray/ (-> %LocalAppData%\Programs\RST\Tray\):
#   build/Tray/{RST.Tray.exe, RST.Tray.dll, RST.Tray.runtimeconfig.json,
#               RST.Tray.deps.json, RST.UI.dll, RST.Core.dll, Serilog*,
#               System.Management.dll, *.pdb}
# framework-dependent net8.0-windows (RollForward=Major), win-x64 apphost.
# It is built with the R25 configuration, where RST.UI is net8.0-windows
# too. RST_VERSION (a.b.c, optional) stamps the tray's version, which its
# About page shows; unset, the tray says 0.0.0-dev.

set -euo pipefail

if [ $# -lt 1 ]; then
  echo "usage: $0 R25|R26|R27|tray [Release|Debug]" >&2
  exit 1
fi

MAJOR="$1"
KIND="${2:-Release}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

# ---- tray target: one build for every Revit year.
if [ "$MAJOR" = "tray" ]; then
  OUT="$ROOT/build/Tray"
  VERSION="${RST_VERSION:-0.0.0-dev}"
  echo "==> dotnet publish RST.Tray -c \"$KIND R25\" (version $VERSION)"
  rm -rf "$OUT"
  dotnet publish "$ROOT/src/RST.Tray/RST.Tray.csproj" -c "$KIND R25" \
    -r win-x64 --self-contained false -p:Version="$VERSION" \
    -o "$OUT" --nologo -v minimal
  # RST.UI's add-in lookup (a Builder asset) rides along as content; the
  # tray never reads it.
  rm -rf "$OUT/Assets"
  for f in RST.Tray.exe RST.Tray.dll RST.Tray.runtimeconfig.json RST.UI.dll RST.Core.dll; do
    if [ ! -f "$OUT/$f" ]; then echo "tray publish is missing $f" >&2; exit 2; fi
  done
  grep -q '"rollForward": "Major"' "$OUT/RST.Tray.runtimeconfig.json" \
    || { echo "RST.Tray.runtimeconfig.json lacks rollForward Major" >&2; exit 2; }
  echo "==> staged:"
  ( cd "$ROOT/build" && find Tray -type f | sort | sed 's/^/    /' )
  echo "==> done"
  exit 0
fi

CONFIG="$KIND $MAJOR"

case "$MAJOR" in
  R25|R26) TFM="net8.0-windows" ;;
  R27)     TFM="net10.0-windows" ;;
  *)       echo "unknown target: $MAJOR (expected R25|R26|R27|tray)" >&2; exit 1 ;;
esac
ENGINE_BIN="$ROOT/src/RST.Engine/bin/$CONFIG/$TFM"
BOOT_BIN="$ROOT/src/RST.Bootstrap/bin/$CONFIG/$TFM"
ADDIN_SRC="$ROOT/src/RST.Bootstrap/RST.addin"
OUT="$ROOT/build/$MAJOR"

echo "==> dotnet build -c \"$CONFIG\""
dotnet build "$ROOT/RST-C.sln" -c "$CONFIG" --nologo -v minimal

if [ ! -d "$ENGINE_BIN" ] || [ ! -d "$BOOT_BIN" ]; then
  echo "expected bin dirs missing after build:" >&2
  echo "  engine: $ENGINE_BIN" >&2
  echo "  boot:   $BOOT_BIN" >&2
  exit 2
fi

echo "==> staging $OUT"
rm -rf "$OUT/addins" "$OUT/app"
mkdir -p "$OUT/addins" "$OUT/app"

# Add-Ins payload — the .addin from source (avoids any post-stage rewrite),
# plus the bootstrap dll/pdb. Three files total.
cp "$ADDIN_SRC"             "$OUT/addins/RST.addin"
cp "$BOOT_BIN/RST.Bootstrap.dll" "$OUT/addins/"
cp "$BOOT_BIN/RST.Bootstrap.pdb" "$OUT/addins/"

# App payload — everything the engine emits, minus any bootstrap leftovers
# (defensive: the bootstrap project doesn't share its bin with engine).
cp -r "$ENGINE_BIN/." "$OUT/app/"
rm -f "$OUT/app/RST.Bootstrap.dll" \
      "$OUT/app/RST.Bootstrap.pdb" \
      "$OUT/app/RST.Bootstrap.deps.json"

# RST-038: drop the netstandard placeholder for System.Management. The
# Windows-specific implementation lives at runtimes/win/lib/net8.0/. With
# the bin-root stub present, AssemblyLoadContext.Default finds it FIRST
# and our AssemblyDependencyResolver fallback never fires — the stub
# throws PlatformNotSupportedException on every WMI query. Removing it
# forces the resolver to read RST.Engine.deps.json and return the
# runtime-specific path. (Same risk applies to any future package that
# ships both a netstandard ref + a runtimes/win-* implementation.)
rm -f "$OUT/app/System.Management.dll"

echo "==> staged:"
( cd "$OUT" && find addins app -type f | sort | sed 's/^/    /' )
echo "==> done"
