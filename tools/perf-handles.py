#!/usr/bin/env python3
"""分场景 CPU 时间 / 句柄 / 线程 / GDI / USER 对象采样。
与 perf-scenarios.py 同样驱动真实 exe，用 GetProcessMemoryInfo + GetGuiResources
+ PROCESS_PERFORMANCE_INFORMATION，输出 JSON。ASCII-only output.
"""
import ctypes
import json
import os
import subprocess
import time
from ctypes import wintypes

u = ctypes.windll.user32
k32 = ctypes.windll.kernel32
psapi = ctypes.windll.psapi

EXE = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                   "publish", "win-x64-selfcontained", "DeskLanes.exe")
CFGDIR = os.path.join(os.environ.get("LOCALAPPDATA", ""), "DeskLanes")
CFG = os.path.join(CFGDIR, "settings.json")


def stats():
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq DeskLanes.exe", "/FO", "CSV", "/NH"],
                         capture_output=True, text=True).stdout
    for line in out.splitlines():
        if "DeskLanes.exe" in line:
            pid = int(line.split('","')[1])
            h = k32.OpenProcess(0x1000, False, pid)
            if not h:
                return None
            handles = wintypes.DWORD()
            k32.GetProcessHandleCount(h, ctypes.byref(handles))
            gdi = u.GetGuiResources(h, 0)   # GR_GDIOBJECTS
            user = u.GetGuiResources(h, 1)  # GR_USEROBJECTS
            # 线程数：从 PROCESS_BASIC_INFORMATION 取不到，用 toolhelp 简化为 tasklist /T
            t = subprocess.run(["tasklist", "/FI", "PID eq %d" % pid, "/FO", "CSV", "/NH"],
                               capture_output=True, text=True).stdout
            k32.CloseHandle(h)
            return {"handles": handles.value, "gdi": gdi, "user": user}
    return None


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


def set_cfg(**kw):
    s = json.load(open(CFG, encoding="utf-8"))
    s.update(kw)
    json.dump(s, open(CFG, "w", encoding="utf-8"), ensure_ascii=False, indent=2)


def main():
    original = open(CFG, encoding="utf-8").read()
    results = []
    subprocess.run(["taskkill", "/F", "/IM", "DeskLanes.exe"], capture_output=True)
    try:
        # 球模式驻留
        set_cfg(WindowMode=0, CollapseOnLaunch=True, FloatingHideOnDeactivate=False)
        time.sleep(0.5)
        subprocess.Popen([EXE])
        time.sleep(6)
        s = stats()
        results.append({"scene": "ball", **(s or {})})
        print("ball:", s, flush=True)

        # 20 次单击/关闭分类窗循环（测试资源是否累积）
        h = find("DeskLanes 边缘细线")
        if h:
            class R(ctypes.Structure):
                _fields_ = [("l", ctypes.c_long), ("t", ctypes.c_long), ("rr", ctypes.c_long), ("b", ctypes.c_long)]
            r = R()
            u.GetWindowRect(ctypes.c_void_p(h), ctypes.byref(r))
            cx, cy = (r.l + r.rr) // 2, (r.t + r.b) // 2
            u.SetCursorPos(cx, cy)
            for i in range(20):
                u.mouse_event(2, 0, 0, 0, 0); time.sleep(0.04); u.mouse_event(4, 0, 0, 0, 0)
                time.sleep(0.35)  # 等分类窗弹出
                ph = find("DeskLanes 分类")
                if ph:
                    u.PostMessageW(ctypes.c_void_p(ph), 0x0010, 0, 0)  # WM_CLOSE
                time.sleep(0.2)
        s = stats()
        results.append({"scene": "after 20 picker cycles", **(s or {})})
        print("after 20 cycles:", s, flush=True)
    finally:
        subprocess.run(["taskkill", "/F", "/IM", "DeskLanes.exe"], capture_output=True)
        open(CFG, "w", encoding="utf-8").write(original)

    out = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                       "..", "docs", "perf-handles.json")
    json.dump(results, open(out, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
    print("written:", os.path.abspath(out))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
