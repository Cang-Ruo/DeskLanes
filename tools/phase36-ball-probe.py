#!/usr/bin/env python3
"""阶段 36：小球可拖动 + 位置持久化 + 悬浮窗缩小 + 配色统一。

用真实配置（只清理测试产生的小球位置，不覆盖用户的其它偏好）：
1. 启动后一次性尺寸迁移生效（比原来小 15%，标记落盘）
2. Esc 收束成 3cm 小球
3. 拖动小球 → 位置真的变了、且**不会误触发展开**
4. 位置写入 settings.json；重启应用后小球仍在新位置
5. 点击小球 → 悬浮窗回来
6. 生成新配色小球 / 缩小后悬浮窗的视觉证据

零第三方依赖。用法：python tools/phase36-ball-probe.py
"""

import ctypes
import json
import os
import subprocess
import sys
import time
from ctypes import wintypes

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from screenshot import capture, _set_dpi_aware  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(REPO, "publish", "win-x64-selfcontained", "DeskLanes.exe")
CFG = os.path.join(os.environ.get("LOCALAPPDATA", "",), "DeskLanes", "settings.json")
OUT = os.path.join(REPO, "docs", "ui-acrylic-final")
STRIP = "DeskLanes 边缘细线"
FLOATING = "DeskLanes 快速启动"
BALL = 70
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


def move(x, y, pause=0.5):
    u.SetCursorPos(int(x), int(y))
    time.sleep(pause)


def drag(x0, y0, x1, y1):
    move(x0, y0, 0.3)
    u.mouse_event(LEFTDOWN, 0, 0, 0, 0)
    time.sleep(0.42)   # 阶段38：长按 350ms 后才具备拖动资格
    steps = 12
    for i in range(1, steps + 1):
        u.SetCursorPos(int(x0 + (x1 - x0) * i / steps),
                       int(y0 + (y1 - y0) * i / steps))
        time.sleep(0.04)
    time.sleep(0.2)
    u.mouse_event(LEFTUP, 0, 0, 0, 0)
    time.sleep(0.7)


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


def start():
    return subprocess.Popen([EXE])


def read_cfg():
    with open(CFG, "r", encoding="utf-8") as f:
        return json.load(f)


def main():
    _set_dpi_aware()
    os.makedirs(OUT, exist_ok=True)
    with open(CFG, "r", encoding="utf-8") as f:
        original = f.read()
    before = json.loads(original)
    kill()

    # 场景隔离：用户真实配置勾着「失焦自动隐藏」，而当前前台可能是浏览器，
    # 应用一启动就会被阶段 35 的逻辑收束成小球，探针等不到悬浮窗。
    # 这里只临时关掉这一项（失焦行为由 phase35-deactivate-probe.py 专门覆盖），
    # 其余真实配置（尤其已保存的窗口尺寸）原样保留，跑完还原。
    isolated = json.loads(original)
    isolated["WindowMode"] = 0
    isolated["FloatingHideOnDeactivate"] = False
    with open(CFG, "w", encoding="utf-8") as f:
        json.dump(isolated, f, ensure_ascii=False, indent=2)

    saved_ball = None
    try:
        proc = start()
        if not wait(FLOATING):
            say("floating window appears", False)
            kill()
            return 1
        say("floating window appears", True)
        time.sleep(1.5)

        after = read_cfg()
        w0, h0 = before.get("FloatingWindowWidth", 620), before.get("FloatingWindowHeight", 430)
        w1, h1 = after.get("FloatingWindowWidth", 0), after.get("FloatingWindowHeight", 0)
        # 一次性迁移只在首次启动执行，重跑时标记已落盘、尺寸不再变化。
        # 所以断言改为「缩小后的尺寸已生效且标记存在」；迁移算法本身由
        # 冒烟第 43 节 FloatingSizePolicy.Compact 覆盖，不靠这里重复验证。
        say("compact floating size in effect with migration flag saved",
            after.get("FloatingCompactSizeMigrated") is True and
            420 <= w1 <= 620 and 360 <= h1 <= 430,
            "%.1fx%.1f (was %.1fx%.1f)" % (w1, h1, w0, h0))

        fw = rect(FLOATING)
        capture(fw[0], fw[1], fw[2], min(fw[3], 520),
                os.path.join(OUT, "phase36-floating-compact.png"))

        esc(FLOATING)
        time.sleep(1.4)
        say("Esc collapses to 3cm ball", not visible(FLOATING) and visible(STRIP))
        r0 = rect(STRIP)
        if not r0:
            say("ball has a rect", False)
            kill()
            return 1
        x0, y0, bw, bh = r0
        say("orb is round (DPI-scaled hit area)", bw == bh and 50 <= bw <= 95,
            "%dx%d" % (bw, bh))

        capture(max(0, x0 - 40), max(0, y0 - 40), BALL + 80, BALL + 80,
                os.path.join(OUT, "phase36-ball-glass.png"))

        # 拖动小球到右下一点
        dx, dy = 260, 150
        drag(x0 + bw / 2, y0 + bh / 2, x0 + bw / 2 + dx, y0 + bh / 2 + dy)
        r1 = rect(STRIP)
        moved_ok = r1 is not None and abs((r1[0] - x0) - dx) <= 20 and abs((r1[1] - y0) - dy) <= 20
        say("ball follows the drag", moved_ok,
            "%s -> %s" % (r0[:2], r1[:2] if r1 else None))
        say("dragging does NOT expand the window", not visible(FLOATING))

        cfg = read_cfg()
        saved_ball = (cfg.get("CollapseBallLeft"), cfg.get("CollapseBallTop"))
        say("ball position persisted to settings.json",
            saved_ball[0] is not None and saved_ball[1] is not None,
            str(saved_ball))

        # 点击小球展开（拖动之后仍可点击）
        if r1:
            click(r1[0] + r1[2] / 2, r1[1] + r1[3] / 2)
        say("clicking ball restores floating window", visible(FLOATING))

        # 重启验证跨进程持久化
        kill()
        time.sleep(0.6)
        proc = start()
        if wait(FLOATING):
            esc(FLOATING)
            time.sleep(1.4)
            r2 = rect(STRIP)
            say("ball remembers position after restart",
                r2 is not None and r1 is not None and
                abs(r2[0] - r1[0]) <= 8 and abs(r2[1] - r1[1]) <= 8,
                "%s -> %s" % (r1[:2] if r1 else None, r2[:2] if r2 else None))
        else:
            say("ball remembers position after restart", False)
        kill()
    finally:
        kill()
        # 保留用户要的「更小悬浮窗」迁移结果，清理测试产生的小球位置，
        # 其它偏好原样还原。
        restored = json.loads(original)
        try:
            live = read_cfg()
            restored["FloatingWindowWidth"] = live.get("FloatingWindowWidth", restored.get("FloatingWindowWidth"))
            restored["FloatingWindowHeight"] = live.get("FloatingWindowHeight", restored.get("FloatingWindowHeight"))
            restored["FloatingCompactSizeMigrated"] = True
        except Exception:
            pass
        restored.pop("CollapseBallLeft", None)
        restored.pop("CollapseBallTop", None)
        with open(CFG, "w", encoding="utf-8") as f:
            json.dump(restored, f, ensure_ascii=False, indent=2)

    passed = sum(1 for _, ok in results if ok)
    print("\n===== phase36 ball probe: passed %d, failed %d =====" %
          (passed, len(results) - passed))
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
