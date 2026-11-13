#!/usr/bin/env python3
"""阶段 31：收束 / 展开功能探针（真实 exe + 合成键鼠，零第三方依赖）。

用 Esc 触发收束（与「双击启动软件」走同一个 CollapseToEdge 入口），验证：

  Full    : Esc 收束 → 主窗口隐藏 + 左侧细线出现
            悬停细线不展开（必须点击）→ 点击后主窗口回来
  Sidebar : Esc 收束 → 侧边栏隐藏 + 细线出现
            悬停细线即展开 → 鼠标移开自动收束

配置只临时改 %LocalAppData%\\DeskLanes\\settings.json，结束一定还原。
用法：python tools/phase31-collapse-probe.py
"""

import ctypes
import json
import os
import subprocess
import sys
import time
from ctypes import wintypes

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from screenshot import capture  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(REPO, "publish", "win-x64-selfcontained", "DeskLanes.exe")
CFGDIR = os.path.join(os.environ.get("LOCALAPPDATA", ""), "DeskLanes")
SETTINGS = os.path.join(CFGDIR, "settings.json")
SHOTS = os.path.join(REPO, "docs", "ui-acrylic-final")
STRIP_PNG = os.path.join(SHOTS, "phase31-edge-strip.png")
TASKBAR_PNG = os.path.join(SHOTS, "phase31-taskbar-visible.png")

MAIN_TITLE = "DeskLanes"
SIDEBAR_TITLE = "DeskLanes 侧边栏"
STRIP_TITLE = "DeskLanes 边缘细线"
MODE_FULL, MODE_SIDEBAR = 1, 2

u = ctypes.windll.user32
u.GetWindowRect.argtypes = [ctypes.c_void_p, ctypes.POINTER(wintypes.RECT)]
u.SetCursorPos.argtypes = [ctypes.c_int, ctypes.c_int]
u.SetForegroundWindow.argtypes = [ctypes.c_void_p]
VK_ESCAPE, KEYEVENTF_KEYUP = 0x1B, 0x0002
MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP = 0x0002, 0x0004

EnumProc = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
results = []


def say(tag, ok, detail=""):
    results.append((tag, bool(ok)))
    print("%-6s %s%s" % ("PASS" if ok else "FAIL", tag, (" -- " + detail) if detail else ""))


def find_window(title):
    found = []

    @EnumProc
    def cb(hwnd, _):
        n = u.GetWindowTextLengthW(hwnd)
        if n > 0:
            buf = ctypes.create_unicode_buffer(n + 1)
            u.GetWindowTextW(hwnd, buf, n + 1)
            if buf.value == title:
                found.append(hwnd)
        return True

    u.EnumWindows(cb, 0)
    return found[0] if found else 0


def visible(title):
    hwnd = find_window(title)
    return bool(hwnd) and bool(u.IsWindowVisible(ctypes.c_void_p(hwnd)))


def rect(title):
    hwnd = find_window(title)
    r = wintypes.RECT()
    if not hwnd or not u.GetWindowRect(ctypes.c_void_p(hwnd), ctypes.byref(r)):
        return None
    return r.left, r.top, r.right - r.left, r.bottom - r.top


def press_esc(title):
    hwnd = find_window(title)
    if not hwnd:
        return False
    u.SetForegroundWindow(ctypes.c_void_p(hwnd))
    time.sleep(0.4)
    u.keybd_event(VK_ESCAPE, 0, 0, 0)
    u.keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, 0)
    return True


def move_to(x, y):
    u.SetCursorPos(int(x), int(y))
    time.sleep(0.35)


def click_at(x, y):
    move_to(x, y)
    u.mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0)
    time.sleep(0.1)
    u.mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0)
    time.sleep(0.4)


def wait_window(title, timeout=20):
    for _ in range(int(timeout / 0.4)):
        if visible(title):
            return True
        time.sleep(0.4)
    return False


def patch(mode):
    with open(SETTINGS, "r", encoding="utf-8") as f:
        s = json.load(f)
    s["WindowMode"] = mode
    s["CollapseOnLaunch"] = True
    s["FloatingTopmost"] = False
    s["SidebarAutoCollapse"] = False
    with open(SETTINGS, "w", encoding="utf-8") as f:
        json.dump(s, f, ensure_ascii=False, indent=2)


def start_app():
    return subprocess.Popen([EXE])


def stop_app(proc):
    try:
        proc.terminate()
        proc.wait(timeout=8)
    except Exception:
        try:
            proc.kill()
        except Exception:
            pass


def center_of_strip():
    r = rect(STRIP_TITLE)
    if not r:
        return None
    x, y, w, h = r
    return x + max(2, w / 2), y + h / 2


def main():
    if not os.path.isfile(SETTINGS) or not os.path.isfile(EXE):
        print(json.dumps({"error": "settings.json 或已发布 exe 不存在"}))
        return 1

    with open(SETTINGS, "r", encoding="utf-8") as f:
        original = f.read()
    subprocess.run(["taskkill", "/F", "/IM", "DeskLanes.exe"],
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    try:
        # ---------------- 全屏：Esc 收束 / 悬停不展开 / 点击展开 ----------------
        patch(MODE_FULL)
        proc = start_app()
        if not wait_window(MAIN_TITLE):
            say("full: main window appears", False)
            stop_app(proc)
            return 1
        say("full: main window appears", True)
        time.sleep(1.5)
        press_esc(MAIN_TITLE)
        time.sleep(1.2)
        say("full: Esc collapses to left strip",
            not visible(MAIN_TITLE) and visible(STRIP_TITLE))

        # 收束态取证：细线 + 左右各留一点背景，能看出它有多"不明显"
        sr = rect(STRIP_TITLE)
        if sr:
            capture(max(0, sr[0] - 8), sr[1], sr[2] + 16,
                    min(sr[3], 360), STRIP_PNG)

        c = center_of_strip()
        if c:
            move_to(c[0], c[1])
            time.sleep(1.0)
            say("full: hovering strip does NOT expand (needs click)",
                not visible(MAIN_TITLE))
            click_at(c[0], c[1])
            time.sleep(1.6)
            say("full: clicking strip restores workspace",
                visible(MAIN_TITLE) and not visible(STRIP_TITLE))
            # 屏幕底部取证：工作空间下沿 + 露出来的任务栏
            sw = u.GetSystemMetrics(0)
            sh = u.GetSystemMetrics(1)
            capture(0, sh - 160, sw, 160, TASKBAR_PNG)
        else:
            say("full: strip has a clickable rect", False)
        stop_app(proc)

        # ---------------- 侧边栏：Esc 收束 / 悬停展开 / 移开收束 ----------------
        patch(MODE_SIDEBAR)
        proc2 = start_app()
        if not wait_window(SIDEBAR_TITLE):
            say("sidebar: window appears", False)
            stop_app(proc2)
            return 1
        say("sidebar: window appears", True)
        time.sleep(1.5)
        press_esc(SIDEBAR_TITLE)
        time.sleep(1.2)
        say("sidebar: Esc collapses to left strip",
            not visible(SIDEBAR_TITLE) and visible(STRIP_TITLE))

        c2 = center_of_strip()
        if c2:
            move_to(c2[0], c2[1])
            time.sleep(1.4)
            say("sidebar: hovering strip expands (no click)",
                visible(SIDEBAR_TITLE))
            # 移到主屏中央，离开侧边栏区域
            px = u.GetSystemMetrics(0) // 2
            py = u.GetSystemMetrics(1) // 2
            move_to(px, py)
            time.sleep(1.4)
            say("sidebar: moving away collapses again",
                not visible(SIDEBAR_TITLE) and visible(STRIP_TITLE))
        else:
            say("sidebar: strip has a hover rect", False)
        stop_app(proc2)
    finally:
        subprocess.run(["taskkill", "/F", "/IM", "DeskLanes.exe"],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        with open(SETTINGS, "w", encoding="utf-8") as f:
            f.write(original)

    passed = sum(1 for _, ok in results if ok)
    print("\n===== collapse probe: passed %d, failed %d =====" %
          (passed, len(results) - passed))
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
