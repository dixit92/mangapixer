#!/usr/bin/env python3
"""Aggregate reader_load.py summaries across repeated runs (standard library only).

    summarize_runs.py <run-dir> [--exclude RUN_ID ...] [--json out.json]

Reads every <run>.json in the folder (and <run>.load-before / .load-after when present: the 1-minute load average on
their second line) and prints, per scenario, the MEDIAN over runs of each run's own p50 / p95 with the spread
(min-max and inter-quartile range), never a single run. The server-side numbers come from the media metrics snapshot
the harness stores before and after a run (the difference of the cumulative buckets = only that run's window).
"""
from __future__ import annotations

import argparse
import glob
import json
import os
import statistics
import sys

BOUNDS = [1, 2, 5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000, 30000, 60000, 120000]


def bucket_pct(buckets: list[int], q: float, max_ms: float) -> float | None:
    n = sum(buckets)
    if n <= 0:
        return None
    rank, seen = -(-q * n // 1), 0
    for i, c in enumerate(buckets):
        seen += c
        if seen >= rank:
            return min(BOUNDS[i], max_ms) if i < len(BOUNDS) else max_ms
    return max_ms


def window(before: dict | None, after: dict | None) -> dict:
    """Per (name, kind): count, p50, p95 of the run window; counts: deltas."""
    if not after:
        return {}
    same = before is not None and before.get("since") == after.get("since")
    prev_t = {(t["name"], t["kind"]): t for t in (before or {}).get("timings", [])} if same else {}
    prev_c = {(c["name"], c["kind"]): c["value"] for c in (before or {}).get("counts", [])} if same else {}
    out = {}
    for t in after.get("timings", []):
        p = prev_t.get((t["name"], t["kind"]))
        b = [x - (p["bucketCounts"][i] if p else 0) for i, x in enumerate(t["bucketCounts"])]
        n = sum(b)
        if n <= 0:
            continue
        total = t["totalMs"] - (p["totalMs"] if p else 0)
        out[f"{t['name']}|{t['kind']}"] = {"n": n, "mean": round(total / n, 1), "p50": bucket_pct(b, 0.5, t["maxMs"]),
                                          "p95": bucket_pct(b, 0.95, t["maxMs"])}
    for c in after.get("counts", []):
        d = c["value"] - prev_c.get((c["name"], c["kind"]), 0)
        if d:
            out[f"{c['name']}|{c['kind']}"] = {"n": d}
    return out


def load1(path: str) -> float | None:
    try:
        with open(path) as f:
            f.readline()
            return float(f.readline().split()[0])
    except (OSError, ValueError, IndexError):
        return None


def spread(values: list[float]) -> dict | None:
    v = sorted(x for x in values if x is not None)
    if not v:
        return None
    q = statistics.quantiles(v, n=4, method="inclusive") if len(v) >= 2 else [v[0], v[0], v[0]]
    return {"runs": len(v), "median": round(statistics.median(v), 1), "iqr": [round(q[0], 1), round(q[2], 1)],
            "min": round(v[0], 1), "max": round(v[-1], 1)}


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("dir")
    ap.add_argument("--exclude", nargs="*", default=[])
    ap.add_argument("--json")
    args = ap.parse_args()
    runs = []
    for path in sorted(glob.glob(os.path.join(args.dir, "*.json"))):
        with open(path) as f:
            s = json.load(f)
        if "scenario" not in s or s.get("run") in args.exclude:
            continue
        base = path[:-5]
        s["_load1"] = [load1(base + ".load-before"), load1(base + ".load-after")]
        s["_window"] = window(s.get("media_before"), s.get("media_after"))
        runs.append(s)
    metrics = {
        "turn ready, later pages p50": lambda s: s["turn_ready_ms_later_pages"]["p50"],
        "turn ready, later pages p95": lambda s: s["turn_ready_ms_later_pages"]["p95"],
        "turns waiting > 100 ms": lambda s: s["turns_waiting_over_100ms"],
        "first page ready p50": lambda s: s["turn_ready_ms_first_page"]["p50"],
        "first page ready max": lambda s: s["turn_ready_ms_first_page"]["max"],
        "current page total p50": lambda s: s["current_total_ms"]["p50"],
        "current page total p95": lambda s: s["current_total_ms"]["p95"],
        "prefetch total p50": lambda s: s["prefetch_total_ms"]["p50"],
        "prefetch total p95": lambda s: s["prefetch_total_ms"]["p95"],
        "progress PUT p50": lambda s: s["progress_total_ms"]["p50"],
        "progress PUT p95": lambda s: s["progress_total_ms"]["p95"],
        "server slot wait (miss) p50": lambda s: s["server_slot_ms_miss"]["p50"],
        "server slot wait (miss) p95": lambda s: s["server_slot_ms_miss"]["p95"],
        "server worker (miss) p50": lambda s: s["server_worker_ms_miss"]["p50"],
        "server worker (miss) p95": lambda s: s["server_worker_ms_miss"]["p95"],
        "server encode p50": lambda s: s["server_encode_ms"]["p50"],
        "server open p50": lambda s: s["server_open_ms"]["p50"],
        "server read p50": lambda s: s["server_read_ms"]["p50"],
        "server db p50": lambda s: s["server_db_ms"]["p50"],
        "server db p95": lambda s: s["server_db_ms"]["p95"],
        "page requests": lambda s: s["page_requests"],
        "503": lambda s: s["status_counts"].get("503", 0),
        "499 / 0 (client)": lambda s: s["status_counts"].get("499", 0) + s["status_counts"].get("0", 0),
        "load1 before": lambda s: s["_load1"][0],
        "load1 after": lambda s: s["_load1"][1],
    }
    result: dict = {}
    for sc in sorted({s["scenario"] for s in runs}):
        group = [s for s in runs if s["scenario"] == sc]
        row = {"runs": [s["run"] for s in group]}
        for name, f in metrics.items():
            vals = []
            for s in group:
                try:
                    vals.append(f(s))
                except (KeyError, TypeError):
                    vals.append(None)
            row[name] = spread(vals)
        keys = sorted({k for s in group for k in s["_window"]})
        row["server window"] = {k: {m: spread([s["_window"].get(k, {}).get(m) for s in group]) for m in ("n", "p50", "p95")}
                                for k in keys}
        result[sc] = row
    if args.json:
        with open(args.json, "w") as f:
            json.dump(result, f, indent=1)
    for sc, row in result.items():
        print(f"== {sc} ({len(row['runs'])} runs)")
        for name in metrics:
            sp = row[name]
            if sp:
                print(f"  {name:32s} median {sp['median']:>9}  IQR {sp['iqr']}  min-max {sp['min']}-{sp['max']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
