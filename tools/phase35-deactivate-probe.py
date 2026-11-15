#!/usr/bin/env python3
"""阶段 35：用**用户真实配置**验证失焦自动收起。

阶段 34 的探针把 FloatingHideOnDeactivate / SidebarAutoCollapse 强制改成了 False，
所以 19/19 全过却没覆盖用户真实配置：勾着「失焦自动隐藏」时，
旧逻辑会把悬浮窗整个隐藏、侧边栏只折成 44px 窄条，看不到小球/半透明边栏。

本探针只临时改 WindowMode，其余设置原样保留，验证：
启动 → 失焦 → 原窗口隐藏 + 对应指示器出现 → 悬停/点击展开。
零第三方依赖。用法：python tools/phase35-deactivate-probe.py
"""

import ctypes
import json
import os
import subprocess
import sys
import time
from ctypes import wintypes

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from screenshot import _set_dpi_aware  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(REPO, "publish", "win-x64-selfcontained", "DeskLanes.exe")
CFG = os.path.join(os.environ.get("LOCALAPPDATA", ""), "DeskLanes", "settings.json")
STRIP = "DeskLanes 边缘细线"
MODES = [
    ("floating", 0, "DeskLanes 快速启动"),
    ("sidebar", 2, "DeskLanes 侧边栏"),
]

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


def move_to(x, y, pause=0.6):
    u.SetCursorPos(int(x), int(y))
    time.sleep(pause)


def click_at(x, y):
    move_to(x, y, 0.3)
    u.mouse_event(0x0002, 0, 0, 0, 0)
    time.sleep(0.1)
    u.mouse_event(0x0004, 0, 0, 0, 0)
    time.sleep(0.6)


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
    # 必须先声明 DPI 感知，否则 GetWindowRect 返回 125% 虚拟化坐标
    # （113px 小球会被读成 90px），尺寸断言就失去意义。
    _set_dpi_aware()
    if not os.path.isfile(EXE) or not os.path.isfile(CFG):
        print("exe 或 settings.json 不存在")
        return 1

    with open(CFG, "r", encoding="utf-8") as f:
        original = f.read()
    s = json.loads(original)
    print("用户真实配置：",
          "FloatingHideOnDeactivate=", s.get("FloatingHideOnDeactivate"),
          "SidebarAutoCollapse=", s.get("SidebarAutoCollapse"),
          "CollapseOnLaunch=", s.get("CollapseOnLaunch"))
    kill()

    try:
        for label, mode, title in MODES:
            # 只改 WindowMode，其余真实设置原样保留
            cfg = json.loads(original)
            cfg["WindowMode"] = mode
            with open(CFG, "w", encoding="utf-8") as f:
                json.dump(cfg, f, ensure_ascii=False, indent=2)

            proc = subprocess.Popen([EXE])
            if not wait_window(title):
                say("%s: window appears at startup" % label, False)
                kill()
                continue
            say("%s: window appears at startup" % label, True)
            time.sleep(1.5)

            # 失焦：把前台切到任务栏
            tray = u.FindWindowW("Shell_TrayWnd", None)
            if tray:
                force_foreground(tray)
            time.sleep(1.8)
            say("%s: focus loss hides window and shows indicator" % label,
                not visible(title) and visible(STRIP))

            r = rect(STRIP)
            if not r:
                say("%s: indicator has a rect" % label, False)
                kill()
                continue
            x, y, w, h = r

            if label == "sidebar":
                say("sidebar: indicator is the 0.5cm bar form",
                    w >= 19 and h > 500, "rect=%dx%d" % (w, h))
                move_to(x + 8, y + min(300, h // 2), 1.2)
                say("sidebar: hovering bar restores sidebar", visible(title))
            else:
                say("floating: indicator is the 3cm ball form",
                    abs(w - 113) <= 8 and abs(h - 113) <= 8, "rect=%dx%d" % (w, h))
                move_to(x + w // 2, y + h // 2, 0.8)
                say("floating: hovering ball does NOT auto-expand", not visible(title))
                click_at(x + w // 2, y + h // 2)
                time.sleep(1.4)
                say("floating: clicking ball restores floating window", visible(title))

            kill()
    finally:
        kill()
        with open(CFG, "w", encoding="utf-8") as f:
            f.write(original)

    passed = sum(1 for _, ok in results if ok)
    print("\n===== phase35 deactivate probe: passed %d, failed %d =====" %
          (passed, len(results) - passed))
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
