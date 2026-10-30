#!/usr/bin/env python3
"""check-revit-api-floor.py: fail when a Revit API package is pinned above the supported floor.

Decision #26: RST supports every update of Revit 2025, 2026 and 2027, so every project compiles
against each year's earliest update. A reference to a later update would let code use API that
older updates lack (TypeLoadException / MissingMethodException at run time on those updates).

Checks every tracked *.csproj, *.props and *.targets for Nice3point.Revit.Api.* PackageReference
or PackageVersion items, and fails when a Version or VersionOverride is above FLOOR for its year,
when an item has no version, or when a year has no floor here.

Raising the floor is deliberate: change FLOOR below in the same PR that needs the newer API, and
update the Compatibility section of README.md and docs/DEVELOPING.md ("Revit API floor").

Usage: build/check-revit-api-floor.py   (from anywhere in the repository)
"""

import re
import subprocess
import sys
from pathlib import Path

# The earliest supported update per Revit year (Nice3point.Revit.Api package versions).
FLOOR = {
    2025: (2025, 0, 2),
    2026: (2026, 0, 4),
    2027: (2027, 0, 0),
}

ITEM = re.compile(r"<(PackageReference|PackageVersion)\b([^>]*)>", re.S)
ATTR = re.compile(r'\b(Include|Update|Version|VersionOverride)\s*=\s*"([^"]*)"')


def parse(version):
    parts = version.strip().split("-")[0].split(".")
    if not parts or not all(p.isdigit() for p in parts):
        return None
    return tuple(int(p) for p in parts) + (0,) * (3 - len(parts))


def main():
    root = Path(subprocess.check_output(["git", "rev-parse", "--show-toplevel"], text=True).strip())
    files = subprocess.check_output(
        ["git", "ls-files", "*.csproj", "*.props", "*.targets"], cwd=root, text=True
    ).split()
    files = [f for f in files if not f.startswith(".super-coder/")]

    problems, checked = [], 0
    for rel in files:
        text = (root / rel).read_text(encoding="utf-8-sig")
        for m in ITEM.finditer(text):
            attrs = dict(ATTR.findall(m.group(2)))
            package = attrs.get("Include") or attrs.get("Update") or ""
            if not package.startswith("Nice3point.Revit.Api."):
                continue
            line = text.count("\n", 0, m.start()) + 1
            where = f"{rel}:{line} {package}"
            versions = [(k, attrs[k]) for k in ("Version", "VersionOverride") if k in attrs]
            if not versions:
                problems.append(f"{where}: no Version or VersionOverride")
                continue
            for key, value in versions:
                checked += 1
                v = parse(value)
                if v is None:
                    problems.append(f'{where}: {key}="{value}" is not a plain version')
                    continue
                floor = FLOOR.get(v[0])
                if floor is None:
                    problems.append(f'{where}: {key}="{value}": no floor for Revit {v[0]} in build/check-revit-api-floor.py')
                elif v > floor:
                    problems.append(
                        f'{where}: {key}="{value}" is above the Revit {v[0]} floor {".".join(map(str, floor))}'
                    )

    if problems:
        print("Revit API floor check FAILED (decision #26):")
        for p in problems:
            print("  " + p)
        print("To raise the floor on purpose, edit FLOOR in build/check-revit-api-floor.py and the docs it names.")
        return 1
    if checked == 0:
        print("Revit API floor check FAILED: no Nice3point.Revit.Api references found (is the pattern stale?)")
        return 1
    floors = ", ".join(".".join(map(str, f)) for f in FLOOR.values())
    print(f"Revit API floor check passed: {checked} versions across {len(files)} project files, all at or below {floors}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
