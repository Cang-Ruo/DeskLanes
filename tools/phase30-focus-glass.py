#!/usr/bin/env python3
"""阶段 30：侧边栏「有焦点 / 无焦点」两态玻璃一致性取证。

用 UiSnapshot 起一个 ShowActivated=false 的侧边栏（临时配置、不碰真实设置），
先在非活动态截屏，再激活它截第二张，比较同一矩形的平均像素差。
阶段 30 起侧边栏改成分层窗口，背景由 WPF 自己合成，DWM 不再按焦点换底色，
因此两态应当几乎一致（差值主要来自搜索框光标等细节）。

零第三方依赖；截图走 tools/screenshot.py 的 GDI BitBlt，能看到真实桌面合成结果。
用法：python tools/phase30-focus-glass.py
"""

import ctypes
import json
import os
import subprocess
import sys
import time
from ctypes import wintypes

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from screenshot import capture, read_png, find_window_by_title, _set_dpi_aware  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SNAP = os.path.join(REPO, "tests", "DeskLanes.UiSnapshot", "bin", "Release",
                    "net10.0-windows", "DeskLanes.UiSnapshot.exe")
OUT = os.path.join(REPO, "docs", "ui-acrylic-final")
UNFOCUSED = os.path.join(OUT, "phase30-sidebar-unfocused.png")
FOCUSED = os.path.join(OUT, "phase30-sidebar-focused.png")

u = ctypes.windll.user32
u.SetForegroundWindow.argtypes = [ctypes.c_void_p]
u.GetForegroundWindow.restype = ctypes.c_void_p


def rect(hwnd):
    r = wintypes.RECT()
    u.GetWindowRect(ctypes.c_void_p(hwnd), ctypes.byref(r))
    return r.left, r.top, r.right - r.left, r.bottom - r.top


def mean_diff(a, b):
    wa, ha, ra = read_png(a)
    wb, hb, rb = read_png(b)
    if (wa, ha) != (wb, hb):
        return None
    total, n = 0, 0
    for i in range(0, len(ra), 3 * 5):
        total += abs(ra[i] - rb[i]) + abs(ra[i + 1] - rb[i + 1]) + abs(ra[i + 2] - rb[i + 2])
        n += 3
    return round(total / max(n, 1), 2)


def main():
    _set_dpi_aware()
    os.makedirs(OUT, exist_ok=True)
    if not os.path.isfile(SNAP):
        print(json.dumps({"error": "UiSnapshot.exe 不存在"}))
        return 1

    snap = os.path.join(OUT, "phase30-snapshot-tmp.png")
    proc = subprocess.Popen([SNAP, "sidebar", snap, "--demo", "--hold=15000"])
    hwnd = 0
    for _ in range(40):
        time.sleep(0.4)
        hwnd = find_window_by_title("DeskLanes 侧边栏")
        if hwnd:
            break
    if not hwnd:
        proc.terminate()
        print(json.dumps({"error": "临时侧栏窗口没出现"}))
        return 1

    time.sleep(1.5)
    x, y, w, h = rect(hwnd)
    was_unfocused = u.GetForegroundWindow() != hwnd
    capture(x, y, w, h, UNFOCUSED)

    u.SetForegroundWindow(ctypes.c_void_p(hwnd))
    time.sleep(1.8)
    is_focused = u.GetForegroundWindow() == hwnd
    capture(x, y, w, h, FOCUSED)

    d = mean_diff(UNFOCUSED, FOCUSED)
    print(json.dumps({
        "rect": [x, y, w, h],
        "first_state_unfocused": bool(was_unfocused),
        "second_state_focused": bool(is_focused),
        "mean_pixel_diff": d,
        "verdict": "两态背景一致" if d is not None and d < 8 else "差异偏大，需人工看图",
        "shots": [UNFOCUSED, FOCUSED],
    }, ensure_ascii=False, indent=2))
    try:
        proc.terminate()
    except Exception:
        pass
    if os.path.isfile(snap):
        try:
            os.remove(snap)
        except OSError:
            pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
