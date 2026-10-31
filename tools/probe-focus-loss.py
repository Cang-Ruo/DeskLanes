"""悬浮窗失焦自动隐藏行为验证（阶段 4 回归用）。

流程：
  1. 备份 settings.json，临时把 FloatingHideOnDeactivate 置 true
  2. 启动真实 exe，等主窗口就绪
  3. 快捷键呼出悬浮窗
  4. 启动记事本抢走前台焦点
  5. 检查悬浮窗是否已隐藏
  6. finally 恢复配置、清理进程
"""

import ctypes
import json
import os
import shutil
import subprocess
import sys
import time

KEYEVENTF_KEYUP = 0x0002
VK_CONTROL = 0x11
VK_MENU = 0x12
VK_SPACE = 0x20

EnumWindowsProc = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
IsWindowVisible = ctypes.windll.user32.IsWindowVisible
GetWindowTextW = ctypes.windll.user32.GetWindowTextW
GetWindowTextLengthW = ctypes.windll.user32.GetWindowTextLengthW
keybd_event = ctypes.windll.user32.keybd_event


def find_window(keyword, timeout=15.0):
    """按标题关键字找可见顶层窗口，返回句柄或 0。"""
    deadline = time.time() + timeout
    while time.time() < deadline:
        result = []

        @EnumWindowsProc
        def cb(hwnd, _):
            if IsWindowVisible(hwnd):
                n = GetWindowTextLengthW(hwnd)
                if n > 0:
                    buf = ctypes.create_unicode_buffer(n + 1)
                    GetWindowTextW(hwnd, buf, n + 1)
                    if keyword.lower() in buf.value.lower():
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


def visible_floating_titles():
    out = []

    @EnumWindowsProc
    def cb(hwnd, _):
        if IsWindowVisible(hwnd):
            n = GetWindowTextLengthW(hwnd)
            if n > 0:
                buf = ctypes.create_unicode_buffer(n + 1)
                GetWindowTextW(hwnd, buf, n + 1)
                if "快速启动" in buf.value:
                    out.append(buf.value)
        return True

    ctypes.windll.user32.EnumWindows(cb, 0)
    return out


def main():
    repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    exe = os.path.join(repo, "publish", "win-x64-selfcontained", "DeskLanes.exe")
    cfg_dir = os.path.join(os.environ.get("LOCALAPPDATA", ""), "DeskLanes")
    cfg = os.path.join(cfg_dir, "settings.json")
    backup = cfg + ".focus-probe-backup"

    if not os.path.isfile(exe) or not os.path.isfile(cfg):
        sys.stdout.write("exe or settings.json missing\n")
        return 1

    shutil.copy2(cfg, backup)
    proc = None
    notepad = None
    try:
        with open(cfg, encoding="utf-8") as f:
            settings = json.load(f)
        settings["FloatingHideOnDeactivate"] = True
        with open(cfg, "w", encoding="utf-8") as f:
            json.dump(settings, f, ensure_ascii=False, indent=2)

        proc = subprocess.Popen([exe])
        if not find_window("DeskLanes", timeout=25.0):
            sys.stdout.write("FAIL: main window did not appear\n")
            return 1
        time.sleep(3.5)

        shown = False
        for _ in range(5):
            send_toggle_shortcut()
            time.sleep(1.5)
            if find_window("快速启动", timeout=3.0):
                shown = True
                break
        if not shown:
            sys.stdout.write("FAIL: floating window did not appear\n")
            return 1
        sys.stdout.write("floating window shown\n")
        time.sleep(1.0)

        notepad = subprocess.Popen(["notepad.exe"])
        time.sleep(2.5)

        visible = visible_floating_titles()
        sys.stdout.write("after focus loss, visible floating windows: %s\n" % (visible or "(none)"))
        ok = not visible
        sys.stdout.write("PASS: floating auto-hid on focus loss\n" if ok
                         else "FAIL: floating window still visible after focus loss\n")
        return 0 if ok else 1
    finally:
        if notepad and notepad.poll() is None:
            notepad.kill()
        if proc and proc.poll() is None:
            proc.terminate()
            try:
                proc.wait(timeout=8)
            except Exception:
                proc.kill()
        if os.path.isfile(backup):
            shutil.copy2(backup, cfg)
            os.remove(backup)


if __name__ == "__main__":
    sys.exit(main())
