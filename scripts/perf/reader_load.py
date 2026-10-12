#!/usr/bin/env python3
"""Synthetic multi-reader load for a MangaPixer test instance (standard library only).

Each simulated reader logs in, opens items one after another and reads them the way the web reader does:

* paged profile: on every page turn the current page is needed, and the reader's prefetch asks for the next 6 and
  the previous 2 pages at once (reader.component.ts PrefetchAhead / PrefetchBehind); a page URL is requested once per
  item (the reader's dedup set), so a page that was prefetched is a browser-cache hit when it becomes current, or -
  while its prefetch is still running - the reader waits for that same request;
* webtoon profile: the visible page plus the next 4 (WebtoonPrefetchAhead);
* a browser keeps at most 6 connections per host (HTTP/1.1), so each reader has a pool of 6 connections; the current
  page is queued ahead of prefetches;
* page URLs carry the reader's ?maxDim=/&filter= for the simulated viewport (page-variant.ts targetMaxDim);
* after each turn the reader saves its progress (PUT /reading/progress, If-Match revision), as the web reader does.

Per request it records the time to the response headers (TTFB) and to the last byte, the status (503 / 499 count
separately), and the server's Server-Timing fields (cache hit / miss, db, slot wait, worker, open / read / encode) when
the server sends them. Per page turn it records how long the reader waited until that page was there ("ready").

Output: one CSV row per request and per turn (--csv) and a JSON summary (--summary) with p50 / p95 / max, the
current page separately from prefetch. With --diagnostics (needs an admin login) it also stores the server's media
metrics snapshot before and after the run, so the window's server-side numbers can be computed.

Use it ONLY against an instance you run yourself on synthetic or test media. It prints counts and timings only.
"""
from __future__ import annotations

import argparse
import csv
import http.client
import http.cookies
import json
import os
import queue
import random
import statistics
import sys
import threading
import time
import urllib.parse
from dataclasses import dataclass, field

LADDER = (1080, 1440, 2160)
PREFETCH_AHEAD, PREFETCH_BEHIND, WEBTOON_AHEAD = 6, 2, 4
PREFETCH_CAP = 2  # 1.42.0 reader: prefetches in flight at once (--prefetch-mode capped)
BROWSER_CONNECTIONS = 6

PROFILES = {
    # name: (view, viewport w, viewport h, dpr, think seconds mean)
    "paged-desktop": ("paged", 1280, 900, 1.0, 2.0),
    "paged-phone": ("paged", 390, 844, 3.0, 2.0),
    "paged-tablet": ("paged", 820, 1180, 2.0, 2.0),
    "paged-skim": ("paged", 1280, 900, 1.0, 0.5),
    "webtoon-phone": ("webtoon", 390, 844, 3.0, 0.8),
    "webtoon-desktop": ("webtoon", 1280, 900, 1.0, 0.8),
}


def target_max_dim(view: str, w: int, h: int, dpr: float, aspect: float) -> int:
    """page-variant.ts targetMaxDim for fit 'screen' (paged) and 'webtoon' at 100 % width."""
    if view == "webtoon":
        box_w = float(w)
        box_h = box_w * aspect if aspect > 0 else box_w
    else:
        box_w, box_h = float(w), float(h)
    needed = int(-(-max(box_w, box_h) * dpr // 1))
    for bucket in LADDER:
        if needed <= bucket:
            return bucket
    return 0  # bigger than the top rung: the full-size transcode


def parse_server_timing(value: str | None) -> dict:
    out: dict = {}
    if not value:
        return out
    for part in value.split(","):
        bits = [b.strip() for b in part.split(";")]
        name = bits[0]
        for b in bits[1:]:
            if b.startswith("dur="):
                try:
                    out[name] = float(b[4:])
                except ValueError:
                    pass
            elif b.startswith("desc="):
                out[name] = b[5:]
    return out


class Session:
    """Cookie session over plain http.client (one connection per thread)."""

    def __init__(self, base: str):
        u = urllib.parse.urlsplit(base)
        self.host, self.port = u.hostname, u.port or 80
        self.cookies: dict[str, str] = {}
        self.lock = threading.Lock()
        self.csrf: str | None = None

    def conn(self) -> http.client.HTTPConnection:
        return http.client.HTTPConnection(self.host, self.port, timeout=300)

    def _cookie_header(self) -> str:
        with self.lock:
            return "; ".join(f"{k}={v}" for k, v in self.cookies.items())

    def _store(self, resp: http.client.HTTPResponse) -> None:
        for h in resp.headers.get_all("Set-Cookie") or []:
            c = http.cookies.SimpleCookie()
            c.load(h)
            with self.lock:
                for k, m in c.items():
                    self.cookies[k] = m.value

    def request(self, conn, method: str, path: str, body=None, headers=None):
        hdr = {"Cookie": self._cookie_header(), "Accept": "*/*"}
        hdr.update(headers or {})
        data = None
        if body is not None:
            data = json.dumps(body).encode()
            hdr["Content-Type"] = "application/json"
        if method != "GET":
            hdr["X-MangaPixer-Csrf"] = self.csrf_token(conn)
        t0 = time.perf_counter()
        conn.request(method, path, body=data, headers=hdr)
        resp = conn.getresponse()
        ttfb = time.perf_counter() - t0
        payload = resp.read()
        total = time.perf_counter() - t0
        self._store(resp)
        return resp, payload, ttfb * 1000, total * 1000

    def csrf_token(self, conn) -> str:
        if self.csrf is None:
            resp, payload, _, _ = self.request(conn, "GET", "/api/v1/auth/csrf")
            self.csrf = json.loads(payload)["token"]
        return self.csrf

    def get_json(self, conn, path: str):
        resp, payload, _, _ = self.request(conn, "GET", path)
        if resp.status != 200:
            raise RuntimeError(f"GET {path.split('?')[0]} -> {resp.status}")
        return json.loads(payload)

    def login(self, user: str, password: str) -> None:
        c = self.conn()
        resp, _, _, _ = self.request(c, "POST", "/api/v1/auth/login", {"username": user, "password": password})
        if resp.status != 200:
            raise RuntimeError(f"login -> {resp.status}")
        self.csrf = None  # a new session cookie gets a new token
        c.close()


def list_items(session: Session, library_name: str | None, min_pages: int) -> list[dict]:
    """Every readable archive of the chosen library (breadth-first browse), as {id, pages}."""
    c = session.conn()
    libs = session.get_json(c, "/api/v1/libraries")
    libs = [l for l in libs if library_name is None or l.get("name") == library_name]
    if not libs:
        raise RuntimeError("library not found")
    items, folders = [], [(libs[0]["id"], None)]
    while folders:
        lib, parent = folders.pop(0)
        cursor = None
        while True:
            q = {"pageSize": 200}
            if parent:
                q["parentId"] = parent
            if cursor:
                q["cursor"] = cursor
            page = session.get_json(c, f"/api/v1/libraries/{lib}/browse?" + urllib.parse.urlencode(q))
            for n in page["items"]:
                if n.get("kind") == "Folder":
                    folders.append((lib, n["id"]))
                elif (n.get("pageCount") or 0) >= min_pages:
                    # The name is used only to order the list (never printed or written): ids are random per instance,
                    # names are not, so two instances of the same library read the same items in the same order.
                    items.append({"id": n["id"], "pages": n["pageCount"], "key": (n.get("displayName") or "").casefold()})
            cursor = page.get("nextCursor")
            if not page.get("hasMore") or not cursor:
                break
    c.close()
    items.sort(key=lambda i: (i["key"], i["pages"], i["id"]))
    for i in items:
        del i["key"]
    return items


@dataclass(order=True)
class Job:
    priority: int
    seq: int
    url: str = field(compare=False)
    page: int = field(compare=False)
    kind: str = field(compare=False)
    done: threading.Event = field(compare=False, default_factory=threading.Event)
    finished_at: float = field(compare=False, default=0.0)
    headers: dict | None = field(compare=False, default=None)
    on_done: object = field(compare=False, default=None)


class Reader(threading.Thread):
    def __init__(self, idx: int, args, items: list[dict], rows: list, rows_lock: threading.Lock, t_zero: float):
        super().__init__(daemon=True)
        self.idx, self.args, self.items = idx, args, items
        self.profile_name = args.profiles[idx % len(args.profiles)]
        self.view, self.vw, self.vh, self.dpr, self.think = PROFILES[self.profile_name]
        if args.think is not None:
            self.think = args.think
        self.rows, self.rows_lock, self.t_zero = rows, rows_lock, t_zero
        self.rng = random.Random(args.seed * 1000 + idx)
        self.session = Session(args.base)
        self.jobs: queue.PriorityQueue = queue.PriorityQueue()
        self.seq = 0
        self.lock = threading.Lock()
        self.stop_at = 0.0
        self.error: str | None = None

    def emit(self, **row) -> None:
        row.update(run=self.args.run_id, scenario=self.args.scenario, reader=self.idx, profile=self.profile_name)
        with self.rows_lock:
            self.rows.append(row)

    def connection_loop(self) -> None:
        conn = self.session.conn()
        while True:
            job = self.jobs.get()
            if job is None or job.url is None:
                break
            started = time.perf_counter()
            try:
                resp, payload, ttfb, total = self.session.request(conn, "GET", job.url, headers=job.headers)
                st = parse_server_timing(resp.headers.get("Server-Timing"))
                status, size = resp.status, len(payload)
            except Exception as ex:  # a dropped connection: record and reconnect
                conn.close()
                conn = self.session.conn()
                st, status, size = {}, 0, 0
                ttfb = total = (time.perf_counter() - started) * 1000
                self.error = type(ex).__name__
            job.finished_at = time.perf_counter()
            job.done.set()
            if callable(job.on_done):
                job.on_done()
            self.emit(event="request", kind=job.kind, page=job.page, t_s=round(started - self.t_zero, 3),
                      ttfb_ms=round(ttfb, 1), total_ms=round(total, 1), status=status, bytes=size,
                      cache=st.get("cache", ""), st_db=st.get("db", ""), st_slot=st.get("slot", ""),
                      st_worker=st.get("worker", ""), st_open=st.get("open", ""), st_read=st.get("read", ""),
                      st_encode=st.get("encode", ""), st_total=st.get("total", ""))
        conn.close()

    def submit(self, url: str, page: int, kind: str, headers: dict | None = None, on_done=None) -> Job:
        with self.lock:
            self.seq += 1
            seq = self.seq
        job = Job(0 if kind == "current" else 1, seq, url, page, kind, headers=headers, on_done=on_done)
        self.jobs.put(job)
        return job

    def run(self) -> None:
        try:
            self.session.login(self.args.user, self.args.password)
        except Exception as ex:
            self.error = f"login: {ex}"
            return
        conns = [threading.Thread(target=self.connection_loop, daemon=True) for _ in range(BROWSER_CONNECTIONS)]
        for t in conns:
            t.start()
        ctl = self.session.conn()
        self.stop_at = time.perf_counter() + self.args.duration
        turns = 0
        try:
            for item in self.items:
                if time.perf_counter() >= self.stop_at:
                    break
                turns += self.read_item(ctl, item)
        except Exception as ex:
            self.error = f"{type(ex).__name__}: {ex}"
        finally:
            for _ in conns:
                self.seq += 1
                self.jobs.put(Job(9, self.seq, None, -1, "stop"))  # type: ignore[arg-type]
            for t in conns:
                t.join(timeout=600)
            ctl.close()

    def read_item(self, ctl, item: dict) -> int:
        opened = time.perf_counter()
        resp, payload, ttfb, total = self.session.request(ctl, "GET", f"/api/v1/items/{item['id']}/manifest")
        self.emit(event="request", kind="manifest", page=-1, t_s=round(opened - self.t_zero, 3), ttfb_ms=round(ttfb, 1),
                  total_ms=round(total, 1), status=resp.status, bytes=len(payload))
        if resp.status != 200:
            return 0
        manifest = json.loads(payload)
        pages = manifest.get("pages") or []
        content_version = manifest.get("contentVersion")
        revision = 0
        jobs: dict[int, Job] = {}

        def url_for(i: int) -> str:
            p = pages[i]
            w, h = p.get("width") or 0, p.get("height") or 0
            md = target_max_dim(self.view, self.vw, self.vh, self.dpr, (h / w) if w > 0 and h > 0 else 0)
            base = f"/api/v1/items/{item['id']}/pages/{urllib.parse.quote(p['entryKey'], safe='')}"
            return f"{base}?maxDim={md}&filter={self.args.filter}" if md > 0 else base

        capped = self.args.prefetch_mode == "capped"
        pending: list[int] = []      # capped mode: prefetches waiting to start, in order
        state = {"inflight": 0, "held": False}

        def pump() -> None:
            # 1.42.0 web reader: at most PREFETCH_CAP prefetches in flight, none while the current page loads.
            while True:
                with self.lock:
                    if state["held"] or state["inflight"] >= PREFETCH_CAP or not pending:
                        return
                    i = pending.pop(0)
                    state["inflight"] += 1
                jobs[i] = self.submit(url_for(i), i, "prefetch", headers={"X-MangaPixer-Prefetch": "1"}, on_done=prefetch_done)

        def prefetch_done() -> None:
            with self.lock:
                state["inflight"] -= 1
            pump()

        def need(i: int, kind: str) -> None:
            if not (0 <= i < len(pages)) or i in jobs:
                return
            if kind == "prefetch" and capped:
                with self.lock:
                    if i not in pending:
                        pending.append(i)
                return
            if kind == "current" and capped:
                with self.lock:
                    if i in pending:
                        pending.remove(i)
            jobs[i] = self.submit(url_for(i), i, kind)

        turns = 0
        for i in range(len(pages)):
            if time.perf_counter() >= self.stop_at:
                break
            turned = time.perf_counter()
            prefetched = i in jobs
            already_there = prefetched and jobs[i].done.is_set()
            need(i, "current")
            if capped and self.view != "webtoon" and not jobs[i].done.is_set():
                with self.lock:
                    state["held"] = True
            if self.view == "webtoon":
                for d in range(1, WEBTOON_AHEAD + 1):
                    need(i + d, "prefetch")
            else:
                for d in range(1, PREFETCH_AHEAD + 1):
                    need(i + d, "prefetch")
                for d in range(1, PREFETCH_BEHIND + 1):
                    need(i - d, "prefetch")
            if capped:
                pump()
            jobs[i].done.wait(timeout=600)
            if capped:
                with self.lock:
                    state["held"] = False
                pump()
            ready = 0.0 if already_there else max(0.0, (jobs[i].finished_at - turned) * 1000)
            self.emit(event="turn", kind="first" if i == 0 else "turn", page=i, t_s=round(turned - self.t_zero, 3),
                      ready_ms=round(ready, 1), prefetched=int(prefetched))
            turns += 1
            # The reader saves the position after each turn.
            body = {"pageIndex": i, "expectedContentVersion": content_version,
                    "mutationId": f"perf-{self.idx}-{self.seq}-{i}", "entryKey": pages[i]["entryKey"]}
            hdr = {"If-Match": f'"{revision}"'} if revision > 0 else {"If-None-Match": "*"}
            t0 = time.perf_counter()
            try:
                presp, ppayload, pttfb, ptotal = self.session.request(ctl, "PUT", f"/api/v1/reading/progress/{item['id']}", body, hdr)
                if presp.status == 200:
                    revision = json.loads(ppayload).get("revision", revision)
                elif presp.status == 412:
                    revision = self.session.get_json(ctl, f"/api/v1/reading/progress/{item['id']}").get("revision", revision)
                self.emit(event="request", kind="progress", page=i, t_s=round(t0 - self.t_zero, 3), ttfb_ms=round(pttfb, 1),
                          total_ms=round(ptotal, 1), status=presp.status, bytes=len(ppayload))
            except Exception as ex:
                self.error = f"progress: {type(ex).__name__}"
            # Think time (+-30 %), minus what the turn already took.
            pause = self.think * self.rng.uniform(0.7, 1.3) - (time.perf_counter() - turned)
            if pause > 0:
                time.sleep(pause)
        return turns


def pct(values: list[float], q: float) -> float | None:
    if not values:
        return None
    v = sorted(values)
    k = max(0, min(len(v) - 1, int(-(-q * len(v) // 1)) - 1))
    return round(v[k], 1)


def stats(values: list[float]) -> dict:
    return {"n": len(values), "p50": pct(values, 0.5), "p95": pct(values, 0.95),
            "max": round(max(values), 1) if values else None,
            "mean": round(statistics.fmean(values), 1) if values else None}


def summarize(rows: list[dict]) -> dict:
    req = [r for r in rows if r["event"] == "request"]
    pages = [r for r in req if r["kind"] in ("current", "prefetch")]
    turns = [r for r in rows if r["event"] == "turn"]
    out: dict = {
        "turn_ready_ms": stats([r["ready_ms"] for r in turns]),
        "turn_ready_ms_first_page": stats([r["ready_ms"] for r in turns if r["kind"] == "first"]),
        "turn_ready_ms_later_pages": stats([r["ready_ms"] for r in turns if r["kind"] == "turn"]),
        "turns_waiting_over_100ms": sum(1 for r in turns if r["ready_ms"] > 100),
        "current_total_ms": stats([r["total_ms"] for r in pages if r["kind"] == "current" and r["status"] == 200]),
        "current_ttfb_ms": stats([r["ttfb_ms"] for r in pages if r["kind"] == "current" and r["status"] == 200]),
        "prefetch_total_ms": stats([r["total_ms"] for r in pages if r["kind"] == "prefetch" and r["status"] == 200]),
        "progress_total_ms": stats([r["total_ms"] for r in req if r["kind"] == "progress"]),
        "manifest_total_ms": stats([r["total_ms"] for r in req if r["kind"] == "manifest"]),
        "page_requests": len(pages),
        "status_counts": {},
        "cache": {"hit": sum(1 for r in pages if r.get("cache") == "hit"), "miss": sum(1 for r in pages if r.get("cache") == "miss")},
        "server_slot_ms_miss": stats([float(r["st_slot"]) for r in pages if r.get("st_slot") not in ("", None)]),
        "server_worker_ms_miss": stats([float(r["st_worker"]) for r in pages if r.get("st_worker") not in ("", None)]),
        "server_encode_ms": stats([float(r["st_encode"]) for r in pages if r.get("st_encode") not in ("", None)]),
        "server_open_ms": stats([float(r["st_open"]) for r in pages if r.get("st_open") not in ("", None)]),
        "server_read_ms": stats([float(r["st_read"]) for r in pages if r.get("st_read") not in ("", None)]),
        "server_db_ms": stats([float(r["st_db"]) for r in pages if r.get("st_db") not in ("", None)]),
        "by_profile": {},
    }
    for r in req:
        out["status_counts"][str(r["status"])] = out["status_counts"].get(str(r["status"]), 0) + 1
    for prof in sorted({r["profile"] for r in turns}):
        out["by_profile"][prof] = {
            "turn_ready_ms_later_pages": stats([r["ready_ms"] for r in turns if r["profile"] == prof and r["kind"] == "turn"]),
            "current_total_ms": stats([r["total_ms"] for r in pages if r["profile"] == prof and r["kind"] == "current" and r["status"] == 200]),
        }
    return out


def diagnostics(base: str, user: str, password: str) -> dict | None:
    s = Session(base)
    try:
        s.login(user, password)
        c = s.conn()
        d = s.get_json(c, "/api/v1/operations/diagnostics")
        c.close()
        return d.get("media")
    except Exception:
        return None


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--base", required=True, help="http://host:port of YOUR test instance")
    ap.add_argument("--user", required=True)
    ap.add_argument("--password-env", default="MP_PERF_PASSWORD", help="environment variable holding the password")
    ap.add_argument("--readers", type=int, default=1)
    ap.add_argument("--profiles", default="paged-desktop,webtoon-phone", help="comma list, assigned round-robin: " + ",".join(PROFILES))
    ap.add_argument("--think", type=float, default=None, help="override the profile's mean think time (s)")
    ap.add_argument("--duration", type=float, default=60, help="seconds each reader reads")
    ap.add_argument("--library", default=None, help="library display name (default: the first)")
    ap.add_argument("--min-pages", type=int, default=12)
    ap.add_argument("--item-offset", type=int, default=0, help="rotate the item list (pick other items per run)")
    ap.add_argument("--items-per-reader", type=int, default=6)
    ap.add_argument("--filter", default="balanced")
    ap.add_argument("--prefetch-mode", choices=["legacy", "capped"], default="capped",
                    help="legacy = the pre-1.42.0 reader (all prefetches at once, unmarked); capped = 1.42.0 (2 at a time, "
                         "after the current page, marked X-MangaPixer-Prefetch)")
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--run-id", default="run")
    ap.add_argument("--scenario", default="x")
    ap.add_argument("--csv", required=True)
    ap.add_argument("--summary", required=True)
    ap.add_argument("--diagnostics", action="store_true", help="store the server's media snapshot before / after (admin)")
    args = ap.parse_args()
    args.password = os.environ.get(args.password_env, "")
    args.profiles = [p.strip() for p in args.profiles.split(",") if p.strip()]
    for p in args.profiles:
        if p not in PROFILES:
            ap.error(f"unknown profile {p}")

    probe = Session(args.base)
    probe.login(args.user, args.password)
    items = list_items(probe, args.library, args.min_pages)
    if not items:
        print("no items", file=sys.stderr)
        return 2
    k = args.item_offset % len(items)
    items = items[k:] + items[:k]
    per = [items[r::args.readers][: args.items_per_reader] for r in range(args.readers)]

    before = diagnostics(args.base, args.user, args.password) if args.diagnostics else None
    rows: list[dict] = []
    lock = threading.Lock()
    t_zero = time.perf_counter()
    readers = [Reader(i, args, per[i], rows, lock, t_zero) for i in range(args.readers)]
    for r in readers:
        r.start()
    for r in readers:
        r.join()
    wall = time.perf_counter() - t_zero
    after = diagnostics(args.base, args.user, args.password) if args.diagnostics else None

    cols = ["run", "scenario", "reader", "profile", "event", "kind", "page", "t_s", "ttfb_ms", "total_ms", "status", "bytes",
            "cache", "st_db", "st_slot", "st_worker", "st_open", "st_read", "st_encode", "st_total", "ready_ms", "prefetched"]
    with open(args.csv, "w", newline="") as f:
        w = csv.DictWriter(f, fieldnames=cols, extrasaction="ignore")
        w.writeheader()
        for row in sorted(rows, key=lambda r: r["t_s"]):
            w.writerow(row)
    summary = {"run": args.run_id, "scenario": args.scenario, "readers": args.readers, "profiles": args.profiles,
               "wall_s": round(wall, 1), "errors": [r.error for r in readers if r.error],
               "items": len(items), **summarize(rows), "media_before": before, "media_after": after}
    with open(args.summary, "w") as f:
        json.dump(summary, f, indent=1)
    print(json.dumps({k: summary[k] for k in ("run", "scenario", "wall_s", "errors", "page_requests", "status_counts", "cache",
                                              "turn_ready_ms_later_pages", "current_total_ms", "prefetch_total_ms")}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
