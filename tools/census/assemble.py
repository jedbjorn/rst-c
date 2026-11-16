#!/usr/bin/env python3
"""assemble.py <year> [--allow-errors]: turn a year's census run into the committed tables (spec #34, "Index files").

Reads what `census.py pull` unpacked into shared/evidence/census/<year>/ (census/rows.tsv, census/summary/*.txt) and the
runner's plan, and writes census/<year>/full.tsv, exceptions.tsv and contexts.tsv:

  full.tsv        every place of every context; the latest row per (context, id, tab, panel, path) wins (a re-run
                  replaces an earlier one); the spec's columns only
  exceptions.tsv  accepted-nothing and refused -> not-runnable; not-drawn -> not-drawn; reason = the run's note/journal
  contexts.tsv    context key, file kind, category, placement type, view type, the view the run used, the template(s)

Refuses to write while any `error` row is left (AC1/AC2), unless --allow-errors (a draft). No machine details: template
paths are cut to their language folder ("English-Imperial/Generic Model.rft"). Python 3 stdlib only.
"""
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
EVIDENCE = Path("/home/jedi/Repos/rst-c/shared/evidence/census")
STATE = Path("/home/jedi/Repos/rst-c/.sc-state/local/vmwork/census")
FULL = ["year", "build", "context", "id", "tab", "panel", "path", "label", "result", "tested", "opened", "teardown", "left",
        "gate", "ms", "run_date", "census_build"]
EXC = ["context", "id", "tab", "panel", "path", "mark", "reason"]
CTX = ["context", "kind", "category", "placement", "view_type", "view", "templates"]


def short_template(p):
    p = p.replace("/", "\\")
    m = re.search(r"\\(English(?:-Imperial)?)\\(.+)$", p)
    return (m.group(1) + "/" + m.group(2).replace("\\", "/")) if m else p.rsplit("\\", 1)[-1]


def clean(s):
    return (s or "").replace("\t", " ").replace("\r", " ").replace("\n", " ").strip()


def main(argv):
    year = argv[0]
    allow = "--allow-errors" in argv
    src = EVIDENCE / year / "census"
    rows_path = src / "rows.tsv"
    lines = rows_path.read_text(encoding="utf-8-sig").splitlines()
    head = lines[0].split("\t")
    latest = {}
    order = []
    for line in lines[1:]:
        if not line.strip():
            continue
        f = line.split("\t")
        r = dict(zip(head, f + [""] * (len(head) - len(f))))
        k = (r["context"], r["id"], r["tab"], r["panel"], r["path"])
        if k not in latest:
            order.append(k)
        latest[k] = r
    # plan order for contexts: project views first, then families as run
    plan = {}
    pp = STATE / f"plan-{year}.tsv"
    if not pp.exists():
        pp = EVIDENCE / year / f"plan-{year}.tsv"
    if pp.exists():
        for l in pp.read_text(encoding="utf-8").splitlines()[1:]:
            f = l.split("\t")
            if len(f) >= 5:
                plan[f[0]] = f
    ctx_order = []
    for k in order:
        if k[0] not in ctx_order:
            ctx_order.append(k[0])
    ctx_order.sort(key=lambda c: (not c.startswith("project/"), c))
    errors = [latest[k] for k in order if latest[k]["result"] == "error"]
    if errors and not allow:
        for e in errors[:30]:
            print("error row:", e["context"], e["id"], e.get("note", ""))
        sys.exit(f"{len(errors)} error rows left: resolve them first (re-run, skip with a reason, or reclassify)")
    out = REPO / "census" / year
    out.mkdir(parents=True, exist_ok=True)
    with open(out / "full.tsv", "w", encoding="utf-8", newline="\n") as fo:
        fo.write("\t".join(FULL) + "\n")
        for c in ctx_order:
            for k in order:
                if k[0] == c:
                    fo.write("\t".join(clean(latest[k].get(col, "")) for col in FULL) + "\n")
    with open(out / "exceptions.tsv", "w", encoding="utf-8", newline="\n") as fo:
        fo.write("\t".join(EXC) + "\n")
        for c in ctx_order:
            for k in order:
                r = latest[k]
                if k[0] != c:
                    continue
                if r["result"] in ("accepted-nothing", "refused"):
                    mark = "not-runnable"
                elif r["result"] == "not-drawn":
                    mark = "not-drawn"
                else:
                    continue
                reason = clean(r.get("note", ""))
                j = clean(r.get("journal", ""))
                if mark == "not-runnable" and "cannot be invoked" in j and "cannot be invoked" not in reason:
                    reason = (reason + "; journal: cannot be invoked in this context").strip("; ")
                if r["result"] == "refused":
                    reason = reason or "PostCommand refused; no route A"
                fo.write("\t".join(clean(x) for x in (r["context"], r["id"], r["tab"], r["panel"], r["path"], mark, reason[:200])) + "\n")
    # contexts
    summaries = {}
    for s in (src / "summary").glob("*.txt") if (src / "summary").exists() else []:
        d = {}
        for l in s.read_text(encoding="utf-8").splitlines():
            if "\t" in l and not l.startswith(" "):
                a, b = l.split("\t", 1)
                d[a] = b
        if "context" in d:
            summaries[d["context"]] = d
    with open(out / "contexts.tsv", "w", encoding="utf-8", newline="\n") as fo:
        fo.write("\t".join(CTX) + "\n")
        for c in ctx_order:
            parts = c.split("/")
            kind = parts[0]
            cat, place, vt = (parts[1], parts[2], parts[3]) if kind == "family" else ("", "", parts[1])
            view = ""
            m = re.search(r"view '([^']*)'", summaries.get(c, {}).get("document", ""))
            if m:
                view = m.group(1)
            job = plan.get(c) or plan.get(c.rsplit("/", 1)[0] + "/Default" if kind == "family" else c)
            tpls = ""
            if job:
                tpls = ";".join(short_template(t) for t in (job[4] or job[2]).split(";") if t)
            elif kind == "project":
                tpls = "English-Imperial/default.rte"
            fo.write("\t".join((c, kind, cat, place, vt, clean(view), tpls)) + "\n")
    n_exc = sum(1 for _ in open(out / "exceptions.tsv", encoding="utf-8")) - 1
    print(f"{year}: {len(order)} places, {len(ctx_order)} contexts, {n_exc} exceptions, {len(errors)} errors -> {out}")


if __name__ == "__main__":
    main(sys.argv[1:])
