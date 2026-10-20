#!/usr/bin/env python3
"""Library browsing / search / info / home / reader-open timing for a MangaPixer test instance (standard library only).

Each "point" is one user action as the web app performs it (the same requests, sent concurrently on up to 6 connections like a
browser): library root, a big folder, filters, the Volumes view, a later page, search (plain / read filter / tag filter), the
Info panel, the series page, home, opening the reader (manifest + first page at both image-quality settings), and the return
from the reader (the 1.45.1 ring refresh). Points are run round-robin (interleaved) N times; every request's time to headers /
last byte, status and the server's Server-Timing fields (app, sql, sqln, lockw with MangaPixer:Perf:RequestTiming on) are
recorded, and the action's wall time.

Targets (which library, folder, series, item) are discovered once (`discover`) and stored as ids only in a targets file; text
needed at run time (a search word) is derived again from the instance at run time and never written or printed.

  MP_PERF_PASSWORD=... library_load.py discover --base URL --user perf-admin --targets t.json
  MP_PERF_PASSWORD=... library_load.py run --base URL --user perf-reader --targets t.json --n 20 --label idle \
      --out idle.json [--points browse_root,search_plain] [--diagnostics-user perf-admin --diagnostics-password-env VAR]

Output (JSON): per point p50 / p95 / max of the action's wall time and of each request (by route shape, ids replaced), with
the Server-Timing medians; optionally the admin diagnostics `perf` / `media` blocks before and after. Counts and timings only.
Use it ONLY against an instance you run yourself.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import threading
import time
import urllib.parse
from concurrent.futures import ThreadPoolExecutor

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from reader_load import Session, parse_server_timing, pct  # noqa: E402

API = "/api/v1"
ID_SEGMENT = re.compile(r"/(?=[0-9a-z]*[0-9])[0-9a-z]{4,}(?=/|$)")


def route_shape(path: str) -> str:
    """The request path with ids replaced and the query reduced to its parameter names (no values)."""
    p, _, q = path.partition("?")
    p = ID_SEGMENT.sub("/{id}", p[len(API):] if p.startswith(API) else p)
    names = sorted({kv.split("=")[0] for kv in q.split("&") if kv})
    return p + ("?" + "&".join(names) if names else "")


class Client:
    def __init__(self, base: str, user: str, password: str, connections: int = 6):
        self.s = Session(base)
        self.s.login(user, password)
        self.local = threading.local()
        self.pool = ThreadPoolExecutor(max_workers=connections)

    def _conn(self):
        c = getattr(self.local, "conn", None)
        if c is None:
            c = self.local.conn = self.s.conn()
        return c

    def call(self, method: str, path: str, body=None, headers=None) -> dict:
        for attempt in range(2):
            c = self._conn()
            try:
                resp, payload, ttfb, total = self.s.request(c, method, path, body, headers)
                break
            except (ConnectionError, OSError):
                self.local.conn = None
                if attempt:
                    raise
        timing = parse_server_timing(", ".join(resp.headers.get_all("Server-Timing") or []))
        return {"route": route_shape(path), "status": resp.status, "ttfb": ttfb, "total": total, "bytes": len(payload),
                "st": {k: v for k, v in timing.items() if isinstance(v, float)}, "body": payload}

    def get_json(self, path: str):
        r = self.call("GET", path)
        if r["status"] != 200:
            raise RuntimeError(f"GET {route_shape(path)} -> {r['status']}")
        return json.loads(r["body"])

    def group(self, requests: list[tuple]) -> tuple[float, list[dict]]:
        """Sends the requests concurrently (a browser's parallel fetches); returns the wall time and the results."""
        t0 = time.perf_counter()
        futures = [self.pool.submit(self.call, *r) for r in requests]
        results = [f.result() for f in futures]
        return (time.perf_counter() - t0) * 1000, results


def q(**kw) -> str:
    return urllib.parse.urlencode({k: v for k, v in kw.items() if v is not None}, doseq=True)


# ---------------------------------------------------------------------------------------------------------------- discovery

def discover(c: Client) -> dict:
    """Ids only: the biggest library, its biggest folder (most children), a linked series folder, an item to read."""
    libs = c.get_json(f"{API}/libraries")
    best = None
    for lib in libs:
        root = c.get_json(f"{API}/libraries/{lib['id']}/browse?" + q(pageSize=500, sort="name", direction="asc"))
        n = len(root.get("items", []))
        total = root.get("totalCount") or n
        if best is None or total > best[1]:
            best = (lib["id"], total)
    lib_id = best[0]
    # Breadth-first over folders of that library (bounded), counting direct children.
    folders, seen, sizes, series, items = [None], 0, [], [], []
    while folders and seen < 400:
        parent = folders.pop(0)
        seen += 1
        cursor, count = None, 0
        while True:
            page = c.get_json(f"{API}/libraries/{lib_id}/browse?" + q(pageSize=500, sort="name", direction="asc",
                                                                       parentId=parent, cursor=cursor))
            for n in page.get("items", []):
                count += 1
                if n.get("kind") == "Folder":
                    folders.append(n["id"])
                    if n.get("linkedSeries") or n.get("hasSeriesInfo"):
                        series.append(n["id"])
                elif n.get("kind") == "Archive" and (n.get("pageCount") or 0) >= 40:
                    items.append((n["id"], n.get("pageCount"), parent))
            cursor = page.get("nextCursor")
            if not page.get("hasMore") or not cursor:
                break
        if parent:
            sizes.append((count, parent))
    sizes.sort(reverse=True)
    big = sizes[0][1] if sizes else None
    mid = sizes[len(sizes) // 2][1] if sizes else None
    # Prefer a series folder with a stored record (series-info answers 200 with content).
    series_id = None
    for sid in series[:60] + [s for _, s in sizes[:60]]:
        r = c.call("GET", f"{API}/nodes/{sid}/series-info")
        if r["status"] == 200 and len(r["body"]) > 200:
            series_id = sid
            break
    big_items = [i for i in items if i[2] == big] or items
    return {
        "libraryId": lib_id,
        "libraryRootCount": best[1],
        "bigFolderId": big,
        "bigFolderChildren": sizes[0][0] if sizes else 0,
        "midFolderId": mid,
        "seriesId": series_id,
        "readItems": [i[0] for i in big_items[:40]],
        "readItemsAll": [i[0] for i in items[:400]],
        "itemFolder": {i[0]: i[2] for i in items[:400]},
        "foldersVisited": seen,
    }


def search_word(c: Client, t: dict) -> str:
    """A word to search for, derived at run time from the big folder's own name (kept in memory only)."""
    node = c.get_json(f"{API}/nodes/{t['bigFolderId']}")
    words = [w for w in re.split(r"[^\w]+", node.get("displayName") or "") if len(w) >= 4]
    return (words[0] if words else (node.get("displayName") or "a")[:4]).lower()


def tag_token(c: Client, t: dict) -> str | None:
    """A facet value token from the series' Info panel (an opaque id like genre:fv_x), for the tag-filter search."""
    sid = t.get("seriesId")
    if not sid:
        return None
    info = c.get_json(f"{API}/nodes/{sid}/info")
    found: list[str] = []

    def walk(o):
        if isinstance(o, dict):
            for k, v in o.items():
                if k in ("token", "query", "filter") and isinstance(v, str) and ":" in v and len(v) < 80:
                    found.append(v)
                walk(v)
        elif isinstance(o, list):
            for v in o:
                walk(v)

    walk(info)
    for prefix in ("genre:", "type:", "creator:"):
        for tok in found:
            if tok.startswith(prefix):
                return tok
    return found[0] if found else None


# ---------------------------------------------------------------------------------------------------------------- points

class Runner:
    def __init__(self, c: Client, t: dict, args):
        self.c, self.t, self.args = c, t, args
        self.lib = t["libraryId"]
        self.big = t["bigFolderId"]
        self.word = search_word(c, t)
        self.tag = tag_token(c, t)
        self.item_i = 0
        self.later_cursor = self._cursor_after(pages=4)

    def _cursor_after(self, pages: int) -> str | None:
        cursor = None
        for _ in range(pages):
            page = self.c.get_json(f"{API}/libraries/{self.lib}/browse?" + q(pageSize=50, sort="name", direction="asc", cursor=cursor))
            cursor = page.get("nextCursor")
            if not cursor:
                return None
        return cursor

    def browse(self, parent=None, **extra):
        return f"{API}/libraries/{self.lib}/browse?" + q(pageSize=50, sort=extra.pop("sort", "name"),
                                                          direction=extra.pop("direction", "asc"), parentId=parent, **extra)

    def folder_reqs(self, folder, **extra):
        reqs = [("GET", f"{API}/libraries"), ("GET", self.browse(folder, **extra))]
        if not extra:
            reqs.append(("GET", f"{API}/libraries/{self.lib}/jump-index?" + q(parentId=folder, direction="asc")))
        if folder:
            reqs += [("GET", f"{API}/nodes/{folder}/breadcrumbs"), ("GET", f"{API}/nodes/{folder}"),
                     ("GET", f"{API}/nodes/{folder}/volume-view"), ("GET", f"{API}/nodes/{folder}/series-info")]
        return reqs

    def points(self) -> dict:
        t, lib, big = self.t, self.lib, self.big
        sid = t.get("seriesId") or big
        p = {
            "browse_root": lambda: self.folder_reqs(None),
            "browse_big_folder": lambda: self.folder_reqs(big),
            "browse_mid_folder": lambda: self.folder_reqs(t.get("midFolderId") or big),
            "filter_root_unread": lambda: self.folder_reqs(None, readState="unread"),
            "filter_big_read": lambda: self.folder_reqs(big, readState="read"),
            "filter_root_completion": lambda: self.folder_reqs(None, completion="MissingSome"),
            "volumes_big_folder": lambda: self.folder_reqs(big, group="volumes"),
            "volumes_root_recent": lambda: [("GET", f"{API}/libraries"),
                                            ("GET", self.browse(None, sort="recentlyUpdated", direction="desc", preferVolumes="true"))],
            "browse_root_page5": lambda: [("GET", self.browse(None, cursor=self.later_cursor))],
            "search_plain": lambda: [("GET", f"{API}/search?" + q(q=self.word))],
            "search_read_filter": lambda: [("GET", f"{API}/search?" + q(q=self.word, readState="unread"))],
            "search_tag": lambda: [("GET", f"{API}/search?" + q(f=self.tag))] if self.tag else [],
            "info_panel": lambda: [("GET", f"{API}/nodes/{sid}/series-info"), ("GET", f"{API}/nodes/{sid}/info"),
                                   ("GET", f"{API}/nodes/{sid}/declared-facts"), ("GET", f"{API}/nodes/{sid}/series-info/flags/mine")],
            "series_page": lambda: [("GET", f"{API}/nodes/{sid}/series-info?includeItems=true"),
                                    ("GET", self.browse(sid, pageSize=1)), ("GET", f"{API}/nodes/{sid}/info"),
                                    ("GET", f"{API}/nodes/{sid}/declared-facts"), ("GET", f"{API}/nodes/{sid}/missing")],
            "home": lambda: [("GET", f"{API}/libraries"), ("GET", f"{API}/reading/continue?limit=12"),
                             ("GET", f"{API}/home/recent-chapters"), ("GET", f"{API}/reading/library-preferences"),
                             ("GET", f"{API}/reading/home-libraries"), ("GET", f"{API}/favorites?pageSize=12")],
            "reader_open": None,  # special: manifest group, then the first page at both qualities
            "reader_back": None,  # special: the 1.45.1 ring refresh
        }
        return p

    def run_reader_open(self, rec):
        items = self.t["readItemsAll"] or self.t["readItems"]
        item = items[self.item_i % len(items)]
        self.item_i += 1
        wall, res = self.c.group([("GET", f"{API}/reading/{item}/effective-mode"), ("GET", f"{API}/items/{item}/manifest"),
                                  ("GET", f"{API}/nodes/{item}/neighbors"), ("GET", f"{API}/nodes/{item}"),
                                  ("GET", f"{API}/reading/{item}/bookmarks")])
        rec("reader_open", wall, res)
        manifest = next((r for r in res if "/manifest" in r["route"]), None)
        if not manifest or manifest["status"] != 200:
            return
        m = json.loads(manifest["body"])
        pages = m.get("pages") or []
        if not pages:
            return
        key = pages[0].get("entryKey") or pages[0].get("key")
        wall2, res2 = self.c.group([("GET", f"{API}/reading/progress/{item}"), ("GET", f"{API}/nodes/{item}/series-info")])
        rec("reader_open_after_manifest", wall2, res2)
        # First page, cold for this item: the paged-desktop variant (auto quality) and the full-size transcode (quality "full").
        w3, r3 = self.c.group([("GET", f"{API}/items/{item}/pages/{key}?maxDim=1440")])
        rec("first_page_auto", w3, r3)
        w4, r4 = self.c.group([("GET", f"{API}/items/{item}/pages/{key}")])
        rec("first_page_full", w4, r4)

    def run_reader_back(self, rec):
        items = self.t["readItems"]
        item = items[self.item_i % len(items)]
        self.item_i += 1
        folder = self.t["itemFolder"].get(item) or self.big
        page = self.c.get_json(self.browse(folder))
        ids = [n["id"] for n in page.get("items", [])][:200]
        reqs = [("GET", f"{API}/reading/{item}/read"), ("GET", f"{API}/reading/progress/{item}"),
                ("GET", self.browse(folder, pageSize=1)),
                ("GET", f"{API}/trackers/card-marks?" + q(libraryId=self.lib, parentId=folder, ids=ids))]
        wall, res = self.c.group(reqs)
        rec("reader_back", wall, res)


def summarize(samples: dict) -> dict:
    out = {}
    for point, rows in samples.items():
        walls = [r["wall"] for r in rows]
        per_route: dict[str, list] = {}
        for r in rows:
            for x in r["requests"]:
                per_route.setdefault(x["route"], []).append(x)
        out[point] = {
            "n": len(rows),
            "wall": {"p50": pct(walls, 0.5), "p95": pct(walls, 0.95), "max": max(walls) if walls else None},
            "requests": {
                route: {
                    "n": len(xs),
                    "status": {str(s): sum(1 for x in xs if x["status"] == s) for s in sorted({x["status"] for x in xs})},
                    "ttfb_p50": pct([x["ttfb"] for x in xs], 0.5), "ttfb_p95": pct([x["ttfb"] for x in xs], 0.95),
                    "ttfb_max": max(x["ttfb"] for x in xs),
                    "bytes_p50": pct([x["bytes"] for x in xs], 0.5),
                    **{f"st_{k}_p50": pct([x["st"][k] for x in xs if k in x["st"]], 0.5)
                       for k in ("app", "sql", "sqln", "lockw", "total", "slot", "worker", "encode")
                       if any(k in x["st"] for x in xs)},
                    **{f"st_{k}_p95": pct([x["st"][k] for x in xs if k in x["st"]], 0.95)
                       for k in ("app", "sql", "sqln") if any(k in x["st"] for x in xs)},
                }
                for route, xs in sorted(per_route.items())
            },
        }
    return out


def diagnostics(args) -> dict | None:
    if not args.diagnostics_user:
        return None
    pw = os.environ.get(args.diagnostics_password_env or "", "")
    d = Client(args.base, args.diagnostics_user, pw, connections=1)
    snap = d.get_json(f"{API}/operations/diagnostics")
    return {k: snap.get(k) for k in ("timestamp", "process", "media", "perf")}


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("mode", choices=["discover", "run"])
    ap.add_argument("--base", required=True)
    ap.add_argument("--user", required=True)
    ap.add_argument("--password-env", default="MP_PERF_PASSWORD")
    ap.add_argument("--targets", required=True)
    ap.add_argument("--n", type=int, default=20)
    ap.add_argument("--label", default="run")
    ap.add_argument("--out")
    ap.add_argument("--points", default="")
    ap.add_argument("--first-only", action="store_true", help="one sample per point (the cold run right after a restart)")
    ap.add_argument("--diagnostics-user")
    ap.add_argument("--diagnostics-password-env")
    args = ap.parse_args()
    c = Client(args.base, args.user, os.environ.get(args.password_env, ""))

    if args.mode == "discover":
        t = discover(c)
        with open(args.targets, "w") as f:
            json.dump(t, f)
        print(json.dumps({k: (len(v) if isinstance(v, (list, dict)) else (v if k.endswith(("Count", "Children", "Visited")) else bool(v)))
                          for k, v in t.items()}))
        return 0

    t = json.load(open(args.targets))
    before = diagnostics(args)
    r = Runner(c, t, args)
    points = r.points()
    chosen = [p for p in (args.points.split(",") if args.points else points.keys()) if p in points]
    samples: dict[str, list] = {}

    def rec(name, wall, res):
        samples.setdefault(name, []).append({"wall": wall, "requests": [{k: v for k, v in x.items() if k != "body"} for x in res]})

    t_start = time.time()
    rounds = 1 if args.first_only else args.n
    for _ in range(rounds):
        for name in chosen:
            if name == "reader_open":
                r.run_reader_open(rec)
            elif name == "reader_back":
                r.run_reader_back(rec)
            else:
                reqs = points[name]()
                if reqs:
                    wall, res = c.group(reqs)
                    rec(name, wall, res)
    after = diagnostics(args)
    out = {"label": args.label, "started": t_start, "seconds": time.time() - t_start, "rounds": rounds,
           "loadavg_end": open("/proc/loadavg").read().split()[:3] if os.path.exists("/proc/loadavg") else None,
           "points": summarize(samples), "diagnostics_before": before, "diagnostics_after": after,
           "has_tag": bool(r.tag)}
    if args.out:
        with open(args.out, "w") as f:
            json.dump(out, f)
    for name, s in out["points"].items():
        w = s["wall"]
        print(f"{name:28s} n={s['n']:3d} p50={w['p50'] or 0:8.1f} p95={w['p95'] or 0:8.1f} max={w['max'] or 0:8.1f} ms")
    return 0


if __name__ == "__main__":
    sys.exit(main())
