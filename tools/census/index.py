#!/usr/bin/env python3
"""index.py: the one-row-per-command index, from the committed census tables.

Reads census/<year>/full.tsv, contexts.tsv and descriptions.tsv for every year folder and writes:

  census/commands.tsv        one row per command id, both years side by side
  census/<year>/matrix.tsv   one row per id, one column per context: what the id did there
  census/COMMANDS.md         the same index as a page to browse on GitHub, grouped by ribbon tab

Nothing here is measured again; every value is derived from the committed tables. Per id and context an id's places
are folded into one result by this priority: executes, greyed, not-runnable (accepted-nothing or refused), skipped,
not-a-command, not-drawn. Python 3 stdlib only.
"""
import csv
import sys
from collections import Counter, defaultdict
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent.parent
CENSUS = REPO / "census"

PRIORITY = ["executes", "greyed", "not-runnable", "skipped", "not-a-command", "not-drawn"]
FOLD = {"executes": "executes", "greyed": "greyed", "accepted-nothing": "not-runnable", "refused": "not-runnable",
        "skipped": "skipped", "not-a-command": "not-a-command", "not-drawn": "not-drawn"}


def read(path):
    with open(path, encoding="utf-8", newline="") as f:
        return list(csv.DictReader(f, delimiter="\t", quoting=csv.QUOTE_NONE))


def fold(results):
    for p in PRIORITY:
        if p in results:
            return p
    return ""


def load(year):
    d = CENSUS / year
    rows = read(d / "full.tsv")
    desc = {r["id"]: r["description"] for r in read(d / "descriptions.tsv")}
    ctxs = [r["context"] for r in read(d / "contexts.tsv")]
    places = defaultdict(set)
    labels = defaultdict(Counter)
    per_ctx = defaultdict(lambda: defaultdict(set))
    opened = defaultdict(set)
    tested = defaultdict(set)
    for r in rows:
        i = r["id"]
        places[i].add((r["tab"], r["panel"], r["path"]))
        if r["label"]:
            labels[i][r["label"]] += 1
        per_ctx[i][r["context"]].add(FOLD[r["result"]])
        for o in filter(None, r["opened"].split(",")):
            opened[i].add(o)
        if r["tested"]:
            tested[i].add(r["tested"])
    state = {i: {c: fold(s) for c, s in m.items()} for i, m in per_ctx.items()}
    return {"year": year, "build": rows[0]["build"], "desc": desc, "ctxs": ctxs, "places": places,
            "labels": labels, "state": state, "opened": opened, "tested": tested}


def origin(i):
    return "add-in" if i.startswith("CustomCtrl_") else "built-in"


def esc(s):
    return s.replace("|", "\\|").replace("\n", " ")


def summary(y, i):
    st = y["state"].get(i)
    if st is None:
        return None
    c = Counter(st.values())
    proj = sorted(k.split("/", 1)[1] for k, v in st.items() if k.startswith("project/") and v == "executes")
    return c, proj


YEAR_COLS = ["places", "contexts", "executes", "greyed", "not_runnable", "not_drawn", "skipped", "not_a_command",
             "project_views_run", "tested", "opened"]


def main():
    years = sorted(p.name for p in CENSUS.iterdir() if p.is_dir() and p.name.isdigit())
    ys = {y: load(y) for y in years}
    ids = sorted({i for y in ys.values() for i in y["state"]})

    head = ["id", "origin", "label", "description", "years", "tabs", "panels"]
    for y in years:
        head += [f"{c}_{y}" for c in YEAR_COLS]
    out = ["\t".join(head)]
    md_rows = defaultdict(list)
    for i in ids:
        lab = Counter()
        tabs, panels, desc = set(), set(), ""
        for y in reversed(years):                      # the newer year's wording wins
            lab += ys[y]["labels"].get(i, Counter())
            for t, p, _ in ys[y]["places"].get(i, ()):
                tabs.add(t)
                panels.add(p)
            desc = desc or ys[y]["desc"].get(i, "")
        label = lab.most_common(1)[0][0] if lab else ""
        present = [y for y in years if i in ys[y]["state"]]
        row = [i, origin(i), label, desc, ",".join(present), ";".join(sorted(tabs)), ";".join(sorted(panels))]
        for y in years:
            s = summary(ys[y], i)
            if s is None:
                row += [""] * len(YEAR_COLS)
                continue
            c, proj = s
            row += [str(len(ys[y]["places"][i])), str(sum(c.values())), str(c["executes"]), str(c["greyed"]),
                    str(c["not-runnable"]), str(c["not-drawn"]), str(c["skipped"]), str(c["not-a-command"]),
                    ";".join(proj), ";".join(sorted(ys[y]["tested"].get(i, ()))),
                    ";".join(sorted(ys[y]["opened"].get(i, ())))]
        out.append("\t".join(x.replace("\t", " ") for x in row))

        tab = sorted(tabs)[0] if tabs else "(no tab)"
        cells = []
        for y in years:
            s = summary(ys[y], i)
            cells.append("-" if s is None else "container" if set(s[0]) == {"not-a-command"}
                         else f"{s[0]['executes']}/{sum(s[0].values())}")
        newest = next((y for y in reversed(years) if summary(ys[y], i)), None)
        proj = ", ".join(summary(ys[newest], i)[1]) if newest else ""
        where = "; ".join(sorted(f"{t} › {p}" for t, p in {(t, p) for y in years for t, p, _ in ys[y]['places'].get(i, ())}))
        md_rows[tab].append(f"| `{i}` | {esc(label)} | {esc(desc)} | {esc(where)} | " + " | ".join(cells) +
                            f" | {esc(proj)} |")
    (CENSUS / "commands.tsv").write_text("\n".join(out) + "\n", encoding="utf-8", newline="\n")

    for y in years:
        ctxs = sorted(ys[y]["ctxs"], key=lambda c: (not c.startswith("project/"), c))
        lines = ["\t".join(["id"] + ctxs)]
        for i in sorted(ys[y]["state"]):
            st = ys[y]["state"][i]
            lines.append("\t".join([i] + [st.get(c, "") for c in ctxs]))
        (CENSUS / y / "matrix.tsv").write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")

    blank = sum(1 for i in ids if not any(ys[y]["desc"].get(i) for y in years))
    md = ["# Revit ribbon command index", "",
          "Every stock ribbon command the [Ribbon Tool Census](README.md) found, one row each, grouped by ribbon tab. "
          f"{len(ids)} ids across Revit {' and '.join(years)}"
          f" ({', '.join(f'{y}: {len([i for i in ids if i in ys[y]['state']])}' for y in years)}).", "",
          "Generated by `tools/census/index.py` from the committed tables; nothing here is measured again. "
          "The same data as plain text: [`commands.tsv`](commands.tsv) (one row per id, both years), "
          "`<year>/matrix.tsv` (one column per context), `<year>/full.tsv` (every place and context, every column).", "",
          "## How to read a row", "",
          "- **Id**: the command id (`RevitCommandId.Name`). `CustomCtrl_…` ids are Autodesk add-in buttons that ship with Revit.",
          "- **Label**: the English ribbon text. **What Revit calls it**: the one-line description Revit wrote beside the id in its own "
          f"journal when the census started the tool. Blank for {blank} of the {len(ids)} ids: the journal had none, or the tool was never started "
          "(containers, greyed and skipped ids).",
          "- **Ribbon place**: the tab › panel where Revit draws it (the panel's English title).",
          "- **Ran in** (" + ", ".join(years) + "): in how many contexts the id executed, out of how many it was found in "
          "(`3/9`); `-` means the id was not in that year's ribbon; `container` marks a split button, combo or gallery that "
          "holds other commands and is not one itself. An id that Revit greys out with nothing selected shows `0/9`: "
          "`<year>/matrix.tsv` says which. A context is a file kind and view (`project/FloorPlan`, "
          "`family/OST_Doors/OneLevelBased/FloorPlan`); the id's places in it are folded into one result, "
          "executes first.",
          f"- **Project views it runs in** (Revit {years[-1]}): the project view types where it executes with nothing selected.", "",
          "Revit " + "; ".join(f"{y} is build {ys[y]['build']}" for y in years) + ". English Revit, nothing selected, no contextual tabs. "
          "Third-party add-ins are not in this index. For the rules, columns and limits see the [census README](README.md).", ""]
    for tab in sorted(md_rows):
        md += [f"## {tab}", "", "| Id | Label | What Revit calls it | Ribbon place | " +
               " | ".join(f"ran in {y}" for y in years) + f" | Project views ({years[-1]}) |",
               "|---|---|---|---|" + "---|" * (len(years) + 1)]
        md += md_rows[tab]
        md.append("")
    (CENSUS / "COMMANDS.md").write_text("\n".join(md), encoding="utf-8", newline="\n")
    print(f"{len(ids)} ids; census/commands.tsv, census/COMMANDS.md, " + ", ".join(f"census/{y}/matrix.tsv" for y in years))


if __name__ == "__main__":
    sys.exit(main())
