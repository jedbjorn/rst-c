#!/usr/bin/env python3
"""census.py: the Ribbon Tool Census host runner (feature #22, spec #34, "Runner and recovery").

Drives the shared test VM only through `./sc vm` (run from the Planner's worktree, the rst_c_vm seat rules) and the
Windows MCP relay (127.0.0.1:18000, for Revit's start-up prompts). Python 3 stdlib only.

  census.py status                         ./sc vm status (run it before any lifecycle verb)
  census.py build                          build src/RST.Census for 2026 and 2027 into the staging folder
  census.py begin                          VM must be off and free: start it, bring the MCP relay up, probe the guest
  census.py prepare <year>                 stage the add-in and skip list, park RST.addin, install the census manifest
  census.py start <year>                   (re)start Revit on the console desktop, answer its prompts, wait for the add-in
  census.py survey <year>                  list every stock family template's category and placement -> plan
  census.py plan <year>                    write the context plan (project views + one family per category/placement)
  census.py ctx <year> <context|job>       open the context's scratch document and view, run it with the watchdog
  census.py all <year> [--until HH:MM]     every planned context not finished yet, in order (resumable)
  census.py pull <year>                    collect on the guest, pull, unpack into shared/evidence/census/<year>/
  census.py end                            MCP down, pull every year, then ./sc vm reset --off
  census.py go <year> <line>...            write raw verbs to the add-in (debugging)

Every state the runner needs survives in files: the guest's C:\\RstC\\census\\<year>\\ (rows.tsv, progress.txt) and the
host's state dir (plan, skip list, run log), so a year can span several VM sessions: `pull` before `end`, and the next
session's `prepare` pushes the pulled rows back (resume).
"""

import json
import os
import re
import shutil
import subprocess
import sys
import time
import urllib.request
import zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
PLN1 = Path("/home/jedi/Repos/rst-c/.sc-worktrees/pln1")
STATE = Path("/home/jedi/Repos/rst-c/.sc-state/local/vmwork/census")
EVIDENCE = Path("/home/jedi/Repos/rst-c/shared/evidence/census")
DOTNET = os.environ.get("CENSUS_DOTNET", "/tmp/dotnet10/dotnet")
MCP_URL = "http://127.0.0.1:18000/mcp"
G = "C:\\RstC"                      # guest workspace (rst-c's own)
GC = G + "\\census"
YEARS = ("2026", "2027")
PROJECT_VIEWS = ["FloorPlan", "CeilingPlan", "ThreeD", "Section", "Elevation", "DrawingSheet", "DraftingView", "Legend", "Schedule"]
HANG_S = 300          # no progress line for this long while tools run -> hang
SWEEP_HANG_S = 900    # before the first tool (the not-drawn sweep)


def log(msg):
    line = f"{time.strftime('%Y-%m-%d %H:%M:%S')} {msg}"
    print(line, flush=True)
    STATE.mkdir(parents=True, exist_ok=True)
    with open(STATE / "runner.log", "a", encoding="utf-8") as f:
        f.write(line + "\n")


# ---------------- ./sc vm ----------------
NOISE = re.compile(r"WARN|quantum|vulnera|upgraded|^stderr:|^stdout:|Guest command exited 0")


def vm(*args, timeout=150, quiet=False):
    try:
        p = subprocess.run(["./sc", "vm", *args], cwd=PLN1, capture_output=True, text=True, timeout=timeout)
        out = (p.stdout or "") + (p.stderr or "")
    except subprocess.TimeoutExpired as e:
        out = f"(host timeout after {timeout}s) " + str(e.stdout or "")
    out = "\n".join(l for l in out.splitlines() if not NOISE.search(l))
    if not quiet:
        log(f"vm {' '.join(args)[:160]} -> {out.strip()[-600:]}")
    return out


def gx(*cmd, timeout=150, quiet=True):
    return vm("exec", "--", *cmd, timeout=timeout, quiet=quiet)


def ps(script, *args, timeout=150, quiet=True):
    return gx("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", f"{G}\\census-tools\\{script}", *args, timeout=timeout, quiet=quiet)


def push(local, remote):
    return vm("push", str(local), remote, quiet=True)


def go(year, *lines):
    """Writes the add-in's trigger file. The add-in reads and deletes it every 2 s; a second write before that would
    replace the first, so wait until the previous trigger is consumed."""
    for _ in range(15):
        if "pending" not in gx("cmd", "/c", f"if exist {GC}\\go echo pending"):
            break
        time.sleep(2)
    STATE.mkdir(parents=True, exist_ok=True)
    tmp = STATE / "go.txt"
    tmp.write_text("\r\n".join(lines) + "\r\n", encoding="utf-8")
    push(tmp, f"{G}\\go.tmp")
    gx("cmd", "/c", f"move /y {G}\\go.tmp {GC}\\go")
    log(f"go {year}: {' ; '.join(lines)}")


def status_text():
    return vm("status", quiet=True)


# ---------------- Windows MCP ----------------
_sid = None


def _mcp_post(body, sid=None):
    req = urllib.request.Request(MCP_URL, data=json.dumps(body).encode(), method="POST")
    req.add_header("Content-Type", "application/json")
    req.add_header("Accept", "application/json, text/event-stream")
    if sid:
        req.add_header("Mcp-Session-Id", sid)
    with urllib.request.urlopen(req, timeout=200) as r:
        raw, ct, sid2 = r.read(), r.headers.get("Content-Type", ""), r.headers.get("Mcp-Session-Id")
    msg = None
    if raw:
        if "event-stream" in ct:
            for line in raw.splitlines():
                if line.startswith(b"data:"):
                    try:
                        v = json.loads(line[5:].strip())
                    except ValueError:
                        continue
                    if isinstance(v, dict) and "id" in v:
                        msg = v
        else:
            msg = json.loads(raw)
    return msg, sid2


def mcp_ps(command, timeout=150):
    """Runs PowerShell in the console session through the Windows MCP PowerShell tool."""
    global _sid
    if _sid is None:
        _, _sid = _mcp_post({"jsonrpc": "2.0", "id": 1, "method": "initialize",
                             "params": {"protocolVersion": "2025-03-26", "capabilities": {}, "clientInfo": {"name": "census", "version": "1"}}})
        _mcp_post({"jsonrpc": "2.0", "method": "notifications/initialized"}, _sid)
    m, _ = _mcp_post({"jsonrpc": "2.0", "id": 2, "method": "tools/call",
                      "params": {"name": "PowerShell", "arguments": {"command": command, "timeout": timeout}}}, _sid)
    if not m or "error" in m:
        return f"MCP error {m}"
    return "\n".join(c.get("text", "") for c in m["result"].get("content", []) if c.get("type") == "text")


# ---------------- steps ----------------
def stage_dir(year):
    return STATE / "stage" / year


def cmd_build():
    for year, cfg in (("2026", "Release R26"), ("2027", "Release R27")):
        p = subprocess.run([DOTNET, "build", str(REPO / "src/RST.Census/RST.Census.csproj"), "-c", cfg, "--nologo", "-v", "q"],
                           capture_output=True, text=True, env={**os.environ, "DOTNET_CLI_TELEMETRY_OPTOUT": "1"})
        tfm = "net8.0-windows" if year == "2026" else "net10.0-windows"
        dll = REPO / "src/RST.Census/bin" / cfg / tfm / "RST.Census.dll"
        if p.returncode != 0 or not dll.exists():
            print(p.stdout[-3000:], p.stderr[-2000:])
            sys.exit(f"build {year} failed")
        stage_dir(year).mkdir(parents=True, exist_ok=True)
        shutil.copy2(dll, stage_dir(year) / "RST.Census.dll")
        log(f"built {year}: {dll}")


def check_free():
    s = status_text()
    log("status: " + " | ".join(l.strip() for l in s.splitlines() if l.strip())[:400])
    return s


def cmd_begin():
    s = check_free()
    if "running" in s.lower() and "powered_off" not in s.lower() and "shut off" not in s.lower():
        sys.exit("VM is not powered off: stop and report (rst_c_vm seat rules)")
    vm("start", timeout=600)
    for _ in range(40):
        if "Windows" in gx("cmd", "/c", "ver", timeout=60):
            break
        time.sleep(10)
    vm("mcp", "up", timeout=300)
    gx("cmd", "/c", f"mkdir {G}\\census-tools {G}\\stage\\2026 {G}\\stage\\2027 {GC} 2>nul")
    for f in sorted((HERE / "guest").glob("*.ps1")):
        push(f, f"{G}\\census-tools\\{f.name}")
    probe = ps("probe.ps1", timeout=120)
    log("guest probe:\n" + probe)
    (STATE / "guest-probe.txt").write_text(probe, encoding="utf-8")


def cmd_prepare(year):
    # Revit locks the add-in DLL: stop it before installing a new build
    log(ps("rv.ps1", "-Action", "stop", "-Year", year))
    for f in sorted((HERE / "guest").glob("*.ps1")):
        push(f, f"{G}\\census-tools\\{f.name}")
    push(stage_dir(year) / "RST.Census.dll", f"{G}\\stage\\{year}\\RST.Census.dll")
    push(HERE / "skip.tsv", f"{G}\\stage\\skip.tsv")
    out = ps("setup.ps1", "-Year", year, timeout=120)
    log("setup:\n" + out)
    # resume across VM sessions: push back what earlier sessions produced
    prev = EVIDENCE / year / "census"
    if (prev / "rows.tsv").exists():
        gx("cmd", "/c", f"mkdir {GC}\\{year} 2>nul")
        for name in ("rows.tsv", "progress.txt", "detail.log"):
            if (prev / name).exists():
                push(prev / name, f"{GC}\\{year}\\{name}")
        log(f"resume: pushed earlier rows for {year}")


def revit_status(year):
    return ps("status.ps1", "-Year", year, timeout=90)


def started_count(year):
    out = gx("cmd", "/c", f"findstr /c:\"started in Revit\" {GC}\\{year}\\actions.log 2>nul")
    return sum(1 for l in out.splitlines() if "started in Revit" in l)


def cmd_start(year, tries=2):
    for attempt in range(tries):
        log(ps("rv.ps1", "-Action", "stop", "-Year", year))
        before = started_count(year)
        log(ps("rv.ps1", "-Action", "launch", "-Year", year))
        ans = mcp_ps(f"& {G}\\census-tools\\answer.ps1 -Seconds 150 -Year {year}", timeout=175)
        log("answer: " + ans.strip()[-500:])
        for _ in range(12):
            if started_count(year) > before and "REVIT running" in revit_status(year):
                log("Revit up with the census add-in")
                return True
            time.sleep(10)
        log(f"Revit start attempt {attempt + 1} did not show the census add-in")
    return False


def plan_path(year):
    return STATE / f"plan-{year}.tsv"


def cmd_survey(year):
    go(year, f"survey|s{int(time.time())}")
    for _ in range(60):
        time.sleep(10)
        out = gx("cmd", "/c", f"type {GC}\\{year}\\templates.tsv 2>nul")
        if out.count("\n") > 5 and "template\t" in out:
            acts = gx("cmd", "/c", f"type {GC}\\{year}\\actions.log | findstr /c:survey")
            if "OK survey" in acts or "FAIL survey" in acts:
                break
    vm("pull", f"{GC}\\{year}\\templates.tsv", str(STATE / f"templates-{year}.tsv"), quiet=True)
    log(f"survey {year}: {sum(1 for _ in open(STATE / f'templates-{year}.tsv', encoding='utf-8'))} lines")


def cmd_plan(year):
    """Jobs: one project document for every project view, then one family per (category, placement type)."""
    tpl = STATE / f"templates-{year}.tsv"
    rows = []
    rte = f"C:\\ProgramData\\Autodesk\\RVT {year}\\Templates\\English-Imperial\\default.rte"
    for v in PROJECT_VIEWS:
        rows.append(("project/" + v, "project", rte, v, ""))
    groups = {}
    if tpl.exists():
        for line in tpl.read_text(encoding="utf-8").splitlines()[1:]:
            f = line.split("\t")
            if len(f) < 7 or f[6].strip() or not f[1] or f[1].startswith("ERR"):
                continue
            key = f"family/{f[1]}/{f[2]}"
            groups.setdefault(key, []).append((f[0], f[3]))
    # the WU1d oracle family first (AC3), then the model families, then annotation-like (view-based) ones
    order = sorted(groups, key=lambda k: (k != "family/OST_GenericModel/OneLevelBased", k.endswith("/ViewBased"), k))
    for key in order:
        members = groups[key]
        # prefer an English-Imperial template (the project is imperial), then the shortest path
        members.sort(key=lambda m: ("English-Imperial" not in m[0], len(m[0]), m[0].lower()))
        first, types = members[0]
        others = ";".join(m[0] for m in members)
        rows.append((key + "/Default", "family", first, "Default", others))
        if "ThreeD" in types.split(","):
            rows.append((key + "/ThreeD", "family", first, "ThreeD", others))
    with open(plan_path(year), "w", encoding="utf-8") as f:
        f.write("job\tkind\ttemplate\tview\ttemplates\n")
        for r in rows:
            f.write("\t".join(r) + "\n")
    log(f"plan {year}: {len(rows)} jobs ({len(groups)} family groups)")


def read_plan(year):
    lines = plan_path(year).read_text(encoding="utf-8").splitlines()[1:]
    return [dict(zip(("job", "kind", "template", "view", "templates"), l.split("\t"))) for l in lines if l.strip()]


def guest_state(year):
    tag = f"st{int(time.time() * 1000) % 10000000}"
    go(year, f"state|{tag}")
    for _ in range(12):
        time.sleep(3)
        out = gx("cmd", "/c", f"type {GC}\\{year}\\state\\{tag}.txt 2>nul")
        if "active=" in out:
            m = {}
            for part in re.search(r"active=.*", out).group(0).split("\t"):
                if "=" in part:
                    k, v = part.split("=", 1)
                    m[k] = v
            m["raw"] = out
            return m
    return {}


def wait_action(year, needle, timeout=120):
    t0 = time.time()
    while time.time() - t0 < timeout:
        time.sleep(4)
        out = gx("cmd", "/c", f"type {GC}\\{year}\\actions.log | findstr /c:\"{needle}\"")
        lines = [l for l in out.splitlines() if needle in l]
        if lines:
            return lines[-1]
    return ""


def ensure_context(year, job):
    """Opens the job's scratch document and view; returns the context key it gives, or ''."""
    st = guest_state(year)
    want_doc = job["kind"]
    cur = st.get("context", "")
    same_doc = False
    if want_doc == "project":
        same_doc = cur.startswith("project/")
    else:
        same_doc = cur.startswith(job["job"].rsplit("/", 1)[0] + "/")
    if not same_doc:
        if cur.startswith("family/"):
            tag = f"c{int(time.time())}"
            go(year, f"closedoc|{tag}")
            wait_action(year, f"closedoc done {tag}", 90)
            st = guest_state(year)
            cur = st.get("context", "")
        if not (want_doc == "project" and cur.startswith("project/")):
            tag = f"n{int(time.time())}"
            go(year, f"newdoc|{tag}|{want_doc}|{job['template']}")
            wait_action(year, f"newdoc done {tag}", 150)
            time.sleep(5)
    if job["view"] != "Default":
        tag = f"v{int(time.time())}"
        go(year, f"view|{tag}|{job['view']}")
        line = wait_action(year, f"view done {tag}", 150)
        log("view: " + line)
    st = guest_state(year)
    log(f"context now {st.get('context')} doc={st.get('active')} view={st.get('view')} elements={st.get('elements')} modified={st.get('IsModified')}")
    ctx = st.get("context", "")
    if job["kind"] == "project" and ctx != job["job"]:
        return ""
    if job["kind"] == "family" and not ctx.startswith(job["job"].rsplit("/", 1)[0] + "/"):
        return ""
    return ctx


def add_skip(year, cid, reason):
    p = HERE / "skip.tsv"
    with open(p, "a", encoding="utf-8") as f:
        f.write(f"{year}\t{cid}\t{reason}\n")
    push(p, f"{GC}\\skip.tsv")
    log(f"skip list += {year} {cid}: {reason}")


def journal_tail_line(year):
    out = ps("status.ps1", "-Year", year)
    return out


def recover(year, why):
    """Revit crashed or hung: put the current tool on the skip list, keep evidence, restart Revit."""
    cur = gx("cmd", "/c", f"type {GC}\\current.txt 2>nul").strip()
    f = cur.split("\t")
    cid = f[3].strip() if len(f) >= 4 else ""
    ctx = f[2].strip() if len(f) >= 4 else ""
    jl = gx("powershell", "-NoProfile", "-Command",
            f"$j = Get-ChildItem \"$env:LOCALAPPDATA\\Autodesk\\Revit\\Autodesk Revit {year}\\Journals\\journal.*.txt\" | sort LastWriteTime | select -Last 1; "
            "Get-Content $j.FullName -Tail 400 | Select-String -Pattern 'DBG_WARN|DBG_INFO|Exception|serious|error' | select -Last 2 | % { $_.Line.Trim() }")
    jl = " ".join(jl.split())[:200]
    log(f"RECOVER ({why}): context={ctx} id={cid} journal: {jl}")
    if cid:
        add_skip(year, cid, f"{why} in {ctx} ({time.strftime('%Y-%m-%d')}): {jl}" if jl else f"{why} in {ctx} ({time.strftime('%Y-%m-%d')})")
    gx("powershell", "-NoProfile", "-Command",
       f"$d = '{GC}\\{year}\\crash-{int(time.time())}'; New-Item -ItemType Directory -Force $d | Out-Null; "
       f"Copy-Item (Get-ChildItem \"$env:LOCALAPPDATA\\Autodesk\\Revit\\Autodesk Revit {year}\\Journals\\journal.*.txt\" | sort LastWriteTime | select -Last 1).FullName $d; "
       f"Copy-Item {GC}\\current.txt $d -ea 0; Remove-Item {GC}\\current.txt -ea 0")
    return cmd_start(year)


def end_count(year, ctx):
    out = gx("cmd", "/c", f"findstr /c:\" END {ctx} \" {GC}\\{year}\\progress.txt 2>nul")
    return sum(1 for l in out.splitlines() if f" END {ctx} " in l)


def run_context(year, job, deadline=None):
    """One planned job: open it, run it, watch it; restart and resume after a crash or a hang."""
    restarts = 0
    while restarts < 6:
        if deadline and time.time() > deadline:
            log("deadline reached before the context started")
            return "deadline"
        ctx = ensure_context(year, job)
        if not ctx:
            log(f"could not open {job['job']}")
            return "noctx"
        ends_before = end_count(year, ctx)
        go(year, f"run|{ctx}")
        started = time.time()
        last_age_ok = time.time()
        seen_drawn = False
        while True:
            time.sleep(20)
            st = revit_status(year)
            prog = [l[2:] for l in st.splitlines() if l.startswith("P ")]
            if any(f"END {ctx} " in l for l in prog) and end_count(year, ctx) > ends_before:
                log(f"done {ctx}: {prog[-1] if prog else ''}")
                return ctx
            acts = [l for l in st.splitlines() if l.startswith("A ")]
            if acts and f"FAIL run|{ctx}" in acts[-1]:
                # the add-in refused the run (an earlier run still active): try again once it is idle
                log(f"run|{ctx} refused; retrying in 30 s")
                time.sleep(30)
                go(year, f"run|{ctx}")
                continue
            if any(f"CONTEXT MISMATCH" in l for l in prog[-2:]):
                log(f"mismatch for {ctx}: {prog[-1]}")
                return "mismatch"
            if any(f"DRAWN {ctx}" in l for l in prog):
                seen_drawn = True
            m = re.search(r"PROGRESS_AGE (-?\d+)", st)
            age = int(m.group(1)) if m else -1
            if "REVIT none" in st or "CRASHREPORTER" in st:
                log("Revit is gone (crash): " + " | ".join(st.splitlines()[:6]))
                gx("powershell", "-NoProfile", "-Command", "Get-Process cer_dialog, senddmp -ea 0 | Stop-Process -Force")
                restarts += 1
                if not recover(year, "crash"):
                    return "restart-failed"
                break
            limit = HANG_S if seen_drawn else SWEEP_HANG_S
            if age > limit:
                log(f"no progress for {age}s (hang): " + " | ".join(st.splitlines()[:6]))
                restarts += 1
                if not recover(year, "hang"):
                    return "restart-failed"
                break
            if deadline and time.time() > deadline:
                go(year, "stop")
                log("deadline: stop sent; waiting for the current tool")
                for _ in range(12):
                    time.sleep(10)
                    if "STOPPED" in revit_status(year) or "END " in revit_status(year):
                        break
                return "deadline"
    return "too-many-restarts"


def done_contexts(year):
    """Contexts whose run ended without a stop or an abort (a resumed context ends again when it finishes)."""
    out = gx("cmd", "/c", f"findstr /c:\" END \" {GC}\\{year}\\progress.txt 2>nul")
    done = set()
    for line in out.splitlines():
        m = re.search(r" END (\S+) tools=", line)
        if m and "(stopped)" not in line and "(incomplete)" not in line:
            done.add(m.group(1))
    return done


def cmd_all(year, until=None):
    deadline = None
    if until:
        hh, mm = map(int, until.split(":"))
        t = time.localtime()
        deadline = time.mktime((t.tm_year, t.tm_mon, t.tm_mday, hh, mm, 0, 0, 0, -1))
        if deadline < time.time():
            deadline += 86400
    plan = read_plan(year)
    finished = done_contexts(year)
    log(f"all {year}: {len(plan)} jobs, finished already: {len(finished)}")
    for job in plan:
        jkey = job["job"]
        if any(f == jkey or (jkey.endswith("/Default") and f.startswith(jkey.rsplit("/", 1)[0] + "/") and not f.endswith("/ThreeD")) for f in finished):
            continue
        r = run_context(year, job, deadline)
        log(f"job {jkey} -> {r}")
        with open(STATE / f"jobs-{year}.tsv", "a", encoding="utf-8") as f:
            f.write(f"{time.strftime('%Y-%m-%d %H:%M:%S')}\t{jkey}\t{job['template']}\t{job['templates']}\t{r}\n")
        if r in ("deadline", "restart-failed", "too-many-restarts"):
            return r
    return "complete"


def cmd_pull(year):
    out = ps("collect.ps1", "-Year", year, timeout=150)
    log("collect: " + out.strip())
    dest = EVIDENCE / year
    dest.mkdir(parents=True, exist_ok=True)
    z = dest / f"collect-{time.strftime('%Y%m%d-%H%M%S')}.zip"
    vm("pull", f"{G}\\collect-{year}.zip", str(z), timeout=300)
    if not z.exists():
        log("pull failed")
        return
    with zipfile.ZipFile(z) as zf:
        for n in zf.namelist():
            if n.endswith("\\") or n.endswith("/"):
                continue
            target = dest / n.replace("\\", "/")
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(zf.read(n))
    for name in ("plan-%s.tsv" % year, "templates-%s.tsv" % year, "jobs-%s.tsv" % year, "runner.log", "guest-probe.txt"):
        if (STATE / name).exists():
            shutil.copy2(STATE / name, dest / name)
    log(f"pulled {year} into {dest}")


def cmd_dropctx(year, ctx):
    """Forgets a context's rows (guest and pulled copy) so it runs again from scratch; Revit must not be running it."""
    for path in (EVIDENCE / year / "census" / "rows.tsv",):
        if path.exists():
            lines = path.read_text(encoding="utf-8-sig").splitlines()
            keep = [lines[0]] + [l for l in lines[1:] if l.split("\t")[2:3] != [ctx]]
            path.write_text("\r\n".join(keep) + "\r\n", encoding="utf-8")
            log(f"dropctx {year} {ctx}: {len(lines) - len(keep)} rows dropped from {path}")
            push(path, f"{GC}\\{year}\\rows.tsv")
    with open(STATE / f"dropped-{year}.txt", "a", encoding="utf-8") as f:
        f.write(f"{time.strftime('%Y-%m-%d %H:%M:%S')}\t{ctx}\n")


def cmd_end():
    for y in YEARS:
        if "census" in gx("cmd", "/c", f"dir /b {GC}\\{y} 2>nul") or gx("cmd", "/c", f"if exist {GC}\\{y}\\progress.txt echo yes").strip() == "yes":
            cmd_pull(y)
    vm("mcp", "down", timeout=120)
    vm("reset", "--off", timeout=900)
    log("end: " + status_text().strip()[:300])


def main(argv):
    if not argv:
        print(__doc__)
        return
    c, a = argv[0], argv[1:]
    if c == "status":
        print(status_text())
    elif c == "build":
        cmd_build()
    elif c == "begin":
        cmd_begin()
    elif c == "prepare":
        cmd_prepare(a[0])
    elif c == "start":
        print("ok" if cmd_start(a[0]) else "FAILED")
    elif c == "survey":
        cmd_survey(a[0])
    elif c == "plan":
        cmd_plan(a[0])
    elif c == "ctx":
        job = next((j for j in read_plan(a[0]) if j["job"] == a[1]), None)
        print(run_context(a[0], job) if job else "no such job")
    elif c == "all":
        until = a[a.index("--until") + 1] if "--until" in a else None
        print(cmd_all(a[0], until))
    elif c == "pull":
        cmd_pull(a[0])
    elif c == "dropctx":
        cmd_dropctx(a[0], a[1])
    elif c == "end":
        cmd_end()
    elif c == "go":
        go(a[0], *a[1:])
    elif c == "rstatus":
        print(revit_status(a[0]))
    elif c == "mcp":
        print(mcp_ps(a[0]))
    else:
        print(__doc__)


if __name__ == "__main__":
    main(sys.argv[1:])
