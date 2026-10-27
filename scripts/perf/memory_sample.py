#!/usr/bin/env python3
"""Memory sampler for a MangaPixer test instance you run yourself (standard library only; 1.49.0 lane M).

Every --interval seconds it records one JSON line with:

- `docker`: the container's memory use as `docker stats --no-stream` reports it (the cgroup's usage, page cache included);
- `cgroup`: the container's `memory.stat` lines `anon`, `file`, `shmem`, `kernel` (bytes) - anon is what the processes hold;
- `procs`: per process inside the container (`dotnet` = the server, the media workers by their command line): VmRSS, RssAnon,
  RssFile, RssShmem (KiB, from `/proc/<pid>/status`);
- `diag`: from the admin diagnostics snapshot (`GET /api/v1/operations/diagnostics`): the server's working set, and with
  `MangaPixer:Perf:RequestTiming=true` the GC heap block (`perf.heap`) - the gap between the server's RssAnon and the GC's
  committed bytes is native memory (SQLite page caches, glibc arenas, image buffers); any other top-level block the build adds
  (a measurement probe) is copied as is.

Counts and sizes only - no names, paths or titles. `docker exec` runs only `cat` / `ls` under /proc and /sys inside the named
container: use it ONLY on a container you created for measuring, never on an instance other people use.

  MP_PERF_PASSWORD=... memory_sample.py --base http://127.0.0.1:8097 --user admin --container my-measure \\
      --interval 60 --duration 3600 --out mem.jsonl [--label run1]

`--summary mem.jsonl` prints a table (one row per sample, MiB) from an earlier run instead of sampling.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from reader_load import Session  # noqa: E402

MIB = 1024 * 1024

# One shell round trip per sample: memory.stat, then "pid|cmdline|status lines" per process.
PROC_SCRIPT = r"""
cat /sys/fs/cgroup/memory.stat 2>/dev/null | grep -E '^(anon|file|shmem|kernel) '
for d in /proc/[0-9]*; do
  [ -r "$d/status" ] || continue
  c=$(tr '\0' ' ' < "$d/cmdline" 2>/dev/null | cut -c1-200)
  [ -n "$c" ] || continue
  s=$(grep -E '^(VmRSS|RssAnon|RssFile|RssShmem):' "$d/status" 2>/dev/null | tr -s ' \t' ' ' | tr '\n' ';')
  echo "P|${d#/proc/}|$c|$s"
done
"""


def to_bytes(text: str) -> int:
    """`docker stats` sizes ('512.3MiB', '1.2GiB', '900kB') to bytes."""
    text = text.strip()
    units = [("GiB", 1024**3), ("MiB", 1024**2), ("KiB", 1024), ("kB", 1000), ("MB", 1000**2), ("GB", 1000**3), ("B", 1)]
    for unit, factor in units:
        if text.endswith(unit):
            return int(float(text[: -len(unit)]) * factor)
    return int(float(text))


def kind_of(cmdline: str) -> str | None:
    if "MangaPixer.MediaWorker" in cmdline:
        return "worker"
    if "MangaPixer.Server" in cmdline:
        return "server"
    return None


def sample_container(container: str) -> dict:
    out: dict = {}
    try:
        stats = subprocess.run(["docker", "stats", "--no-stream", "--format", "{{.MemUsage}}", container],
                               capture_output=True, text=True, timeout=60).stdout.strip()
        out["docker"] = to_bytes(stats.split("/")[0]) if stats else None
    except (subprocess.SubprocessError, ValueError):
        out["docker"] = None
    try:
        text = subprocess.run(["docker", "exec", container, "sh", "-c", PROC_SCRIPT],
                              capture_output=True, text=True, timeout=60).stdout
    except subprocess.SubprocessError:
        text = ""
    cgroup, procs = {}, []
    for line in text.splitlines():
        if line.startswith("P|"):
            _, pid, cmd, status = line.split("|", 3)
            kind = kind_of(cmd)
            if kind is None:
                continue
            row = {"pid": int(pid), "kind": kind}
            for part in status.split(";"):
                if ":" in part:
                    k, v = part.split(":", 1)
                    row[k.strip()] = int(v.strip().split(" ")[0])
            procs.append(row)
        elif " " in line:
            k, v = line.split(" ", 1)
            cgroup[k] = int(v)
    out["cgroup"] = cgroup
    out["procs"] = procs
    return out


def sample_diagnostics(session: Session, conn) -> dict:
    try:
        snap = session.get_json(conn, "/api/v1/operations/diagnostics")
    except Exception as e:  # noqa: BLE001 - a missed sample is recorded, not fatal
        return {"error": type(e).__name__}
    diag = {"workingSet": snap.get("process", {}).get("workingSetBytes"),
            "threads": snap.get("process", {}).get("threadCount"),
            "uptime": snap.get("process", {}).get("uptimeSeconds")}
    perf = snap.get("perf") or {}
    if perf.get("heap"):
        diag["heap"] = perf["heap"]
    media = snap.get("media") or {}
    if "pool" in media:
        diag["pool"] = media["pool"]
    for key, value in snap.items():
        if key not in ("timestamp", "database", "process", "media", "perf"):
            diag[key] = value
    return diag


def summarize(path: str) -> None:
    rows = [json.loads(line) for line in open(path, encoding="utf-8") if line.strip()]
    print(f"{'t(min)':>6} {'docker':>7} {'anon':>6} {'file':>6} {'srvRSS':>7} {'srvAnon':>7} {'gcComm':>7} {'gcHeap':>7} "
          f"{'native':>7} {'workers':>8}")
    t0 = rows[0]["t"] if rows else 0
    for r in rows:
        procs = r.get("procs", [])
        srv = next((p for p in procs if p["kind"] == "server"), {})
        workers = [p for p in procs if p["kind"] == "worker"]
        heap = (r.get("diag") or {}).get("heap") or {}
        comm = heap.get("committedBytes")
        anon = srv.get("RssAnon")
        native = (anon * 1024 - comm) / MIB if anon is not None and comm is not None else None

        def mib(v, scale=1):
            return f"{v * scale / MIB:7.0f}" if isinstance(v, (int, float)) else f"{'-':>7}"
        print(f"{(r['t'] - t0) / 60:6.1f} {mib(r.get('docker'))} {mib(r.get('cgroup', {}).get('anon')):>6} "
              f"{mib(r.get('cgroup', {}).get('file')):>6} {mib(srv.get('VmRSS'), 1024)} {mib(anon, 1024)} {mib(comm)} "
              f"{mib(heap.get('heapSizeBytes'))} {native if native is None else f'{native:7.0f}':>7} "
              f"{len(workers)}:{sum(w.get('VmRSS', 0) for w in workers) / 1024:5.0f}")


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--summary", help="print a table from an earlier JSONL file and exit")
    ap.add_argument("--base")
    ap.add_argument("--user")
    ap.add_argument("--password-env", default="MP_PERF_PASSWORD")
    ap.add_argument("--container", help="the container YOU created for measuring")
    ap.add_argument("--interval", type=float, default=60)
    ap.add_argument("--duration", type=float, default=3600)
    ap.add_argument("--label", default="run")
    ap.add_argument("--out")
    args = ap.parse_args()
    if args.summary:
        summarize(args.summary)
        return
    if not (args.base and args.user and args.container and args.out):
        ap.error("--base, --user, --container and --out are required when sampling")
    session = Session(args.base)
    session.login(args.user, os.environ[args.password_env])
    conn = session.conn()
    end = time.time() + args.duration
    with open(args.out, "a", encoding="utf-8") as f:
        while True:
            started = time.time()
            row = {"t": round(started, 1), "label": args.label}
            row.update(sample_container(args.container))
            try:
                row["diag"] = sample_diagnostics(session, conn)
            except OSError:
                conn = session.conn()
                row["diag"] = {"error": "connection"}
            f.write(json.dumps(row, separators=(",", ":")) + "\n")
            f.flush()
            if started + args.interval > end:
                break
            time.sleep(max(0.0, args.interval - (time.time() - started)))


if __name__ == "__main__":
    main()
