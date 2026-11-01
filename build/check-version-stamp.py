#!/usr/bin/env python3
"""check-version-stamp.py: the staged RST binaries carry the release version (SC-054).

Usage: build/check-version-stamp.py <version> <file.dll|file.exe>...

<version> is what build/stage.sh passed as -p:Version (a.b.c, or a.b.c-pre such as the
<RstDevVersion>-dev default of a dev or PR build, 3.2.99-dev). Each file's Win32 version resource must say:
  - FileVersion (the fixed VS_FIXEDFILEINFO and the string) = a.b.c.0
  - ProductVersion (the SDK writes InformationalVersion there) = <version> or <version>+<commit>
Reads the PE's version resource directly (no Windows tools on the Linux runner). Exits 1 on
any mismatch, naming the file and what it found.
"""

import re
import struct
import sys

FIXED_SIGNATURE = b"\xbd\x04\xef\xfe"  # VS_FIXEDFILEINFO.dwSignature, little-endian 0xFEEF04BD


def version_info(data: bytes) -> bytes:
    """The VS_VERSIONINFO resource onwards (searched from the end: .rsrc is the PE's last section), or b""."""
    at = data.rfind("VS_VERSION_INFO\0".encode("utf-16-le"))
    return data[at:] if at >= 0 else b""


def string_value(data: bytes, key: str):
    """The value of a StringFileInfo entry (UTF-16LE key, padding, UTF-16LE value), or None."""
    start = data.find("StringFileInfo\0".encode("utf-16-le"))
    if start < 0:
        return None
    needle = (key + "\0").encode("utf-16-le")
    at = data.find(needle, start)
    if at < 0:
        return None
    at += len(needle)
    while data[at:at + 2] == b"\0\0":  # padding to the next 32-bit boundary
        at += 2
    end = at
    while end + 1 < len(data) and data[end:end + 2] != b"\0\0":
        end += 2
    return data[at:end].decode("utf-16-le", errors="replace")


def fixed_file_version(data: bytes):
    at = data.find(FIXED_SIGNATURE)
    if at < 0:
        return None
    _sig, _struc, ms, ls = struct.unpack_from("<IIII", data, at)
    return f"{ms >> 16}.{ms & 0xFFFF}.{ls >> 16}.{ls & 0xFFFF}"


def main(argv):
    if len(argv) < 3:
        print(__doc__.strip(), file=sys.stderr)
        return 2
    version, files = argv[1], argv[2:]
    numeric = version.split("-")[0].split("+")[0]
    if not re.fullmatch(r"\d+\.\d+\.\d+", numeric):
        print(f"version '{version}' is not a.b.c or a.b.c-pre", file=sys.stderr)
        return 2
    want_file = numeric + ".0"
    product = re.compile(re.escape(version) + r"(\+[0-9a-f]+)?")

    failed = False
    for path in files:
        try:
            with open(path, "rb") as f:
                data = version_info(f.read())
        except OSError as e:
            print(f"FAIL {path}: {e}", file=sys.stderr)
            failed = True
            continue
        fixed = fixed_file_version(data)
        text = string_value(data, "FileVersion")
        prod = string_value(data, "ProductVersion")
        problems = []
        if fixed != want_file:
            problems.append(f"fixed FileVersion {fixed}, expected {want_file}")
        if text != want_file:
            problems.append(f"FileVersion string {text!r}, expected {want_file!r}")
        if prod is None or not product.fullmatch(prod):
            problems.append(f"ProductVersion {prod!r}, expected {version!r} or {version}+<commit>")
        if problems:
            print(f"FAIL {path}: " + "; ".join(problems), file=sys.stderr)
            failed = True
        else:
            print(f"ok   {path}: FileVersion {fixed}, ProductVersion {prod}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
