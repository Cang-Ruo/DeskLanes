#!/usr/bin/env python3
"""阶段 37：收起小球 A「细环玻璃球」的三态取证。

用真实 exe 抓静止 / 悬停 / 拖动中三张 GDI 截图，
比较同一小球区域的像素差，确认三态视觉确实不同；
同时回归小球拖动、位置落盘、点击展开仍然正常。
零第三方依赖。用法：python tools/phase37-ball-style-probe.py
"""

import ctypes
import json
import os
import subprocess
import sys
import time
from ctypes import wintypes

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from screenshot import capture, read_png, _set_dpi_aware  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(REPO, "publish", "win-x64-selfcontained", "DeskLanes.exe")
CFG = os.path.join(os.environ.get("LOCALAPPDATA", ""), "DeskLanes", "settings.json")
OUT = os.path.join(REPO, "docs", "ui-acrylic-final")
STRIP = "DeskLanes 边缘细线"
FLOATING = "DeskLanes 快速启动"
BALL = 113
VK_ESCAPE, KEYUP = 0x1B, 0x0002
LEFTDOWN, LEFTUP = 0x0002, 0x0004

u = ctypes.windll.user32
k = ctypes.windll.kernel32
u.SetForegroundWindow.argtypes = [ctypes.c_void_p]
u.GetForegroundWindow.restype = ctypes.c_void_p
u.GetWindowThreadProcessId.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
u.GetWindowThreadProcessId.restype = wintypes.DWORD
u.AttachThreadInput.argtypes = [wintypes.DWORD, wintypes.DWORD, wintypes.BOOL]
k.GetCurrentThreadId.restype = wintypes.DWORD
EnumProc = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
results = []


def say(tag, ok, detail=""):
    results.append((tag, bool(ok)))
    print("%-6s %s%s" % ("PASS" if ok else "FAIL", tag, (" -- " + detail) if detail else ""))


def find(title):
    out = []

    @EnumProc
    def cb(h, _):
        n = u.GetWindowTextLengthW(h)
        if n:
            b = ctypes.create_unicode_buffer(n + 1)
            u.GetWindowTextW(h, b, n + 1)
            if b.value == title:
                out.append(h)
        return True

    u.EnumWindows(cb, 0)
    return out[0] if out else 0


def visible(title):
    h = find(title)
    return bool(h) and bool(u.IsWindowVisible(ctypes.c_void_p(h)))


def rect(title):
    h = find(title)
    r = wintypes.RECT()
    if not h or not u.GetWindowRect(ctypes.c_void_p(h), ctypes.byref(r)):
        return None
    return r.left, r.top, r.right - r.left, r.bottom - r.top


def force(hwnd):
    me = k.GetCurrentThreadId()
    fg = u.GetForegroundWindow()
    att = []
    for h in (fg, hwnd):
        if h:
            t = u.GetWindowThreadProcessId(ctypes.c_void_p(h), None)
            if t and t != me and u.AttachThreadInput(me, t, True):
                att.append(t)
    u.BringWindowToTop(ctypes.c_void_p(hwnd))
    u.ShowWindow(ctypes.c_void_p(hwnd), 5)
    u.SetForegroundWindow(ctypes.c_void_p(hwnd))
    for t in att:
        u.AttachThreadInput(me, t, False)
    time.sleep(0.4)
    return u.GetForegroundWindow() == hwnd


def esc(title):
    h = find(title)
    if not h:
        return False
    force(h)
    time.sleep(0.2)
    u.keybd_event(VK_ESCAPE, 0, 0, 0)
    u.keybd_event(VK_ESCAPE, 0, KEYUP, 0)
    return True


def move(x, y, pause=0.7):
    u.SetCursorPos(int(x), int(y))
    time.sleep(pause)


def click(x, y):
    move(x, y, 0.3)
    u.mouse_event(LEFTDOWN, 0, 0, 0, 0)
    time.sleep(0.1)
    u.mouse_event(LEFTUP, 0, 0, 0, 0)
    time.sleep(0.7)


def kill():
    subprocess.run(["taskkill", "/F", "/IM", "DeskLanes.exe"],
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def wait(title, timeout=20):
    for _ in range(int(timeout / 0.4)):
        if visible(title):
            return True
        time.sleep(0.4)
    return False


def mean_diff(a, b):
    wa, ha, ra = read_png(a)
    wb, hb, rb = read_png(b)
    if (wa, ha) != (wb, hb):
        return None
    total, n = 0, 0
    for i in range(0, len(ra), 3):
        total += abs(ra[i] - rb[i]) + abs(ra[i + 1] - rb[i + 1]) + abs(ra[i + 2] - rb[i + 2])
        n += 3
    return round(total / max(n, 1), 2)


def main():
    _set_dpi_aware()
    os.makedirs(OUT, exist_ok=True)
    with open(CFG, "r", encoding="utf-8") as f:
        original = f.read()
    cfg = json.loads(original)
    cfg["WindowMode"] = 0
    cfg["CollapseOnLaunch"] = True
    cfg["FloatingHideOnDeactivate"] = False
    with open(CFG, "w", encoding="utf-8") as f:
        json.dump(cfg, f, ensure_ascii=False, indent=2)
    kill()

    try:
        proc = subprocess.Popen([EXE])
        if not wait(FLOATING):
            say("floating window appears", False)
            kill()
            return 1
        say("floating window appears", True)
        time.sleep(1.5)
        esc(FLOATING)
        time.sleep(1.4)
        say("Esc collapses to ball", not visible(FLOATING) and visible(STRIP))

        r = rect(STRIP)
        if not r:
            say("ball has a rect", False)
            kill()
            return 1
        x, y, w, h = r
        say("ball is 3cm round", abs(w - BALL) <= 6 and abs(h - BALL) <= 6,
            "%dx%d" % (w, h))

        idle = os.path.join(OUT, "phase37-ball-idle.png")
        hover = os.path.join(OUT, "phase37-ball-hover.png")
        dragging = os.path.join(OUT, "phase37-ball-dragging.png")

        # 静止：鼠标远离小球
        move(x + w / 2 + 320, y + h / 2 + 200, 0.9)
        capture(x, y, w, h, idle)

        # 悬停：鼠标移到球心
        move(x + w / 2, y + h / 2, 0.9)
        capture(x, y, w, h, hover)

        # 拖动中：按住并移动，但先不松开
        cx, cy = x + w / 2, y + h / 2
        u.mouse_event(LEFTDOWN, 0, 0, 0, 0)
        time.sleep(0.15)
        for i in range(1, 9):
            u.SetCursorPos(int(cx + 30 * i / 8), int(cy + 18 * i / 8))
            time.sleep(0.05)
        time.sleep(0.5)
        rd = rect(STRIP)
        if rd:
            capture(rd[0], rd[1], rd[2], rd[3], dragging)
        u.mouse_event(LEFTUP, 0, 0, 0, 0)
        time.sleep(0.8)

        d_idle_hover = mean_diff(idle, hover)
        d_hover_drag = mean_diff(hover, dragging) if rd else None
        say("three states are visually distinct (idle vs hover)",
            d_idle_hover is not None and d_idle_hover > 1.0,
            "pixel diff=%s" % d_idle_hover)
        say("three states are visually distinct (hover vs dragging)",
            d_hover_drag is not None and d_hover_drag > 1.0,
            "pixel diff=%s" % d_hover_drag)

        say("dragging does NOT expand the window", not visible(FLOATING))
        cfg2 = json.load(open(CFG, encoding="utf-8"))
        say("drag position still saved",
            cfg2.get("CollapseBallLeft") is not None and cfg2.get("CollapseBallTop") is not None)

        if rd:
            click(rd[0] + rd[2] / 2, rd[1] + rd[3] / 2)
        say("clicking ball restores floating window", visible(FLOATING))
        kill()
    finally:
        kill()
        restored = json.loads(original)
        restored.pop("CollapseBallLeft", None)
        restored.pop("CollapseBallTop", None)
        with open(CFG, "w", encoding="utf-8") as f:
            json.dump(restored, f, ensure_ascii=False, indent=2)

    passed = sum(1 for _, ok in results if ok)
    print("\n===== phase37 ball style probe: passed %d, failed %d =====" %
          (passed, len(results) - passed))
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
