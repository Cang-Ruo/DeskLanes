"""Desktop Acrylic 材质取证探针（阶段 1 用）。

作用：用真实 DeskLanes.exe 启动一个窗口，强制指定 DWM 背景材质，
等窗口矩形稳定后做**屏幕合成截图**，并把 API 的写入/读回结果一并落盘。

这样才能回答一个问题：Acrylic 到底有没有真的渲染出来？
（RenderTargetBitmap 抓的是 WPF 视觉树，看不到 DWM 材质，不能用来判断。）

用法：
    python tools/probe-acrylic.py <输出.png> [--backdrop auto|none|mica|acrylic|tabbed]

环境变量 DESKLANES_BACKDROP 会被透传给被测进程。
"""

import ctypes
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from screenshot import capture, _set_dpi_aware  # noqa: E402

SW_MINIMIZE = 6
SW_RESTORE = 9

EnumWindowsProc = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
GetWindowTextW = ctypes.windll.user32.GetWindowTextW
GetWindowTextLengthW = ctypes.windll.user32.GetWindowTextLengthW
IsWindowVisible = ctypes.windll.user32.IsWindowVisible
IsIconic = ctypes.windll.user32.IsIconic
ShowWindow = ctypes.windll.user32.ShowWindow
GetWindowRect = ctypes.windll.user32.GetWindowRect


def _title(hwnd):
    n = GetWindowTextLengthW(hwnd)
    if n <= 0:
        return ""
    buf = ctypes.create_unicode_buffer(n + 1)
    GetWindowTextW(hwnd, buf, n + 1)
    return buf.value


def list_top_windows():
    out = []

    @EnumWindowsProc
    def cb(hwnd, _):
        out.append(hwnd)
        return True

    ctypes.windll.user32.EnumWindows(cb, 0)
    return out


SHELL_CLASSES = {
    "Progman", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "DV2ControlHost",
    "Windows.UI.Core.CoreWindow", "ApplicationFrameWindow", "WorkerW",
}


def _class(hwnd):
    buf = ctypes.create_unicode_buffer(256)
    ctypes.windll.user32.GetClassNameW(hwnd, buf, 256)
    return buf.value


def minimize_desktop_windows():
    """最小化所有会挡住桌面的可见窗口，返回被最小化的句柄列表。"""
    minimized = []
    for hwnd in list_top_windows():
        if not IsWindowVisible(hwnd) or IsIconic(hwnd):
            continue
        cls = _class(hwnd)
        title = _title(hwnd)
        if cls in SHELL_CLASSES or not title:
            continue
        try:
            ShowWindow(hwnd, SW_MINIMIZE)
            minimized.append(hwnd)
        except Exception:
            pass
    return minimized


def restore_windows(handles):
    for hwnd in handles:
        try:
            ShowWindow(hwnd, SW_RESTORE)
        except Exception:
            pass


class RECT(ctypes.Structure):
    _fields_ = [("Left", ctypes.c_int32), ("Top", ctypes.c_int32),
                ("Right", ctypes.c_int32), ("Bottom", ctypes.c_int32)]


def find_deskspace_window(timeout=25.0):
    """等主窗口出现（标题含 DeskLanes 且可见）。返回 hwnd 或 0。"""
    deadline = time.time() + timeout
    while time.time() < deadline:
        for hwnd in list_top_windows():
            if not IsWindowVisible(hwnd) or IsIconic(hwnd):
                continue
            title = _title(hwnd)
            if title and "DeskLanes" in title:
                return hwnd
        time.sleep(0.3)
    return 0


def rect_of(hwnd):
    r = RECT()
    GetWindowRect(hwnd, ctypes.byref(r))
    return (r.Left, r.Top, r.Right, r.Bottom)


def wait_rect_stable(hwnd, tries=14, gap=0.35):
    last = None
    for _ in range(tries):
        r = rect_of(hwnd)
        if r == last:
            return r
        last = r
        time.sleep(gap)
    return rect_of(hwnd)


def analyse(width, height, rgb):
    """分析窗口区域的像素分布，用来判断背景材质是否真的在工作。

    判据：
      - 各通道标准差 ≈ 0  -> 整块是均匀色（不透明底色，或材质根本没生效）
      - 标准差明显 > 0    -> 背景里有内容在变化（能看到桌面/壁纸的图案）
    """
    # 只取中间区域，避开窗口边框与圆角，间隔采样控制耗时
    x0, x1 = int(width * 0.15), int(width * 0.85)
    y0, y1 = int(height * 0.20), int(height * 0.80)
    step = max(1, min((x1 - x0), (y1 - y0)) // 160)

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
    sys.stdout.write("---- 窗口区域像素分析 ----\n")
    sys.stdout.write("采样点数 = %d\n" % n)

    labels = ("R", "G", "B")
    for c in range(3):
        vals = [s[c] for s in samples]
        mean = sum(vals) / float(n)
        var = sum((v - mean) ** 2 for v in vals) / float(n)
        std = var ** 0.5
        sys.stdout.write("%s: 均值=%6.1f  标准差=%6.2f  范围=[%3d, %3d]\n"
                         % (labels[c], mean, std, min(vals), max(vals)))

    colours = len(set(samples))
    sys.stdout.write("唯一颜色数 = %d\n" % colours)
    if std_all_zero(samples):
        sys.stdout.write("结论：整块是均匀色 -> 背景材质没有透出来\n")
    else:
        sys.stdout.write("结论：背景里有颜色变化 -> 材质在起作用\n")


def std_all_zero(samples):
    n = len(samples)
    for c in range(3):
        vals = [s[c] for s in samples]
        mean = sum(vals) / float(n)
        if max(abs(v - mean) for v in vals) > 2:
            return False
    return True


def main(argv):
    if len(argv) < 2:
        sys.stdout.write(__doc__)
        return 2

    out = os.path.abspath(argv[1])
    backdrop = None
    # 同时兼容 "--backdrop=mica" 与 "--backdrop mica" 两种写法
    for a in argv[2:]:
        if a.startswith("--backdrop="):
            backdrop = a.split("=", 1)[1].strip().lower()
            break
    if backdrop is None and "--backdrop" in argv:
        idx = argv.index("--backdrop")
        if idx + 1 < len(argv):
            backdrop = argv[idx + 1].strip().lower()

    repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    exe = os.path.join(repo, "publish", "win-x64-selfcontained", "DeskLanes.exe")
    if not os.path.isfile(exe):
        sys.stdout.write("exe not found: %s\n" % exe)
        return 1

    env = dict(os.environ)
    if backdrop:
        env["DESKLANES_BACKDROP"] = backdrop
    sys.stdout.write("backdrop = %s\n" % (backdrop or "(default)"))

    _set_dpi_aware()
    minimized = minimize_desktop_windows()
    sys.stdout.write("minimized %d window(s)\n" % len(minimized))
    time.sleep(0.6)

    proc = None
    try:
        proc = subprocess.Popen([exe], env=env)
        hwnd = find_deskspace_window()
        if not hwnd:
            sys.stdout.write("FAIL: main window did not appear\n")
            return 1
        r = wait_rect_stable(hwnd)
        sys.stdout.write("window rect = %s\n" % (r,))
        # 等首轮桌面扫描完成（截图里出现"正在扫描桌面…"说明拍早了）
        time.sleep(4.0)
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
        restore_windows(minimized)


if __name__ == "__main__":
    sys.exit(main(sys.argv))
