"""Startup performance probe (B6: cold start to main window visible).

Drives the real published exe and measures how long it takes until the main
window is actually on screen. Also checks that logs/errors.log does not grow
during a normal start.

Run the published build first -- measuring a stale exe is meaningless:

    dotnet publish src/DeskLanes.App/DeskLanes.App.csproj -c Release \
        -p:PublishProfile=win-x64-selfcontained

ASCII-only output on purpose (Windows console encoding).
"""

import ctypes
import json
import os
import shutil
import subprocess
import sys
import time
from ctypes import wintypes

u = ctypes.windll.user32
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)  # PER_MONITOR_DPI_AWARE
except Exception as exc:
    print("WARN: could not set DPI awareness: %s" % exc)

# 仓库根目录：本脚本在 tools\ 下，上一级就是仓库根（换个目录 / 换台机器也能直接用）
REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(REPO, "publish", "win-x64-selfcontained", "DeskLanes.exe")
CFGDIR = os.path.join(os.environ.get("LOCALAPPDATA", ""), "DeskLanes")
SETTINGS = os.path.join(CFGDIR, "settings.json")
ERRORLOG = os.path.join(CFGDIR, "logs", "errors.log")
EXTRACT_DIR = os.path.join(os.environ.get("TEMP", ""), ".net", "DeskLanes")

# WindowMode: Floating=0, Full=1, Sidebar=2 -- force Full so the main window shows.
MODE_FULL = 1
MAIN_TITLE = "DeskLanes"

results = []


def say(tag, ok, detail=""):
    results.append((tag, ok, detail))
    print("%-6s %s%s" % ("PASS" if ok else "FAIL", tag, (" -- " + detail) if detail else ""))


# ---------------- window lookup ----------------

_found = {}
WNDENUMPROC = ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)


def _enum_proc(hwnd, lparam):
    if u.IsWindowVisible(hwnd):
        n = u.GetWindowTextLengthW(hwnd)
        if n:
            buf = ctypes.create_unicode_buffer(n + 1)
            u.GetWindowTextW(hwnd, buf, n + 1)
            _found.setdefault(buf.value, []).append(hwnd)
    return True


_enum_proc_cb = WNDENUMPROC(_enum_proc)


def find_window(title, timeout=90.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        _found.clear()
        u.EnumWindows(_enum_proc_cb, 0)
        hits = _found.get(title)
        if hits:
            return hits[0]
        time.sleep(0.1)
    return None


def kill_all():
    subprocess.run(["taskkill", "/IM", "DeskLanes.exe", "/F"],
                   capture_output=True)
    time.sleep(1.0)


def patch_settings(mode):
    data = {}
    if os.path.exists(SETTINGS):
        try:
            with open(SETTINGS, "r", encoding="utf-8") as fh:
                data = json.load(fh)
        except Exception:
            data = {}
    data["WindowMode"] = mode
    with open(SETTINGS, "w", encoding="utf-8") as fh:
        json.dump(data, fh, ensure_ascii=False, indent=2)


def measure_once(label, timeout=90.0):
    kill_all()
    t0 = time.time()
    proc = subprocess.Popen([EXE])
    hwnd = find_window(MAIN_TITLE, timeout=timeout)
    elapsed = time.time() - t0
    kill_all()
    try:
        proc.wait(timeout=10)
    except Exception:
        pass
    return hwnd, elapsed


def log_size():
    try:
        return os.path.getsize(ERRORLOG)
    except OSError:
        return 0


def main():
    if not os.path.exists(EXE):
        print("FATAL: exe not found: %s" % EXE)
        return 1

    original = None
    if os.path.exists(SETTINGS):
        with open(SETTINGS, "r", encoding="utf-8") as fh:
            original = fh.read()

    try:
        patch_settings(MODE_FULL)
        log_before = log_size()

        # ---- cold start: drop the single-file extraction cache first ----
        if os.path.isdir(EXTRACT_DIR):
            shutil.rmtree(EXTRACT_DIR, ignore_errors=True)
        cold_hwnd, cold = measure_once("cold")
        print("cold start -- %s -- %.2f s" % ("window found" if cold_hwnd else "NO WINDOW", cold))
        say("cold start shows the main window", cold_hwnd is not None)
        say("cold start under 15 s", cold < 15.0, "%.2f s" % cold)

        # ---- warm starts (extraction cache already in place) ----
        warm = []
        for i in range(3):
            hwnd, secs = measure_once("warm%d" % i)
            print("warm start %d -- %s -- %.2f s" % (i, "window found" if hwnd else "NO WINDOW", secs))
            warm.append(secs)
            say("warm start %d shows the main window" % i, hwnd is not None)

        warm.sort()
        median = warm[len(warm) // 2]
        print("warm median = %.2f s (all: %s)" % (median, ", ".join("%.2f" % s for s in warm)))
        say("warm start median under 3 s (B6 criterion)", median < 3.0, "%.2f s" % median)

        log_after = log_size()
        say("errors.log did not grow during startup", log_after == log_before,
            "%d -> %d bytes" % (log_before, log_after))

        passed = sum(1 for _, ok, _ in results if ok)
        failed = len(results) - passed
        print("")
        print("===== startup probe: passed %d, failed %d =====" % (passed, failed))
        return 0 if failed == 0 else 1
    finally:
        kill_all()
        if original is not None:
            with open(SETTINGS, "w", encoding="utf-8") as fh:
                fh.write(original)


if __name__ == "__main__":
    sys.exit(main())
