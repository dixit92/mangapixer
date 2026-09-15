#!/usr/bin/env python3
"""Review-instance reproduction for the reading_progress UNIQUE-constraint race.

Historical diagnostic script, kept as a record of how an already-fixed race was
triaged; it is not a maintained tool. The race is recovered in
ReadingStateService, and the leftover EF Core error log line is dropped by
RecoveredRaceNoiseFilter. The admin password below is a deliberate, fixed
throwaway for a local, disposable review instance, not a real credential.

Drives N concurrent first-write progress PUTs against the same (user, item) on a
fresh review instance built from the current tree. The guarded UpdateProgressAsync
path recovers the race (no 500), but EF Core still logs the failed INSERT at Error
level before the catch runs - the recovered-race "noise". We confirm:
  - every concurrent first-write returns 200 (recovered, no 500), and
  - the container log contains the SQLite-19 reading_progress line (the noise),
  - and (post-fix) that noise line is absent while writes still succeed.

Uses curl via subprocess because libcurl reliably stores the HttpOnly .MangaPlex.Auth
cookie, where Python's urllib cookiejar dropped it.

Run after `docker run -d --name mangaplex-race-repro -p 127.0.0.1:8097:8080 ...`
with a small multi-chapter CBZ library mounted read-only under /media, then pass
its in-container path with `--lib-root` (any folder holding a couple of archives
works). Pass `--expect-noise` to assert the noise is ABSENT (post-fix image);
default asserts the noise is PRESENT (pre-fix triage).
"""
import argparse
import json
import os
import subprocess
import sys
import tempfile
import threading

BASE = "http://127.0.0.1:8097"
CONTAINER = "mangaplex-race-repro"
LIBRARY_NAME = "Race Repro"
# Windows absolute path so Python and the native Windows curl resolve the jar
# identically (a bare "/tmp/..." resolved differently between the two).
CJ = os.path.join(tempfile.gettempdir(), "race-repro-cookies.txt")

parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
parser.add_argument("--lib-root", default="/media/Manga/<series>",
                    help="library folder inside the container (default: %(default)s, a placeholder)")
parser.add_argument("--expect-noise", action="store_true",
                    help="assert the EF Core noise line is ABSENT (post-fix image)")
ARGS = parser.parse_args()
LIB_ROOT = ARGS.lib_root
EXPECT_NOISE_ABSENT = ARGS.expect_noise


def curl(method, path, body=None, headers=None):
    cmd = ["curl", "-s", "-b", CJ, "-c", CJ, "-X", method, BASE + path,
           "-w", "\n%{http_code}"]
    if body is not None:
        cmd += ["-H", "Content-Type: application/json", "-d", json.dumps(body)]
    for k, v in (headers or {}).items():
        cmd += ["-H", f"{k}: {v}"]
    out = subprocess.run(cmd, capture_output=True, text=True).stdout
    idx = out.rfind("\n")
    status = int(out[idx + 1:])
    body_text = out[:idx]
    return status, body_text


def main():
    if os.path.exists(CJ):
        os.remove(CJ)

    # 1. Login (admin already created by a prior setup; create if not).
    st, body = curl("POST", "/api/v1/auth/login",
                    {"username": "admin", "password": "repro-pass-1234"})
    if st != 200:
        st, body = curl("POST", "/api/v1/auth/setup",
                        {"username": "admin", "password": "repro-pass-1234"})
        print(f"setup -> {st}")
        if st != 200:
            sys.exit(f"setup failed: {body[:200]}")
    else:
        print(f"login -> {st}")

    # 2. CSRF token.
    st, body = curl("GET", "/api/v1/auth/csrf")
    csrf = json.loads(body)["token"]
    auth = {"X-MangaPlex-Csrf": csrf}

    # 3. Register a small library (a couple of CBZ files under LIB_ROOT), or reuse it.
    st, body = curl("POST", "/api/v1/admin/libraries",
                    {"displayName": LIBRARY_NAME, "rootPath": LIB_ROOT}, auth)
    if st == 200:
        lib_id = json.loads(body)["id"]
        print(f"library registered: {lib_id}")
    else:
        # Duplicate or other non-200: find the existing repro library by name.
        st, body = curl("GET", "/api/v1/libraries")
        if st != 200:
            sys.exit(f"register failed ({st}) and libraries list failed: {body[:200]}")
        libs = json.loads(body)
        libs = libs if isinstance(libs, list) else libs.get("libraries", [])
        lib = next((l for l in libs if LIBRARY_NAME in l.get("displayName", l.get("name", ""))), None)
        if lib is None:
            sys.exit(f"register failed ({st}) and no existing {LIBRARY_NAME} library: {body[:200]}")
        lib_id = lib["id"]
        print(f"library reused: {lib_id}")

    # 4. Trigger a scan (202).
    st, body = curl("POST", f"/api/v1/admin/libraries/{lib_id}/scan", None, auth)
    print(f"scan -> {st}")

    # 5. Poll until the library has items and is no longer scanning.
    import time
    for _ in range(180):
        time.sleep(1)
        st, body = curl("GET", f"/api/v1/admin/libraries/{lib_id}")
        if st != 200:
            continue
        d = json.loads(body)
        if d.get("itemCount", 0) > 0 and not d.get("isScanning", False):
            print(f"library ready: itemCount={d['itemCount']}")
            break
    else:
        sys.exit("scan did not complete in time")

    # 6. Browse the library root to find an archive item.
    st, body = curl("GET", f"/api/v1/libraries/{lib_id}/browse")
    if st != 200:
        sys.exit(f"browse failed: {st} {body[:200]}")
    browse = json.loads(body)
    children = browse.get("items") or browse.get("children") or browse.get("nodes") or []
    items = [n for n in children if n.get("kind") == "Archive"]
    if not items:
        sys.exit(f"no archive items: {json.dumps(browse)[:300]}")
    item_id = items[0]["id"]
    print(f"picked item {item_id} ({items[0].get('displayName')})")

    # 7. Wait until the item is progress-writable (analysis created its ArchiveItem).
    for _ in range(180):
        st, body = curl("GET", f"/api/v1/reading/progress/{item_id}")
        if st == 200:
            break
        time.sleep(1)
    else:
        sys.exit("item never became progress-writable")

    # 8. Fire N concurrent first-writes (If-None-Match: * => first write, no row yet).
    N = 12
    barrier = threading.Barrier(N)
    results = [None] * N

    def writer(i):
        barrier.wait()
        h = dict(auth)
        h["If-None-Match"] = "*"
        results[i] = curl("PUT", f"/api/v1/reading/progress/{item_id}",
                          {"pageIndex": 0, "expectedContentVersion": 1,
                           "mutationId": f"repro-{i}"}, h)

    threads = [threading.Thread(target=writer, args=(i,)) for i in range(N)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()

    statuses = sorted(r[0] for r in results)
    five_hundreds = [i for i, r in enumerate(results) if r[0] == 500]
    print(f"concurrent first-write statuses: {statuses}")
    print(f"500s: {len(five_hundreds)}")

    # 9. Grep the container log for the SQLite-19 reading_progress noise.
    logs = subprocess.run(["docker", "logs", CONTAINER],
                          capture_output=True, text=True).stdout
    noise_lines = [ln for ln in logs.splitlines()
                   if "UNIQUE constraint failed: reading_progress" in ln]
    print(f"noise log lines (SQLite-19 reading_progress): {len(noise_lines)}")
    for ln in noise_lines[:3]:
        print("  " + ln.strip()[:200])

    no_500 = len(five_hundreds) == 0
    if EXPECT_NOISE_ABSENT:
        ok = no_500 and len(noise_lines) == 0
        print("RESULT: " + ("NOISE-SUPPRESSED (no 500, EF log absent) - filter works"
                            if ok else "unexpected (noise still present or 500s)"))
    else:
        ok = no_500 and len(noise_lines) > 0
        print("RESULT: " + ("RECOVERED-NOISE (no 500, EF log present) - guarded path triaged"
                            if ok else "unexpected"))
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
