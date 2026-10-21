#!/usr/bin/env python3
"""Summarize library_load.py runs: per point the action's wall time, per request the time to headers with the server's
Server-Timing split (app / sql / sqln), and - from the diagnostics `perf` snapshots taken before and after the run - the
server-side window: requests per route, the SQL statements that cost the most time (shapes only), BEGIN / COMMIT waits and
the heap. Counts and timings only.

  summarize_library.py run1.json [run2.json ...] [--statements 15] [--points browse_root,search_plain] [--compare other.json]
"""
from __future__ import annotations

import argparse
import json


def rows_by_key(snap, field):
    return {(r["key"], r.get("shape")): r for r in (snap or {}).get("perf", {}).get(field, []) or []}


def delta(before, after, field):
    b, a = rows_by_key(before, field), rows_by_key(after, field)
    out = []
    for k, r in a.items():
        p = b.get(k, {})
        d = {f: r.get(f, 0) - p.get(f, 0) for f in ("count", "msSum", "rows", "sqlMsSum", "sqlCount", "lockWaitMsSum")}
        d["msMax"] = r.get("msMax")
        if d["count"] or d["msSum"]:
            out.append((k, d))
    return out


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("runs", nargs="+")
    ap.add_argument("--statements", type=int, default=12)
    ap.add_argument("--points", default="")
    ap.add_argument("--routes", action="store_true", help="print every request route of each point")
    args = ap.parse_args()
    want = set(args.points.split(",")) if args.points else None
    for path in args.runs:
        run = json.load(open(path))
        print(f"\n=== {run['label']}  rounds={run['rounds']}  {run['seconds']:.0f}s  loadavg_end={run.get('loadavg_end')}")
        print(f"{'point':28s} {'n':>3s} {'p50':>8s} {'p95':>8s} {'max':>8s}  slowest request (ttfb p50 / app / sql / sqln)")
        for name, s in run["points"].items():
            if want and name not in want:
                continue
            w = s["wall"]
            reqs = sorted(s["requests"].items(), key=lambda kv: -(kv[1].get("ttfb_p50") or 0))
            top = reqs[0] if reqs else None
            desc = ""
            if top:
                r = top[1]
                desc = (f"{top[0][:58]} {r.get('ttfb_p50')} / {r.get('st_app_p50')} / {r.get('st_sql_p50')} / {r.get('st_sqln_p50')}")
            print(f"{name:28s} {s['n']:3d} {w['p50'] or 0:8.1f} {w['p95'] or 0:8.1f} {w['max'] or 0:8.1f}  {desc}")
            if args.routes:
                for route, r in reqs:
                    print(f"      {route[:70]:70s} n={r['n']:3d} ttfb p50 {r.get('ttfb_p50')} p95 {r.get('ttfb_p95')} max {r.get('ttfb_max'):.0f}"
                          f" app {r.get('st_app_p50')} sql {r.get('st_sql_p50')} sqln {r.get('st_sqln_p50')} lockw {r.get('st_lockw_p50')}"
                          f" status {r['status']}")
        b, a = run.get("diagnostics_before"), run.get("diagnostics_after")
        if not (b and a and a.get("perf")):
            continue
        print("  -- server window: background SQL")
        st = delta(b, a, "statements")
        bg = [x for x in st if x[0][0] in ("(background)", "(after a request)")]
        tot_bg = sum(d["msSum"] for _, d in bg)
        print(f"     background statements: {sum(d['count'] for _, d in bg)} commands, {tot_bg:.0f} ms SQL")
        for (k, shape), d in sorted(bg, key=lambda x: -x[1]["msSum"])[: args.statements]:
            print(f"     {d['msSum']:9.0f} ms {d['count']:7d}x max {d['msMax']:7.1f}  {shape[:150]}")
        print("  -- server window: request SQL (top statements)")
        rq = [x for x in st if x[0][0] not in ("(background)", "(after a request)")]
        for (k, shape), d in sorted(rq, key=lambda x: -x[1]["msSum"])[: args.statements]:
            print(f"     {d['msSum']:9.0f} ms {d['count']:7d}x rows {d['rows']:8d}  {k[:48]:48s} {shape[:110]}")
        print("  -- transactions (begin = write-lock wait)")
        for (k, _), d in sorted(delta(b, a, "transactions")):
            avg = d["msSum"] / d["count"] if d["count"] else 0
            print(f"     {k:22s} n={d['count']:7d} sum {d['msSum']:9.0f} ms avg {avg:6.1f} max {d['msMax']}")
        hb, ha = b["perf"]["heap"], a["perf"]["heap"]
        print(f"  -- heap: {hb['heapSizeBytes'] / 2**20:.0f} -> {ha['heapSizeBytes'] / 2**20:.0f} MiB, working set "
              f"{ha['workingSetBytes'] / 2**20:.0f} MiB, allocated {(ha['totalAllocatedBytes'] - hb['totalAllocatedBytes']) / 2**20:.0f} MiB, "
              f"gen2 +{ha['gen2Collections'] - hb['gen2Collections']}, pause {ha['totalPauseMs'] - hb['totalPauseMs']:.0f} ms; "
              f"threadpool threads {a['perf']['threadPool']['threads']} pending {a['perf']['threadPool']['pendingWorkItems']}")
        m = a.get("media") or {}
        pool = m.get("pool") or {}
        if pool:
            print(f"  -- media pool: {json.dumps(pool)[:200]}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
