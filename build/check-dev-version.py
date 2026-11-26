#!/usr/bin/env python3
"""check-dev-version.py: fail when the post-release bump was missed.

After each release, RstDevVersion (Directory.Build.props) must become <that release's a.b>.99, and
msi-migrate's latest-release leg (.github/workflows/_build.yml) must move to that release
(installer/README.md, "Untagged builds"). A dev build versioned below the latest release cannot
upgrade a released install ("A newer version of RST is already installed."), and an upgrade leg
left on an older release no longer proves a dev build upgrades the current one. v3.3.0 to v3.6.0
shipped with RstDevVersion still 3.2.99.

Checks, against the latest published full release (a tag such as v3.7.0):
  - RstDevVersion sorts above its numeric a.b.c;
  - _build.yml has a msi-migrate leg `- release: <that tag>`.

Usage: build/check-dev-version.py <latest release tag>   (from anywhere in the repository)
"""

import re
import subprocess
import sys
from pathlib import Path


def numeric(version: str) -> tuple[int, ...]:
    match = re.match(r"v?(\d+)\.(\d+)\.(\d+)", version)
    if not match:
        sys.exit(f"check-dev-version: not a version: {version!r}")
    return tuple(int(part) for part in match.groups())


def main() -> int:
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    tag = sys.argv[1].strip()
    root = Path(subprocess.check_output(["git", "rev-parse", "--show-toplevel"], text=True).strip())

    props = (root / "Directory.Build.props").read_text(encoding="utf-8")
    dev = re.search(r"<RstDevVersion>([^<]+)</RstDevVersion>", props)
    if not dev:
        sys.exit("check-dev-version: no <RstDevVersion> in Directory.Build.props")

    workflow = (root / ".github/workflows/_build.yml").read_text(encoding="utf-8")
    legs = re.findall(r"^\s*- release: (\S+)\s*$", workflow, re.M)

    errors = []
    if numeric(dev.group(1)) <= numeric(tag):
        errors.append(f"RstDevVersion {dev.group(1)} is not above the latest release {tag}: "
                      f"bump it to {'.'.join(map(str, numeric(tag)[:2]))}.99 in Directory.Build.props.")
    if tag not in legs:
        errors.append(f"msi-migrate has no leg for the latest release {tag} (legs: {', '.join(legs)}): "
                      f"move the latest-release leg in .github/workflows/_build.yml to {tag}.")
    for error in errors:
        print(f"::error::{error}")
    if errors:
        print('See installer/README.md, "Untagged builds".')
        return 1
    print(f"RstDevVersion {dev.group(1)} is above {tag}, and msi-migrate upgrades from {tag}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
