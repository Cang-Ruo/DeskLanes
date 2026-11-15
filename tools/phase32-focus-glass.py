#!/usr/bin/env python3
"""阶段 32：三种窗口模式「有焦点 / 无焦点」玻璃一致性取证。

阶段 30 只验证了侧边栏；阶段 32 把全屏与悬浮窗也改成分层窗口
（WPF 自己合成透明像素，DWM backdrop 关成 NONE），因此三个窗口都应当
在焦点切换时保持逐像素一致。

用 UiSnapshot 起临时窗口（不碰真实配置），分别在有焦点 / 无焦点时做
GDI 屏幕合成截图，比较同一矩形的平均像素差，并保存证据图。
零第三方依赖。用法：python tools/phase32-focus-glass.py
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
MODES = [
    ("full", "panels", "DeskLanes"),
    ("sidebar", "sidebar", "DeskLanes 侧边栏"),
    ("floating", "floating", "DeskLanes 快速启动"),
]

u = ctypes.windll.user32
k = ctypes.windll.kernel32
u.SetForegroundWindow.argtypes = [ctypes.c_void_p]
u.GetForegroundWindow.restype = ctypes.c_void_p
u.GetWindowThreadProcessId.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
u.GetWindowThreadProcessId.restype = wintypes.DWORD
u.AttachThreadInput.argtypes = [wintypes.DWORD, wintypes.DWORD, wintypes.BOOL]
k.GetCurrentThreadId.restype = wintypes.DWORD


def force_foreground(hwnd):
    """前台锁会挡住 SetForegroundWindow；挂接当前线程与前台线程后再抢。"""
    fg = u.GetForegroundWindow()
    tid_fg = u.GetWindowThreadProcessId(ctypes.c_void_p(fg), None) if fg else 0
    tid_target = u.GetWindowThreadProcessId(ctypes.c_void_p(hwnd), None)
    tid_me = k.GetCurrentThreadId()
    attached = []
    for tid in (tid_fg, tid_target):
        if tid and tid != tid_me and u.AttachThreadInput(tid_me, tid, True):
            attached.append(tid)
    u.BringWindowToTop(ctypes.c_void_p(hwnd))
    u.ShowWindow(ctypes.c_void_p(hwnd), 5)  # SW_SHOW
    u.SetForegroundWindow(ctypes.c_void_p(hwnd))
    for tid in attached:
        u.AttachThreadInput(tid_me, tid, False)
    time.sleep(0.5)
    return u.GetForegroundWindow() == hwnd


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

    result = []
    for label, mode, title in MODES:
        focused = os.path.join(OUT, "phase32-%s-focused.png" % label)
        unfocused = os.path.join(OUT, "phase32-%s-unfocused.png" % label)
        snap = os.path.join(OUT, "phase32-snap-tmp.png")
        proc = subprocess.Popen([SNAP, mode, snap, "--demo", "--hold=15000"])
        hwnd = 0
        for _ in range(40):
            time.sleep(0.4)
            hwnd = find_window_by_title(title)
            if hwnd:
                break

        if not hwnd:
            result.append({"mode": label, "error": "窗口没出现"})
            proc.terminate()
            continue

        time.sleep(1.2)
        is_focused = force_foreground(hwnd)
        time.sleep(1.0)
        x, y, w, h = rect(hwnd)
        capture(x, y, w, h, focused)

        tray = u.FindWindowW("Shell_TrayWnd", None)
        if tray:
            force_foreground(tray)
        time.sleep(1.4)
        lost = u.GetForegroundWindow() != hwnd
        # 阶段 31 的「失焦沉到焦点软件后面」会让 GDI 截图拍到别的窗口，
        # 那测的是 z 序而不是透明度。这里只把它拉回可见层且 SWP_NOACTIVATE，
        # 保持失焦状态不变，才能单独验证玻璃是否随焦点改变。
        u.SetWindowPos(ctypes.c_void_p(hwnd), ctypes.c_void_p(-1), 0, 0, 0, 0,
                       0x0001 | 0x0002 | 0x0010)  # NOSIZE|NOMOVE|NOACTIVATE
        time.sleep(0.6)
        lost = lost and u.GetForegroundWindow() != hwnd
        capture(x, y, w, h, unfocused)

        d = mean_diff(focused, unfocused)
        # 全屏游戏 / 独占输入窗口会抢走前台，此时 GDI 截到的是别的窗口，
        # 像素差毫无意义。把无效截图删掉，绝不留成「证据」。
        valid = bool(is_focused) and bool(lost)
        if not valid:
            for path in (focused, unfocused):
                if os.path.isfile(path):
                    try:
                        os.remove(path)
                    except OSError:
                        pass
            d = None

        result.append({
            "mode": label, "rect": [x, y, w, h],
            "focused_ok": bool(is_focused), "focus_lost_ok": bool(lost),
            "mean_pixel_diff": d,
            "verdict": ("两态一致" if d is not None and d < 8
                        else "差异偏大，需人工看图" if valid
                        else "无效：前台被其他窗口占用（全屏游戏等）"),
            "shots": [focused, unfocused] if valid else [],
        })
        try:
            proc.terminate()
        except Exception:
            pass
        if os.path.isfile(snap):
            try:
                os.remove(snap)
            except OSError:
                pass

    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0 if all(r.get("mean_pixel_diff") is not None and r["mean_pixel_diff"] < 8
                    for r in result) else 1


if __name__ == "__main__":
    sys.exit(main())
