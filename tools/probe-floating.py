"""悬浮窗 / 侧边栏材质取证探针。

启动真实 DeskLanes.exe，等主窗口就绪后发全局快捷键呼出悬浮窗，
等悬浮窗矩形稳定后做屏幕合成截图并分析像素。

用法：
    python tools/probe-floating.py <输出.png> [--sidebar]
"""

import ctypes
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from screenshot import capture, _set_dpi_aware  # noqa: E402

KEYEVENTF_KEYUP = 0x0002
VK_CONTROL = 0x11
VK_MENU = 0x12
VK_SPACE = 0x20

EnumWindowsProc = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
GetWindowTextW = ctypes.windll.user32.GetWindowTextW
GetWindowTextLengthW = ctypes.windll.user32.GetWindowTextLengthW
IsWindowVisible = ctypes.windll.user32.IsWindowVisible
IsIconic = ctypes.windll.user32.IsIconic
keybd_event = ctypes.windll.user32.keybd_event


def _title(hwnd):
    n = GetWindowTextLengthW(hwnd)
    if n <= 0:
        return ""
    buf = ctypes.create_unicode_buffer(n + 1)
    GetWindowTextW(hwnd, buf, n + 1)
    return buf.value


def find_window(keyword, timeout=15.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        result = []

        @EnumWindowsProc
        def cb(hwnd, _):
            if IsWindowVisible(hwnd) and not IsIconic(hwnd):
                t = _title(hwnd)
                if t and keyword.lower() in t.lower():
                    result.append(hwnd)
            return True

        ctypes.windll.user32.EnumWindows(cb, 0)
        if result:
            return result[0]
        time.sleep(0.3)
    return 0


def send_toggle_shortcut():
    """Ctrl+Alt+Space：呼出 / 隐藏悬浮窗（默认快捷键）。"""
    keybd_event(VK_CONTROL, 0, 0, 0)
    keybd_event(VK_MENU, 0, 0, 0)
    keybd_event(VK_SPACE, 0, 0, 0)
    time.sleep(0.06)
    keybd_event(VK_SPACE, 0, KEYEVENTF_KEYUP, 0)
    keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0)
    keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0)


def analyse(width, height, rgb):
    x0, x1 = int(width * 0.15), int(width * 0.85)
    y0, y1 = int(height * 0.35), int(height * 0.75)
    step = max(1, min((x1 - x0), (y1 - y0)) // 120)
    samples = []
    for y in range(y0, y1, step):
        base = y * width * 3
        for x in range(x0, x1, step):
            i = base + x * 3
            samples.append((rgb[i], rgb[i + 1], rgb[i + 2]))
    if not samples:
        sys.stdout.write("analyse: no samples\n")
        return
    n = len(samples)
    for c, name in enumerate("RGB"):
        vals = [s[c] for s in samples]
        mean = sum(vals) / float(n)
        var = sum((v - mean) ** 2 for v in vals) / float(n)
        sys.stdout.write("%s: 均值=%6.1f 标准差=%6.2f\n" % (name, mean, var ** 0.5))
    sys.stdout.write("唯一颜色数 = %d\n" % len(set(samples)))


class RECT(ctypes.Structure):
    _fields_ = [("Left", ctypes.c_int32), ("Top", ctypes.c_int32),
                ("Right", ctypes.c_int32), ("Bottom", ctypes.c_int32)]


def wait_rect_stable(hwnd, tries=12, gap=0.35):
    last = None
    for _ in range(tries):
        r = RECT()
        ctypes.windll.user32.GetWindowRect(hwnd, ctypes.byref(r))
        cur = (r.Left, r.Top, r.Right, r.Bottom)
        if cur == last:
            return cur
        last = cur
        time.sleep(gap)
    r = RECT()
    ctypes.windll.user32.GetWindowRect(hwnd, ctypes.byref(r))
    return (r.Left, r.Top, r.Right, r.Bottom)


def main(argv):
    if len(argv) < 2:
        sys.stdout.write(__doc__)
        return 2
    out = os.path.abspath(argv[1])

    repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    exe = os.path.join(repo, "publish", "win-x64-selfcontained", "DeskLanes.exe")
    if not os.path.isfile(exe):
        sys.stdout.write("exe not found: %s\n" % exe)
        return 1

    _set_dpi_aware()
    proc = None
    try:
        proc = subprocess.Popen([exe])
        main_hwnd = find_window("DeskLanes", timeout=25.0)
        if not main_hwnd:
            sys.stdout.write("FAIL: main window did not appear\n")
            return 1
        time.sleep(4.0)

        # 快捷键可能因初始化时序或输入法干扰偶尔失手：多试几次
        target = 0
        for attempt in range(5):
            send_toggle_shortcut()
            time.sleep(1.5)
            target = find_window("快速启动", timeout=3.0)
            if target:
                sys.stdout.write("floating shown on attempt %d\n" % (attempt + 1))
                break
            sys.stdout.write("attempt %d: floating not shown yet\n" % (attempt + 1))
        if not target:
            sys.stdout.write("FAIL: floating window did not appear\n")
            return 1

        r = wait_rect_stable(target)
        sys.stdout.write("floating rect = %s\n" % (r,))
        time.sleep(1.0)
        w, h, rgb = capture(r[0], r[1], r[2] - r[0], r[3] - r[1], out)
        analyse(w, h, rgb)
        return 0
    finally:
        if proc and proc.poll() is None:
            proc.terminate()
            try:
                proc.wait(timeout=8)
            except Exception:
                proc.kill()


if __name__ == "__main__":
    sys.exit(main(sys.argv))
