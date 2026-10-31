"""重截 README 用的对外截图（脱敏 + 玻璃材质）。

为什么需要这个脚本：
  1. 屏幕合成截图会把窗口之外的桌面一起拍进来 —— 先用最小化清场，
     且主窗口模式会最大化，背景只剩壁纸，真实图标文件名不会进截图。
  2. 必须带 --demo：项目列表换成演示数据，不出现真实桌面文件名。
  3. 必须带 --screen：视觉树快照抓不到 DWM 材质，拍出来还是旧深色观感。

用法：
    python tools/refresh-readme-shots.py
"""

import ctypes
import os
import subprocess
import sys
import time

EnumWindowsProc = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)

SW_MINIMIZE = 6
SW_RESTORE = 9

IsWindowVisible = ctypes.windll.user32.IsWindowVisible
IsIconic = ctypes.windll.user32.IsIconic
ShowWindow = ctypes.windll.user32.ShowWindow
GetWindowTextW = ctypes.windll.user32.GetWindowTextW
GetWindowTextLengthW = ctypes.windll.user32.GetWindowTextLengthW

SHELL_CLASSES = {
    "Progman", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "DV2ControlHost",
    "Windows.UI.Core.CoreWindow", "WorkerW",
}

# 模式 -> 输出文件名
TARGETS = [
    ("category", "category.png"),
    ("panels", "panels.png"),
    ("floating", "floating.png"),
    ("sidebar", "sidebar.png"),
    ("settings", "settings.png"),
]


def _class(hwnd):
    buf = ctypes.create_unicode_buffer(256)
    ctypes.windll.user32.GetClassNameW(hwnd, buf, 256)
    return buf.value


def _title(hwnd):
    n = GetWindowTextLengthW(hwnd)
    if n <= 0:
        return ""
    buf = ctypes.create_unicode_buffer(n + 1)
    GetWindowTextW(hwnd, buf, n + 1)
    return buf.value


def enumerate_windows():
    out = []

    @EnumWindowsProc
    def cb(hwnd, _):
        out.append(hwnd)
        return True

    ctypes.windll.user32.EnumWindows(cb, 0)
    return out


def minimize_all():
    """清场：把会挡在桌面上的窗口最小化，避免拍进截图。"""
    minimized = []
    for hwnd in enumerate_windows():
        if not IsWindowVisible(hwnd) or IsIconic(hwnd):
            continue
        if _class(hwnd) in SHELL_CLASSES or not _title(hwnd):
            continue
        ShowWindow(hwnd, SW_MINIMIZE)
        minimized.append(hwnd)
    return minimized


def restore(handles):
    for hwnd in handles:
        ShowWindow(hwnd, SW_RESTORE)


def main():
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
    repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    out_dir = os.path.join(repo, "docs", "screenshots")
    os.makedirs(out_dir, exist_ok=True)

    dotnet = os.path.join(
        os.path.expanduser("~"), ".dotnet", "dotnet.exe")
    if not os.path.isfile(dotnet):
        sys.stdout.write("dotnet not found: %s\n" % dotnet)
        return 1

    env = dict(os.environ)
    env["DOTNET_ROOT"] = os.path.dirname(dotnet)

    minimized = minimize_all()
    sys.stdout.write("cleared desktop (%d windows minimized)\n" % len(minimized))
    time.sleep(0.8)

    failures = []
    try:
        for mode, filename in TARGETS:
            out = os.path.join(out_dir, filename)
            args = [dotnet, "run", "--project",
                    os.path.join(repo, "tests", "DeskLanes.UiSnapshot"),
                    "-c", "Release", "--no-build", "--",
                    mode, out, "--demo", "--screen"]
            proc = subprocess.run(args, capture_output=True, env=env, timeout=180)
            if proc.returncode == 0:
                size = os.path.getsize(out) if os.path.isfile(out) else 0
                sys.stdout.write("  %-10s -> %s (%d B)\n" % (mode, filename, size))
            else:
                sys.stdout.write("  %-10s FAILED\n" % mode)
                sys.stdout.write(proc.stdout.decode("utf-8", "replace")[-500:] + "\n")
                failures.append(mode)
    finally:
        restore(minimized)

    if failures:
        sys.stdout.write("failed modes: %s\n" % ", ".join(failures))
        return 1
    sys.stdout.write("all screenshots refreshed\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
