#!/usr/bin/env python3
"""阶段 38：DeskLanes Glass Orb 实机验收。

覆盖用户验收清单：
1. 普通点击能否打开
2. 长按拖动不会意外打开
3. 鼠标移出按钮后仍能完成拖动
4. 松开后位置保存正确
5. 重启后位置恢复
6. 多显示器边缘不会丢失（钳制在工作区内）
7. 不影响现有悬浮窗口与快捷键（Ctrl+Alt+Space）
8. 三态截图 + 拖动前后位置对比

零第三方依赖。用法：python tools/phase38-orb-probe.py
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
EXE = os.environ.get(
    "DESKLANES_EXE",
    os.path.join(REPO, "publish", "win-x64-selfcontained", "DeskLanes.exe"))
CFG = os.path.join(os.environ.get("LOCALAPPDATA", ""), "DeskLanes", "settings.json")
OUT = os.path.join(REPO, "docs", "ui-acrylic-final")
TAG = os.environ.get("SHOT_TAG", "phase38-orb")
BALL_X = float(os.environ.get("BALL_X", "300"))
BALL_Y = float(os.environ.get("BALL_Y", "300"))
STRIP = "DeskLanes 边缘细线"
FLOATING = "DeskLanes 快速启动"
VK_ESCAPE, KEYUP = 0x1B, 0x0002
LEFTDOWN, LEFTUP = 0x0002, 0x0004
VK_CONTROL, VK_MENU, VK_SPACE = 0x11, 0x12, 0x20

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


def dpi_of(hwnd):
    u.GetDpiForWindow.argtypes = [ctypes.c_void_p]
    u.GetDpiForWindow.restype = wintypes.UINT
    try:
        return u.GetDpiForWindow(ctypes.c_void_p(hwnd))
    except Exception:
        return 120


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


def esc(title):
    h = find(title)
    if not h:
        return False
    force(h)
    time.sleep(0.2)
    u.keybd_event(VK_ESCAPE, 0, 0, 0)
    u.keybd_event(VK_ESCAPE, 0, KEYUP, 0)
    return True


def move(x, y, pause=0.6):
    u.SetCursorPos(int(x), int(y))
    time.sleep(pause)


def click(x, y):
    move(x, y, 0.3)
    u.mouse_event(LEFTDOWN, 0, 0, 0, 0)
    time.sleep(0.08)
    u.mouse_event(LEFTUP, 0, 0, 0, 0)
    time.sleep(0.8)


def press_drag(x0, y0, dx, dy, hold=0.45):
    move(x0, y0, 0.3)
    u.mouse_event(LEFTDOWN, 0, 0, 0, 0)
    time.sleep(hold)   # 长按 350ms 门槛
    for i in range(1, 13):
        u.SetCursorPos(int(x0 + dx * i / 12), int(y0 + dy * i / 12))
        time.sleep(0.035)
    time.sleep(0.35)
    return


def release():
    u.mouse_event(LEFTUP, 0, 0, 0, 0)
    time.sleep(0.8)


def hotkey():
    u.keybd_event(VK_CONTROL, 0, 0, 0)
    u.keybd_event(VK_MENU, 0, 0, 0)
    u.keybd_event(VK_SPACE, 0, 0, 0)
    time.sleep(0.08)
    u.keybd_event(VK_SPACE, 0, KEYUP, 0)
    u.keybd_event(VK_MENU, 0, KEYUP, 0)
    u.keybd_event(VK_CONTROL, 0, KEYUP, 0)
    time.sleep(1.2)


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
    cfg["CollapseBallLeft"] = BALL_X
    cfg["CollapseBallTop"] = BALL_Y
    with open(CFG, "w", encoding="utf-8") as f:
        json.dump(cfg, f, ensure_ascii=False, indent=2)
    kill()
    positions = {}

    try:
        proc = subprocess.Popen([EXE])
        if not wait(FLOATING):
            say("floating window appears", False)
            kill()
            return 1
        say("floating window appears", True)
        time.sleep(1.5)
        esc(FLOATING)
        time.sleep(1.5)
        say("Esc collapses to Glass Orb", not visible(FLOATING) and visible(STRIP))

        r = rect(STRIP)
        if not r:
            say("orb has a rect", False)
            kill()
            return 1
        x, y, w, h = r
        dpi = dpi_of(find(STRIP)) / 96.0
        expected_render = round(76 * dpi)
        inset = (expected_render - round(56 * dpi)) // 2
        say("render canvas 76 DIP (56 DIP hit + aura margin), round",
            w == h and abs(w - expected_render) <= 4,
            "%dx%d px, expected ~%d (hit inset %d)" % (w, h, expected_render, inset))

        idle = os.path.join(OUT, f"{TAG}-idle.png")
        hover = os.path.join(OUT, f"{TAG}-hover.png")
        dragging = os.path.join(OUT, f"{TAG}-dragging.png")

        # 三态截图统一裁切同一尺寸，才能逐像素比对
        move(x + w / 2 + 280, y + h / 2 + 180, 0.8)
        capture(x - 25, y - 25, w + 50, h + 50, idle)
        positions["initial"] = [x, y]

        # Hover：停留 900ms 让 550ms Tooltip 有机会出现
        cx, cy = x + w / 2, y + h / 2
        move(cx, cy, 0.9)
        capture(x - 25, y - 25, w + 50, h + 50, hover)
        capture(x - 110, y - 30, w + 220, h + 190,
                os.path.join(OUT, f"{TAG}-hover-tooltip.png"))
        say("hover visual differs from idle",
            mean_diff(idle, hover) is not None and mean_diff(idle, hover) > 2,
            "pixel diff=%s" % mean_diff(idle, hover))

        # 0) 光晕渲染区（命中区外）不应响应点击
        click(x + 5, y + h / 2)
        say("aura margin is NOT clickable (56 DIP hit area unchanged)",
            not visible(FLOATING))
        move(cx, cy, 0.3)

        # 1) 普通点击打开
        click(cx, cy)
        say("plain click opens floating window", visible(FLOATING))
        esc(FLOATING)
        time.sleep(1.4)

        # 2/3/4) 长按拖动（鼠标会移出小球）、不误开、位置保存
        r2 = rect(STRIP)
        if not r2:
            say("orb visible after re-collapse", False)
            kill()
            return 1
        cx2, cy2 = r2[0] + r2[2] / 2, r2[1] + r2[3] / 2
        dx, dy = 210, 130
        press_drag(cx2, cy2, dx, dy)
        rd = rect(STRIP)
        if rd:
            capture(rd[0] - 25, rd[1] - 25, rd[2] + 50, rd[3] + 50, dragging)
        say("dragging visual differs from idle",
            mean_diff(idle, dragging) is not None and mean_diff(idle, dragging) > 2,
            "pixel diff=%s" % mean_diff(idle, dragging))
        say("long-press drag does NOT open floating window", not visible(FLOATING))
        release()
        r3 = rect(STRIP)
        positions["after_drag"] = [r3[0], r3[1]] if r3 else None
        say("mouse leaving orb still completes drag",
            r3 is not None and abs((r3[0] - r2[0]) - dx) <= 25 and
            abs((r3[1] - r2[1]) - dy) <= 25,
            "%s -> %s (expected +%d,+%d)" % (r2[:2], r3[:2] if r3 else None, dx, dy))
        saved = json.load(open(CFG, encoding="utf-8"))
        # 位置落盘语义 = 命中区左上角 = 渲染窗口左上角 + inset
        say("position saved on mouse up (hit-area origin)",
            saved.get("CollapseBallLeft") is not None and
            abs(saved["CollapseBallLeft"] - (r3[0] + inset)) <= 6,
            "saved=%s, window=%s inset=%d" %
            (str((saved.get("CollapseBallLeft"), saved.get("CollapseBallTop"))), r3[:2], inset))

        # 5) 重启恢复
        kill()
        time.sleep(0.6)
        subprocess.Popen([EXE])
        if wait(FLOATING):
            esc(FLOATING)
            time.sleep(1.4)
            r4 = rect(STRIP)
            say("position restored after restart",
                r4 is not None and r3 is not None and
                abs(r4[0] - r3[0]) <= 8 and abs(r4[1] - r3[1]) <= 8,
                "%s -> %s" % (r3[:2] if r3 else None, r4[:2] if r4 else None))
        else:
            say("position restored after restart", False)

        # 6) 多显示器边缘：向左上大幅拖，必须被钳制在工作区内
        work = wintypes.RECT()
        u.SystemParametersInfoW(0x0030, 0, ctypes.byref(work), 0)
        re = rect(STRIP)
        if re:
            press_drag(re[0] + re[2] / 2, re[1] + re[3] / 2, -900, -900)
            release()
            r5 = rect(STRIP)
            say("clamped inside work area at monitor edge",
                r5 is not None and r5[0] >= work.left - 2 and r5[1] >= work.top - 2,
                "rect=%s work=(%d,%d)" % (r5[:2] if r5 else None, work.left, work.top))
        else:
            say("clamped inside work area at monitor edge", False)

        # 7) 快捷键仍可展开
        hotkey()
        say("Ctrl+Alt+Space still restores workspace", visible(FLOATING))
        kill()
    finally:
        kill()
        restored = json.loads(original)
        restored.pop("CollapseBallLeft", None)
        restored.pop("CollapseBallTop", None)
        with open(CFG, "w", encoding="utf-8") as f:
            json.dump(restored, f, ensure_ascii=False, indent=2)
        with open(os.path.join(OUT, f"{TAG}-positions.json"), "w", encoding="utf-8") as f:
            json.dump(positions, f, ensure_ascii=False, indent=2)

    passed = sum(1 for _, ok in results if ok)
    print("\n===== phase38 orb probe: passed %d, failed %d =====" %
          (passed, len(results) - passed))
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
