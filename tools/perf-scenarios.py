#!/usr/bin/env python3
"""分场景内存占用：仅托盘 -> 悬浮球 -> 悬浮窗 -> 全屏 -> 侧栏 -> 静置。

用真实发布的 exe（不是 UiSnapshot），通过 P/Invoke 读 ProcessMemoryCountersEx，
与 soak-8h.py 同源，避免 WorkingSet 受分页影响的误判。
ASCII-only output.
"""
import ctypes
import json
import os
import subprocess
import time
from ctypes import wintypes

u = ctypes.windll.user32
psapi = ctypes.windll.psapi

EXE = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                   "publish", "win-x64-selfcontained", "DeskLanes.exe")
CFGDIR = os.path.join(os.environ.get("LOCALAPPDATA", ""), "DeskLanes")
CFG = os.path.join(CFGDIR, "settings.json")


class PMC(ctypes.Structure):
    _fields_ = [("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD),
                ("PeakWorkingSetSize", ctypes.c_size_t), ("WorkingSetSize", ctypes.c_size_t),
                ("QuotaPeakPagedPoolUsage", ctypes.c_size_t), ("QuotaPagedPoolUsage", ctypes.c_size_t),
                ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t), ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
                ("PagefileUsage", ctypes.c_size_t), ("PeakPagefileUsage", ctypes.c_size_t)]


def mem():
    procs = []
    import subprocess as sp
    out = sp.run(["tasklist", "/FI", "IMAGENAME eq DeskLanes.exe", "/FO", "CSV", "/NH"],
                 capture_output=True, text=True).stdout
    for line in out.splitlines():
        if "DeskLanes.exe" in line:
            pid = int(line.split('","')[1])
            h = ctypes.windll.kernel32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
            p = PMC()
            p.cb = ctypes.sizeof(PMC)
            if h and psapi.GetProcessMemoryInfo(h, ctypes.byref(p), p.cb):
                procs.append({"ws": p.WorkingSetSize / 1048576, "private": p.PagefileUsage / 1048576})
            if h:
                ctypes.windll.kernel32.CloseHandle(h)
    if not procs:
        return None
    return {"ws": round(sum(x["ws"] for x in procs), 1),
            "private": round(sum(x["private"] for x in procs), 1)}


def set_mode(mode):
    s = json.load(open(CFG, encoding="utf-8"))
    s["WindowMode"] = mode
    s["CollapseOnLaunch"] = False
    s["FloatingHideOnDeactivate"] = False
    json.dump(s, open(CFG, "w", encoding="utf-8"), ensure_ascii=False, indent=2)


def find(title):
    out = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
    def cb(h, _):
        n = u.GetWindowTextLengthW(h)
        if n and u.IsWindowVisible(ctypes.c_void_p(h)):
            b = ctypes.create_unicode_buffer(n + 1)
            u.GetWindowTextW(h, b, n + 1)
            if b.value == title:
                out.append(h)
        return True
    u.EnumWindows(cb, 0)
    return out[0] if out else 0


def sample(label, results, settle=2.0):
    time.sleep(settle)
    m = mem()
    results.append({"scene": label, **(m or {"ws": None, "private": None})})
    print("%-18s ws=%7s MB  private=%7s MB" % (label, m["ws"] if m else "N/A", m["private"] if m else "N/A"), flush=True)


def main():
    original = open(CFG, encoding="utf-8").read()
    results = []
    subprocess.run(["taskkill", "/F", "/IM", "DeskLanes.exe"], capture_output=True)
    try:
        # 1) 仅托盘驻留：启动后隐藏所有窗口
        set_mode(0)
        subprocess.run(["taskkill", "/F", "/IM", "DeskLanes.exe"], capture_output=True)
        time.sleep(0.5)
        p = subprocess.Popen([EXE])
        time.sleep(5)
        sample("startup+idle", results, 2)
        # 关闭窗口到托盘（最小化）
        for title in ["DeskLanes 快速启动", "DeskLanes 一格桌面"]:
            h = find(title)
            if h:
                u.ShowWindow(ctypes.c_void_p(h), 6)  # SW_MINIMIZE
        sample("tray only", results, 3)

        # 2) 悬浮球：Esc 收起
        subprocess.run(["taskkill", "/F", "/IM", "DeskLanes.exe"], capture_output=True)
        cfg = json.load(open(CFG, encoding="utf-8"))
        cfg["CollapseOnLaunch"] = True
        json.dump(cfg, open(CFG, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
        p = subprocess.Popen([EXE])
        time.sleep(5)
        sample("floating ball", results, 2)

        # 3) 悬浮窗：双击球（用快捷键等价）
        h = find("DeskLanes 边缘细线")
        if h:
            class R(ctypes.Structure):
                _fields_ = [("l", ctypes.c_long), ("t", ctypes.c_long), ("rr", ctypes.c_long), ("b", ctypes.c_long)]
            r = R()
            u.GetWindowRect(ctypes.c_void_p(h), ctypes.byref(r))
            cx, cy = (r.l + r.rr) // 2, (r.t + r.b) // 2
            u.SetCursorPos(cx, cy)
            time.sleep(0.2)
            u.mouse_event(2, 0, 0, 0, 0); time.sleep(0.05); u.mouse_event(4, 0, 0, 0, 0)
            time.sleep(0.08)
            u.mouse_event(2, 0, 0, 0, 0); time.sleep(0.05); u.mouse_event(4, 0, 0, 0, 0)
        sample("floating window", results, 3)

        # 4) 全屏：托盘菜单切换太复杂，直接用配置重启
        subprocess.run(["taskkill", "/F", "/IM", "DeskLanes.exe"], capture_output=True)
        set_mode(1)  # Full mode (enum: 0 Floating, 1 Full, 2 Sidebar)
        cfg = json.load(open(CFG, encoding="utf-8"))
        cfg["WindowMode"] = 1
        json.dump(cfg, open(CFG, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
        p = subprocess.Popen([EXE])
        time.sleep(6)
        sample("full workspace", results, 2)

        # 5) 侧栏
        subprocess.run(["taskkill", "/F", "/IM", "DeskLanes.exe"], capture_output=True)
        cfg = json.load(open(CFG, encoding="utf-8"))
        cfg["WindowMode"] = 2
        json.dump(cfg, open(CFG, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
        p = subprocess.Popen([EXE])
        time.sleep(6)
        sample("sidebar", results, 2)

        # 6) 静置 30 秒（当前侧栏）
        sample("sidebar idle 30s", results, 30)
    finally:
        subprocess.run(["taskkill", "/F", "/IM", "DeskLanes.exe"], capture_output=True)
        open(CFG, "w", encoding="utf-8").write(original)

    out = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                       "..", "docs", "perf-scenarios.json")
    json.dump(results, open(out, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
    print("written:", os.path.abspath(out))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
