#!/usr/bin/env python3
"""把真实的 DeskLanes 窗口摆到指定位置，截它自己那一块。

存在的理由：判断"玻璃透没透、字清不清"必须在真实桌面上看，
而位置很关键 —— 同一扇窗压在深色壁纸上和在亮窗口上，可读性完全不同。
所以这个工具允许指定坐标，同一套主题在两个背景下各拍一张。

用法：
    python tools/shoot-window.py <mode> <输出.png> [--pos=30,120] [--demo]
                                [--size=1400x900]
"""

import ctypes
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import screenshot  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SNAPSHOT_EXE = os.path.join(
    REPO, "tests", "DeskLanes.UiSnapshot", "bin", "Release", "net10.0-windows",
    "DeskLanes.UiSnapshot.exe")

TITLES = {
    "category": "DeskLanes",
    "panels": "DeskLanes",
    "floating": "快速启动",
    "sidebar": "侧边栏",
    # 必须带 DeskLanes 前缀：光搜"设置"会匹配到别的程序的窗口（踩过）
    "settings": "DeskLanes 设置",
    "dialog": "重命名分类",
    "reference": "参照",
}




def click_at(x, y):
    """在指定屏幕坐标点一下（只用来把窗口点成活动窗口）。"""
    u = ctypes.windll.user32
    u.SetCursorPos(int(x), int(y))
    time.sleep(0.25)
    u.mouse_event(0x0002, 0, 0, 0, 0)   # LEFTDOWN
    time.sleep(0.08)
    u.mouse_event(0x0004, 0, 0, 0, 0)   # LEFTUP
    time.sleep(0.4)

def set_aside(hwnd_keep):
    """把其它可见顶层窗口最小化，避免它们盖住目标窗口（抓屏抓到别人）。"""
    u = ctypes.windll.user32
    hidden = []

    def cb(h, _):
        try:
            if h == hwnd_keep:
                return True
            if not u.IsWindowVisible(h) or u.IsIconic(h):
                return True
            length = u.GetWindowTextLengthW(h)
            if length <= 0:
                return True
            cls = screenshot._class(h) or ""
            if any(k in cls for k in ("Shell_TrayWn", "Shell_SecondaryTrayWn",
                                      "Progman", "WorkerW", "DV2ControlHost", "Butt")):
                return True
            u.ShowWindow(h, 6)  # SW_MINIMIZE
            hidden.append(h)
        except Exception:
            pass
        return True

    u.EnumWindows(ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)(cb), 0)
    return hidden


def restore(hidden):
    for h in hidden:
        try:
            ctypes.windll.user32.ShowWindow(h, 9)  # SW_RESTORE
        except Exception:
            pass

def main(argv):
    if len(argv) < 3:
        print(__doc__)
        return 2
    mode, out = argv[1], os.path.abspath(argv[2])
    demo = "--demo" in argv
    pos = None
    size = None
    for a in argv:
        if a.startswith("--pos="):
            x, y = a[6:].split(",")
            pos = (int(x), int(y))
        elif a.startswith("--size="):
            w, h = a[7:].split("x")
            size = (int(w), int(h))
    screenshot._set_dpi_aware()
    os.makedirs(os.path.dirname(out), exist_ok=True)

    args = [SNAPSHOT_EXE, mode, os.path.join(os.path.dirname(out), "_tmp-self.png"),
            "--screen", "--keep-size", "--hold=60000"]
    if demo:
        args.append("--demo")
    hidden = []
    proc = subprocess.Popen(args, cwd=REPO, stdout=subprocess.DEVNULL,
                            stderr=subprocess.STDOUT)
    try:
        hwnd = 0
        for _ in range(80):
            hwnd = screenshot.find_window_by_title(TITLES[mode])
            if hwnd:
                break
            time.sleep(0.4)
        if not hwnd:
            print("FAIL 没找到窗口")
            return 1
        time.sleep(2.5)
        if pos or size:
            # SWP_NOZORDER | SWP_NOACTIVATE，带 --size 时连尺寸一起改
            flags = 0x0004 | 0x0010
            pw = size[0] if size else 0
            ph = size[1] if size else 0
            if not size:
                flags |= 0x0001
            px, py = pos if pos else (0, 0)
            if not pos:
                flags |= 0x0002
            ctypes.windll.user32.SetWindowPos(hwnd, 0, px, py, pw, ph, flags)
            time.sleep(1.5)
        # 必须让窗口变成"活动窗口"再拍：Win11 的 Acrylic 在非活动状态下
        # 会退回近乎实心的底色，拍出来就是一块平板，跟真实观感完全不是一回事。
        before = ctypes.windll.user32.GetForegroundWindow()
        ctypes.windll.user32.SetForegroundWindow(hwnd)
        time.sleep(1.2)
        if ctypes.windll.user32.GetForegroundWindow() != hwnd:
            # SetForegroundWindow 会被前台锁拦下，退一步：先用 AttachThreadInput 借焦点
            tid_cur = ctypes.windll.kernel32.GetCurrentThreadId()
            tid_tgt = ctypes.windll.user32.GetWindowThreadProcessId(hwnd, None)
            ctypes.windll.user32.AttachThreadInput(tid_cur, tid_tgt, True)
            ctypes.windll.user32.SetForegroundWindow(hwnd)
            ctypes.windll.user32.AttachThreadInput(tid_cur, tid_tgt, False)
            time.sleep(1.0)
        hidden = set_aside(hwnd)
        active = ctypes.windll.user32.GetForegroundWindow() == hwnd
        print("active=%s" % active)
        # 点一下窗口内部空白处：Win11 的 Acrylic 只对"活动窗口"渲染，
        # 非活动会退成近乎实心的底色，拍出来完全不是真实观感。
        r0 = screenshot.RECT()
        ctypes.windll.user32.GetWindowRect(hwnd, ctypes.byref(r0))
        click_at(r0.Left + (r0.Right - r0.Left) * 0.72, r0.Top + (r0.Bottom - r0.Top) * 0.86)
        active2 = ctypes.windll.user32.GetForegroundWindow() == hwnd
        print("clicked, active=%s" % active2)

        # 保证置顶：被别的窗口压住时，抓屏抓到的是别人（踩过：抓到系统设置窗口）
        ctypes.windll.user32.SetWindowPos(hwnd, -1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010)
        time.sleep(0.8)
        if ctypes.windll.user32.GetForegroundWindow() == hwnd:
            active = True
        print("on-top 已确认")
        r = screenshot.RECT()
        ctypes.windll.user32.GetWindowRect(hwnd, ctypes.byref(r))
        w, h = r.Right - r.Left, r.Bottom - r.Top
        # 抓两帧确认画面已稳定（异步加载会让内容还在变）
        prev = None
        for _ in range(20):
            cw, ch, rgb = screenshot.capture(r.Left, r.Top, w, h, out)
            if prev is not None and rgb == prev:
                break
            prev = rgb
            time.sleep(0.5)
        screenshot._write_png(out, cw, ch, rgb)
        print("saved %s  %dx%d  at (%d,%d)" % (out, cw, ch, r.Left, r.Top))
        return 0
    finally:
        if proc.poll() is None:
            proc.kill()


if __name__ == "__main__":
    sys.exit(main(sys.argv))
