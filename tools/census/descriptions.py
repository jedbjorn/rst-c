#!/usr/bin/env python3
"""descriptions.py <year>: census/<year>/descriptions.tsv from a year's census run.

Revit writes one journal line per command it starts: `Jrn.Command "Ribbon" , "Create a wall , ID_OBJECTS_WALL"`. The
text before the id is Revit's own one-line description of the command. The census recorded that line for every tool it
started (rows.tsv, `journal` column; the raw run lives under shared/evidence/census/<year>/census/, never in a
commit). This script keeps the description per id, so the published index can offer what was recorded.

Rules: only a journal line that is exactly one `Jrn.Command` whose trailing id is the row's own id counts (lines that
carry an undo or another command are dropped); an empty description is dropped; an id with several descriptions
gets the most common one. Output columns: id, description. Python 3 stdlib only.
"""
import csv
import re
import subprocess
import sys
from collections import Counter, defaultdict
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
LINE = re.compile(r'^Jrn\.Command\s+"[A-Za-z]+"\s*,\s*"([^"|]*?)\s,\s([^\s",|]+)"\s*$')


def main_checkout():
    common = subprocess.run(["git", "rev-parse", "--path-format=absolute", "--git-common-dir"], cwd=HERE,
                            capture_output=True, text=True, check=True).stdout.strip()
    return Path(common).resolve().parent


def clean(s):
    return re.sub(r"\s+", " ", s).strip()


def main(argv):
    year = argv[0]
    rows = main_checkout() / "shared" / "evidence" / "census" / year / "census" / "rows.tsv"
    csv.field_size_limit(1 << 30)
    per = defaultdict(Counter)
    with open(rows, encoding="utf-8", newline="") as f:
        for r in csv.DictReader(f, delimiter="\t", quoting=csv.QUOTE_NONE):
            m = LINE.match(r["journal"] or "")
            if m and m.group(2) == r["id"] and clean(m.group(1)):
                per[r["id"]][clean(m.group(1))] += 1
    out = REPO / "census" / year / "descriptions.tsv"
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        f.write("id\tdescription\n")
        for i in sorted(per):
            f.write(f"{i}\t{per[i].most_common(1)[0][0].replace(chr(9), ' ')}\n")
    print(f"{year}: {len(per)} ids with a description -> {out.relative_to(REPO)}")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    main(sys.argv[1:])
