"""Long-running soak probe (B5): idle the real app and sample resource counters.

Runs the app for hours and records private bytes / handle count / GDI objects /
USER objects / thread count every N minutes, so "memory grows over time" becomes
a measurable curve instead of a feeling.

The app is launched from a private copy so that re-publishing during the night
does not disturb a running soak.

ASCII-only output (Windows console encoding).
"""

import argparse
import csv
import ctypes
import json
import os
import shutil
import subprocess
import sys
import time
from ctypes import wintypes

k32 = ctypes.windll.kernel32
u32 = ctypes.windll.user32
psapi = ctypes.windll.psapi

try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass

PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
PROCESS_VM_READ = 0x0010
GR_GDIOBJECTS = 0
GR_USEROBJECTS = 1
ES_SYSTEM_REQUIRED = 0x00000002
ES_CONTINUOUS = 0x80000000
TH32CS_SNAPTHREAD = 0x00000004
INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value

MAIN_TITLE = "DeskLanes"
MODE_FULL = 1


class PROCESS_MEMORY_COUNTERS_EX(ctypes.Structure):
    _fields_ = [
        ("cb", wintypes.DWORD),
        ("PageFaultCount", wintypes.DWORD),
        ("PeakWorkingSetSize", ctypes.c_size_t),
        ("WorkingSetSize", ctypes.c_size_t),
        ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
        ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
        ("PagefileUsage", ctypes.c_size_t),
        ("PeakPagefileUsage", ctypes.c_size_t),
        ("PrivateUsage", ctypes.c_size_t),
    ]


class THREADENTRY32(ctypes.Structure):
    _fields_ = [
        ("dwSize", wintypes.DWORD),
        ("cntUsage", wintypes.DWORD),
        ("th32ThreadID", wintypes.DWORD),
        ("th32OwnerProcessID", wintypes.DWORD),
        ("tpBasePri", wintypes.LONG),
        ("tpDeltaPri", wintypes.LONG),
        ("dwFlags", wintypes.DWORD),
    ]


def keep_awake(on):
    """ES_SYSTEM_REQUIRED only: keeps the machine from sleeping, keeps display off.
    The requirement is per-thread and is released automatically when we exit."""
    try:
        k32.SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED if on else ES_CONTINUOUS)
    except Exception:
        pass


def thread_count(pid):
    snap = k32.CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0)
    if snap == INVALID_HANDLE_VALUE:
        return -1
    try:
        entry = THREADENTRY32()
        entry.dwSize = ctypes.sizeof(THREADENTRY32)
        ok = k32.Thread32First(snap, ctypes.byref(entry))
        n = 0
        while ok:
            if entry.th32OwnerProcessID == pid:
                n += 1
            ok = k32.Thread32Next(snap, ctypes.byref(entry))
        return n
    finally:
        k32.CloseHandle(snap)


def sample(pid):
    handle = k32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, False, pid)
    if not handle:
        return None
    try:
        pmc = PROCESS_MEMORY_COUNTERS_EX()
        pmc.cb = ctypes.sizeof(PROCESS_MEMORY_COUNTERS_EX)
        private = -1
        working = -1
        if psapi.GetProcessMemoryInfo(handle, ctypes.byref(pmc), pmc.cb):
            private = pmc.PrivateUsage
            working = pmc.WorkingSetSize
        handles = ctypes.c_uint(0)
        k32.GetProcessHandleCount(handle, ctypes.byref(handles))
        gdi = u32.GetGuiResources(handle, GR_GDIOBJECTS)
        user = u32.GetGuiResources(handle, GR_USEROBJECTS)
        return {
            "private_bytes": private,
            "working_set": working,
            "handles": handles.value,
            "gdi": gdi,
            "user": user,
            "threads": thread_count(pid),
        }
    finally:
        k32.CloseHandle(handle)


def find_window(title, timeout=90.0):
    WNDENUMPROC = ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)
    found = {}

    def proc(hwnd, lparam):
        if u32.IsWindowVisible(hwnd):
            n = u32.GetWindowTextLengthW(hwnd)
            if n:
                buf = ctypes.create_unicode_buffer(n + 1)
                u32.GetWindowTextW(hwnd, buf, n + 1)
                found.setdefault(buf.value, []).append(hwnd)
        return True

    cb = WNDENUMPROC(proc)
    deadline = time.time() + timeout
    while time.time() < deadline:
        found.clear()
        u32.EnumWindows(cb, 0)
        if found.get(title):
            return True
        time.sleep(0.2)
    return False


def kill_all():
    subprocess.run(["taskkill", "/IM", "DeskLanes.exe", "/F"], capture_output=True)
    time.sleep(1.0)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--hours", type=float, default=8.0)
    parser.add_argument("--interval-min", type=float, default=5.0)
    parser.add_argument("--errorlog", default="")
    args = parser.parse_args()

    cfgdir = os.path.join(os.environ.get("LOCALAPPDATA", ""), "DeskLanes")
    settings = os.path.join(cfgdir, "settings.json")
    original = None
    if os.path.exists(settings):
        with open(settings, "r", encoding="utf-8") as fh:
            original = fh.read()

    rows = []
    try:
        if original is not None:
            try:
                data = json.loads(original)
            except Exception:
                data = {}
            data["WindowMode"] = MODE_FULL
            with open(settings, "w", encoding="utf-8") as fh:
                json.dump(data, fh, ensure_ascii=False, indent=2)

        kill_all()
        t0 = time.time()
        proc = subprocess.Popen([args.exe])
        keep_awake(True)
        if not find_window(MAIN_TITLE):
            print("FATAL: main window never appeared")
            return 1
        print("app up after %.2f s, pid=%d" % (time.time() - t0, proc.pid))

        interval = args.interval_min * 60.0
        deadline = time.time() + args.hours * 3600.0
        next_at = time.time()
        try:
            while time.time() < deadline:
                now = time.time()
                s = sample(proc.pid)
                if s is None:
                    print("WARN: process gone at t=%.0f s" % (now - t0))
                    break
                s["t_sec"] = int(now - t0)
                s["t_iso"] = time.strftime("%Y-%m-%d %H:%M:%S")
                s["errors_log_bytes"] = (os.path.getsize(args.errorlog)
                                         if args.errorlog and os.path.exists(args.errorlog) else 0)
                rows.append(s)
                print("%s t=%5ds  private=%8.1f MB  handles=%4d  gdi=%4d  user=%4d  threads=%d"
                      % (s["t_iso"], s["t_sec"], s["private_bytes"] / 1048576.0,
                         s["handles"], s["gdi"], s["user"], s["threads"]))
                with open(args.out, "w", newline="", encoding="utf-8") as fh:
                    w = csv.DictWriter(fh, fieldnames=list(rows[0].keys()))
                    w.writeheader()
                    for r in rows:
                        w.writerow(r)
                next_at += interval
                sleep_for = next_at - time.time()
                if sleep_for <= 0:
                    next_at = time.time()
                    sleep_for = interval
                time.sleep(min(sleep_for, max(0.0, deadline - time.time())))
        except KeyboardInterrupt:
            print("interrupted")
    finally:
        keep_awake(False)
        kill_all()
        if original is not None:
            with open(settings, "w", encoding="utf-8") as fh:
                fh.write(original)

    if len(rows) >= 2:
        first, last = rows[0], rows[-1]
        growth = (last["private_bytes"] - first["private_bytes"]) / float(first["private_bytes"]) * 100.0
        print("")
        print("===== soak: %d samples over %.2f h =====" % (len(rows), last["t_sec"] / 3600.0))
        print("private bytes: %.1f MB -> %.1f MB (%+.1f%%)"
              % (first["private_bytes"] / 1048576.0, last["private_bytes"] / 1048576.0, growth))
        for key in ("handles", "gdi", "user", "threads"):
            print("%-8s: %d -> %d (%+d)" % (key, first[key], last[key], last[key] - first[key]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
