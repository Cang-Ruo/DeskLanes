#!/usr/bin/env python3
"""阶段 34：三种收束指示器的真实窗口 / 交互探针。

· 侧边栏：0.5cm 半透明边栏（≈19px），悬停即展开、移开自动收束
· 悬浮窗：3cm 半透明小球（≈113px），贴左边缘中部，点击展开
· 全屏  ：热区 ≈38px、可见窄条 0.5cm 默认隐藏；鼠标靠近左边界才弹出，点击展开

用真实 exe + 合成键鼠，配置临时修改后一定还原。零第三方依赖。
用法：python tools/phase34-collapse-probe.py
"""

import ctypes
import json
import os
import shutil
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
MODES = [
    ("sidebar", 2, "DeskLanes 侧边栏"),
    ("floating", 0, "DeskLanes 快速启动"),
    ("full", 1, "DeskLanes"),
]
BAR, BALL, HOT = 19, 113, 38
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


class MONITORINFO(ctypes.Structure):
    _fields_ = [("cbSize", wintypes.DWORD), ("rcMonitor", wintypes.RECT),
                ("rcWork", wintypes.RECT), ("dwFlags", wintypes.DWORD)]


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


def work_area():
    r = wintypes.RECT()
    # SPI_GETWORKAREA
    if not u.SystemParametersInfoW(0x0030, 0, ctypes.byref(r), 0):
        return 0, 0, u.GetSystemMetrics(0), u.GetSystemMetrics(1)
    return r.left, r.top, r.right - r.left, r.bottom - r.top


def work_area_of(hwnd):
    """窗口实际所在那块显示器的工作区（侧边栏可能停靠在左侧竖屏）。"""
    u.MonitorFromWindow.argtypes = [ctypes.c_void_p, wintypes.DWORD]
    u.MonitorFromWindow.restype = ctypes.c_void_p
    u.GetMonitorInfoW.argtypes = [ctypes.c_void_p, ctypes.POINTER(MONITORINFO)]
    u.GetMonitorInfoW.restype = wintypes.BOOL
    mon = u.MonitorFromWindow(ctypes.c_void_p(hwnd), 2)  # MONITOR_DEFAULTTONEAREST
    mi = MONITORINFO()
    mi.cbSize = ctypes.sizeof(MONITORINFO)
    if mon and u.GetMonitorInfoW(ctypes.c_void_p(mon), ctypes.byref(mi)):
        r = mi.rcWork
        return r.left, r.top, r.right - r.left, r.bottom - r.top
    return work_area()


def force_foreground(hwnd):
    me = k.GetCurrentThreadId()
    fg = u.GetForegroundWindow()
    attached = []
    for h in (fg, hwnd):
        if h:
            t = u.GetWindowThreadProcessId(ctypes.c_void_p(h), None)
            if t and t != me and u.AttachThreadInput(me, t, True):
                attached.append(t)
    u.BringWindowToTop(ctypes.c_void_p(hwnd))
    u.ShowWindow(ctypes.c_void_p(hwnd), 5)
    u.SetForegroundWindow(ctypes.c_void_p(hwnd))
    for t in attached:
        u.AttachThreadInput(me, t, False)
    time.sleep(0.4)
    return u.GetForegroundWindow() == hwnd


def press_esc(title):
    h = find(title)
    if not h:
        return False
    force_foreground(h)
    time.sleep(0.2)
    u.keybd_event(VK_ESCAPE, 0, 0, 0)
    u.keybd_event(VK_ESCAPE, 0, KEYUP, 0)
    return True


def move_to(x, y, pause=0.6):
    u.SetCursorPos(int(x), int(y))
    time.sleep(pause)


def click_at(x, y):
    move_to(x, y, 0.3)
    u.mouse_event(LEFTDOWN, 0, 0, 0, 0)
    time.sleep(0.1)
    u.mouse_event(LEFTUP, 0, 0, 0, 0)
    time.sleep(0.6)


def mean_diff(a, b):
    wa, ha, ra = read_png(a)
    wb, hb, rb = read_png(b)
    if (wa, ha) != (wb, hb):
        return None
    total, n = 0, 0
    for i in range(0, len(ra), 3 * 3):
        total += abs(ra[i] - rb[i]) + abs(ra[i + 1] - rb[i + 1]) + abs(ra[i + 2] - rb[i + 2])
        n += 3
    return round(total / max(n, 1), 2)


def patch(mode):
    with open(CFG, "r", encoding="utf-8") as f:
        s = json.load(f)
    s["WindowMode"] = mode
    s["CollapseOnLaunch"] = True
    s["FloatingTopmost"] = False
    s["FloatingHideOnDeactivate"] = False
    s["SidebarAutoCollapse"] = False
    with open(CFG, "w", encoding="utf-8") as f:
        json.dump(s, f, ensure_ascii=False, indent=2)


def kill():
    subprocess.run(["taskkill", "/F", "/IM", "DeskLanes.exe"],
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def wait_window(title, timeout=20):
    for _ in range(int(timeout / 0.4)):
        if visible(title):
            return True
        time.sleep(0.4)
    return False


def main():
    _set_dpi_aware()
    os.makedirs(OUT, exist_ok=True)
    if not os.path.isfile(EXE) or not os.path.isfile(CFG):
        print(json.dumps({"error": "exe 或 settings.json 不存在"}))
        return 1

    with open(CFG, "r", encoding="utf-8") as f:
        original = f.read()
    wx, wy, ww, wh = work_area()
    kill()

    try:
        for label, mode, title in MODES:
            patch(mode)
            move_to(wx + ww // 2, wy + wh // 2, 0.2)  # 鼠标放主屏，避免落错屏
            proc = subprocess.Popen([EXE])
            if not wait_window(title):
                say("%s: window appears" % label, False)
                kill()
                continue
            say("%s: window appears" % label, True)
            time.sleep(1.5)
            press_esc(title)
            time.sleep(1.4)
            say("%s: Esc collapses to indicator" % label,
                not visible(title) and visible(STRIP))

            r = rect(STRIP)
            if not r:
                say("%s: indicator has a rect" % label, False)
                kill()
                continue
            x, y, w, h = r
            mx, my, mw, mh = work_area_of(find(STRIP))

            if label == "sidebar":
                # 150% DPI 竖屏上 GetWindowRect 含系统边框（实测 46px vs 主屏 38px），
                # 所以不拿窗口 rect 硬卡 38；可见玻璃宽度由 Bar.Width = 19px/scale
                # 固定（CollapseVisualPolicy.BarPixels，冒烟已锁定）。
                # 这里验证「热区确实比 0.5cm 可见边栏宽、但不是整块窗口」。
                say("sidebar: 0.5cm bar with wider hover zone",
                    BAR <= w <= int(HOT * 1.3),
                    "window w=%d, visible bar 19px" % w)
                say("sidebar: indicator spans its monitor work area",
                    abs(h - mh) <= 60, "h=%d work=%d on monitor (%d,%d)" % (h, mh, mx, my))
                # 悬停展开
                move_to(x + 8, y + min(300, h // 2))
                time.sleep(1.2)
                say("sidebar: hovering bar expands (no click)", visible(title))
                move_to(wx + ww // 2, wy + wh // 2)
                time.sleep(1.5)
                say("sidebar: moving away collapses again",
                    not visible(title) and visible(STRIP))
                # 视觉证据：直接截指示器真实位置；半透明会透出后面的壁纸/图标，
                # 这正是用户日常看到的样子。取中段避开桌面图标最密的一列。
                capture(x, y + 600, 140, 300,
                        os.path.join(OUT, "phase34-sidebar-bar.png"))

            elif label == "floating":
                say("floating: ball is 3cm (≈113px) round",
                    abs(w - BALL) <= 6 and abs(h - BALL) <= 6, "%dx%d" % (w, h))
                say("floating: ball sits at left edge middle",
                    abs(x - mx) <= 4 and abs((y + h / 2) - (my + mh / 2)) <= 12,
                    "x=%d y=%d work=(%d,%d,%d,%d)" % (x, y, mx, my, mw, mh))
                # 悬停不应展开
                move_to(x + w // 2, y + h // 2, 1.0)
                say("floating: hovering ball does NOT expand", not visible(title))
                capture(max(0, x - 30), max(0, y - 30), BALL + 60, BALL + 60,
                        os.path.join(OUT, "phase34-floating-ball.png"))
                click_at(x + w // 2, y + h // 2)
                time.sleep(1.4)
                say("floating: clicking ball restores floating window", visible(title))

            else:  # full
                say("full: hidden-until-near hot zone is ≈38px wide",
                    abs(w - HOT) <= 4, "w=%d" % w)
                say("full: hot zone spans work area height", abs(h - mh) <= 60,
                    "h=%d work=%d" % (h, mh))
                # 远离边界时截左边缘
                shot_a = os.path.join(OUT, "phase34-full-hidden.png")
                shot_b = os.path.join(OUT, "phase34-full-strip.png")
                # 阶段 36：窄条改用了与窗口本体相同的深蓝玻璃，
                # 全 80px 区域平均会把 19px 的信号稀释掉。
                # 只比对窄条所在的那 0.5cm 区域，信号更集中、断言更严格。
                move_to(wx + ww // 2, wy + wh // 2, 0.8)
                capture(x, y + 180, BAR, 360, shot_a)
                # 靠近左边界 → 窄条弹出
                move_to(x + 10, y + 300, 1.0)
                capture(x, y + 180, BAR, 360, shot_b)
                d = mean_diff(shot_a, shot_b)
                say("full: strip appears only when cursor nears left edge",
                    d is not None and d > 6, "pixel diff=%s" % d)
                say("full: hovering does NOT expand workspace", not visible(title))
                click_at(x + 10, y + 300)
                time.sleep(1.6)
                say("full: clicking strip restores full workspace", visible(title))

            kill()
    finally:
        kill()
        with open(CFG, "w", encoding="utf-8") as f:
            f.write(original)

    passed = sum(1 for _, ok in results if ok)
    print("\n===== phase34 collapse probe: passed %d, failed %d =====" %
          (passed, len(results) - passed))
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
