#!/usr/bin/env python3
"""Side-by-side p50 / p95 of each point across library_load.py runs (one column per run). Timings only.

  compare_library.py q-idle.json l-facet.json l-meta.json [--p 95]
"""
import argparse
import json
import os

ap = argparse.ArgumentParser()
ap.add_argument("runs", nargs="+")
ap.add_argument("--p", default="p50", help="p50, p95 or max")
args = ap.parse_args()
key = args.p if args.p.startswith(("p", "m")) else "p" + args.p
runs = [json.load(open(r)) for r in args.runs]
points = list(runs[0]["points"].keys())
print(f"{'point (' + key + ' ms)':28s}" + "".join(f"{os.path.basename(r)[:-5][:13]:>14s}" for r in args.runs))
for p in points:
    cells = []
    for run in runs:
        s = run["points"].get(p)
        cells.append(f"{s['wall'][key]:14.1f}" if s and s["wall"][key] is not None else f"{'-':>14s}")
    print(f"{p:28s}" + "".join(cells))
print(f"{'loadavg at end':28s}" + "".join(f"{(r.get('loadavg_end') or ['?'])[0]:>14s}" for r in runs))
