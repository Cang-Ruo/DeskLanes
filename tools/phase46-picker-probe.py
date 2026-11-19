#!/usr/bin/env python3
"""阶段 46：悬浮球单击分类选择窗 / 双击展开 / 长按拖动 三路径验收。"""
import ctypes
import json
import os
import subprocess
import sys
import time

u = ctypes.windll.user32
EXE = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                   "publish", "win-x64-selfcontained", "DeskLanes.exe")
CFG = os.path.join(os.environ.get("LOCALAPPDATA", ""), "DeskLanes", "settings.json")
OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                   "docs", "ui-acrylic-final")
EnumProc = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
results = []


class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


def say(tag, ok, detail=""):
    results.append((tag, bool(ok)))
    print("%-6s %s%s" % ("PASS" if ok else "FAIL", tag, (" -- " + detail) if detail else ""))


def visible_windows():
    out = []

    @EnumProc
    def cb(h, _):
        n = u.GetWindowTextLengthW(h)
        if n and u.IsWindowVisible(ctypes.c_void_p(h)):
            b = ctypes.create_unicode_buffer(n + 1)
            u.GetWindowTextW(h, b, n + 1)
            out.append(b.value)
        return True

    u.EnumWindows(cb, 0)
    return out


def find(title):
    @EnumProc
    def cb(h, _):
        n = u.GetWindowTextLengthW(h)
        if n:
            b = ctypes.create_unicode_buffer(n + 1)
            u.GetWindowTextW(h, b, n + 1)
            if b.value == title:
                find.hwnd = h
        return True

    find.hwnd = 0
    u.EnumWindows(cb, 0)
    return find.hwnd


def rect(title):
    h = find(title)
    r = RECT()
    if h and u.GetWindowRect(ctypes.c_void_p(h), ctypes.byref(r)):
        return r.left, r.top, r.right - r.left, r.bottom - r.top
    return None


def click(x, y):
    u.SetCursorPos(int(x), int(y))
    time.sleep(0.2)
    u.mouse_event(2, 0, 0, 0, 0)
    time.sleep(0.08)
    u.mouse_event(4, 0, 0, 0, 0)


def dblclick(x, y):
    u.SetCursorPos(int(x), int(y))
    time.sleep(0.2)
    for _ in range(2):
        u.mouse_event(2, 0, 0, 0, 0)
        time.sleep(0.05)
        u.mouse_event(4, 0, 0, 0, 0)
        time.sleep(0.08)


def esc(title):
    h = find(title)
    if h:
        u.SetForegroundWindow(ctypes.c_void_p(h))
        time.sleep(0.2)
        u.keybd_event(0x1B, 0, 0, 0)
        u.keybd_event(0x1B, 0, 2, 0)
    time.sleep(1.2)


def main():
    original = json.load(open(CFG, encoding="utf-8"))
    cfg = dict(original)
    cfg.update(WindowMode=0, CollapseOnLaunch=True, FloatingHideOnDeactivate=False)
    cfg["CollapseBallLeft"] = 300.0
    cfg["CollapseBallTop"] = 300.0
    json.dump(cfg, open(CFG, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
    subprocess.run(["taskkill", "/F", "/IM", "DeskLanes.exe"], capture_output=True)

    try:
        proc = subprocess.Popen([EXE])
        time.sleep(3)
        esc("DeskLanes 快速启动")

        r = rect("DeskLanes 边缘细线")
        say("ball collapsed", r is not None and not any("快速启动" in w for w in visible_windows()))
        cx, cy = r[0] + r[2] / 2, r[1] + r[3] / 2

        # 1) 单击 → 分类选择窗
        click(cx, cy)
        time.sleep(0.7)
        say("single click opens category picker", any("分类" in w for w in visible_windows()),
            str([w for w in visible_windows() if "DeskLanes" in w or "分类" in w]))
        pr = rect("DeskLanes 分类")
        say("floating window stays collapsed while picker open",
            not any("快速启动" in w for w in visible_windows()))

        # 2) Esc 关闭选择窗
        esc("DeskLanes 分类")
        say("Esc closes picker, ball still visible",
            not any("分类" in w for w in visible_windows()) and
            any("边缘细线" in w for w in visible_windows()))

        # 3) 双击 → 展开完整悬浮窗，不弹选择窗
        dblclick(cx, cy)
        time.sleep(1.0)
        say("double click opens floating window", any("快速启动" in w for w in visible_windows()))
        say("double click does NOT open picker", not any("分类" in w for w in visible_windows()))

        # 4) 再收起，单击弹选择窗 → 点第一行「全部」→ 展开且分类选中
        esc("DeskLanes 快速启动")
        click(cx, cy)
        time.sleep(0.7)
        pr = rect("DeskLanes 分类")
        if pr:
            # 第一行「全部」在标题下方约 40px 处
            click(pr[0] + pr[2] / 2, pr[1] + 52)
            time.sleep(1.0)
        say("picking category expands floating window", any("快速启动" in w for w in visible_windows()))
        say("picker closes after pick", not any("分类" in w for w in visible_windows()))

        # 5) 长按拖动仍正常
        esc("DeskLanes 快速启动")
        u.SetCursorPos(int(cx), int(cy))
        u.mouse_event(2, 0, 0, 0, 0)
        time.sleep(0.45)
        for i in range(1, 9):
            u.SetCursorPos(int(cx + 150 * i / 8), int(cy + 80 * i / 8))
            time.sleep(0.04)
        u.mouse_event(4, 0, 0, 0, 0)
        time.sleep(0.8)
        r2 = rect("DeskLanes 边缘细线")
        say("long-press drag still works",
            r2 is not None and abs((r2[0] - r[0]) - 150) <= 25 and
            abs((r2[1] - r[1]) - 80) <= 25,
            "%s -> %s" % (r[:2], r2[:2] if r2 else None))
        say("long-press drag does NOT open picker/window",
            not any("分类" in w or "快速启动" in w for w in visible_windows()))

        subprocess.run(["taskkill", "/F", "/IM", "DeskLanes.exe"], capture_output=True)
    finally:
        subprocess.run(["taskkill", "/F", "/IM", "DeskLanes.exe"], capture_output=True)
        restored = dict(original)
        restored.pop("CollapseBallLeft", None)
        restored.pop("CollapseBallTop", None)
        json.dump(restored, open(CFG, "w", encoding="utf-8"), ensure_ascii=False, indent=2)

    passed = sum(1 for _, ok in results if ok)
    print("\n===== phase46 picker probe: passed %d, failed %d =====" %
          (passed, len(results) - passed))
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
