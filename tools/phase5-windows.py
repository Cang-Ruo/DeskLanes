#!/usr/bin/env python3
"""阶段 5：窗口边界验收（设置窗、侧边栏三种状态、悬浮窗、失焦行为）。

全部走真实 exe。设置窗口没有命令行入口，给进程顶层窗口发 WM_COMMAND(2005)
（等同点托盘菜单「设置…」）。敏感项目临时移出，跑完还原。

用法：python tools/phase5-windows.py
"""

import ctypes
import json
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import screenshot  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(REPO, "publish", "win-x64", "DeskLanes.exe")
CFG = os.path.join(os.environ["LOCALAPPDATA"], "DeskLanes")
SETTINGS = os.path.join(CFG, "settings.json")
LIBRARY = os.path.join(CFG, "library.json")
OUT = os.path.join(REPO, "docs", "ui-acrylic-final")

u = ctypes.windll.user32
TRAY_ID_SETTINGS = 2005
SENSITIVE = ("学号", "简历", "个人材料", "厦门", "团组织", "毕业", "2026届",
             "副本", "新建 DOCX", "~$")


def dwm(hwnd, attr):
    v = ctypes.c_int(0)
    hr = ctypes.windll.dwmapi.DwmGetWindowAttribute(hwnd, attr, ctypes.byref(v), 4)
    return v.value if hr == 0 else None


def click(x, y):
    u.SetCursorPos(int(x), int(y))
    time.sleep(0.2)
    u.mouse_event(0x0002, 0, 0, 0, 0)
    u.mouse_event(0x0004, 0, 0, 0, 0)
    time.sleep(0.5)


def kill():
    subprocess.run(["taskkill", "/IM", "DeskLanes.exe", "/F"],
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(1.2)


def open_settings(pid):
    wins = []

    def cb(h, _):
        owner = ctypes.c_ulong()
        u.GetWindowThreadProcessId(h, ctypes.byref(owner))
        if owner.value == pid:
            wins.append(h)
        return True

    u.EnumWindows(ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)(cb), 0)
    for h in wins:
        u.PostMessageW(h, 0x0111, TRAY_ID_SETTINGS, 0)
    return len(wins)


def launch(cfg_patch, first_title, target_title=None, pos=None):
    cfg = json.load(open(SETTINGS, encoding="utf-8"))
    cfg.update(cfg_patch)
    json.dump(cfg, open(SETTINGS, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
    kill()
    proc = subprocess.Popen([EXE], cwd=os.path.dirname(EXE),
                            stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT)
    hwnd = 0
    for _ in range(70):
        hwnd = screenshot.find_window_by_title(first_title)
        if hwnd:
            break
        time.sleep(0.4)
    if not hwnd:
        return proc, 0
    time.sleep(3.0)
    if target_title:
        open_settings(proc.pid)
        for _ in range(30):
            hwnd = screenshot.find_window_by_title(target_title)
            if hwnd:
                break
            time.sleep(0.4)
    if pos:
        u.SetWindowPos(hwnd, 0, pos[0], pos[1], 0, 0, 0x0001 | 0x0004 | 0x0010)
        time.sleep(0.8)
    u.SetWindowPos(hwnd, -1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010)
    time.sleep(0.6)
    return proc, hwnd


def shoot(hwnd, name):
    r = screenshot.RECT()
    u.GetWindowRect(hwnd, ctypes.byref(r))
    click(r.Left + 40, r.Bottom - 14)
    u.GetWindowRect(hwnd, ctypes.byref(r))
    w, h = r.Right - r.Left, r.Bottom - r.Top
    cw, ch, _ = screenshot.capture(r.Left, r.Top, w, h, os.path.join(OUT, name))
    return {"file": name, "size": [cw, ch], "active": u.GetForegroundWindow() == hwnd,
            "backdrop": dwm(hwnd, 38), "corner": dwm(hwnd, 33)}


def hide_sensitive():
    if not os.path.exists(LIBRARY):
        return 0
    lib = json.load(open(LIBRARY, encoding="utf-8"))
    keep, drop = [], []
    for it in lib.get("Items", []):
        (drop if any(k in it.get("Name", "") for k in SENSITIVE) else keep).append(it)
    if not drop:
        return 0
    lib["Items"] = keep
    lib.setdefault("DismissedPaths", [])
    for it in drop:
        key = it["FullPath"].upper()
        if key not in lib["DismissedPaths"]:
            lib["DismissedPaths"].append(key)
    json.dump(lib, open(LIBRARY, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
    return len(drop)


CASES = [
    # 名字, 配置覆盖, 先等这个标题, 目标标题（None=就用先等到的）
    ("settings", {"WindowMode": 1, "WindowMaximized": True, "WindowLeft": 120, "WindowTop": 80},
     "DeskLanes", "DeskLanes 设置", None),
    ("sidebar-right-expanded", {"WindowMode": 2, "SidebarDock": 1, "SidebarExpanded": True},
     "DeskLanes 侧边栏", None, None),
    ("sidebar-left-expanded", {"WindowMode": 2, "SidebarDock": 0, "SidebarExpanded": True},
     "DeskLanes 侧边栏", None, None),
    ("sidebar-collapsed", {"WindowMode": 2, "SidebarDock": 1, "SidebarExpanded": False},
     "DeskLanes 侧边栏", None, None),
    ("floating-default", {"WindowMode": 0, "FloatingHideOnDeactivate": False},
     "DeskLanes 快速启动", None, None),
]


def main():
    global OUT
    for a in sys.argv[1:]:
        if a.startswith("--out="):
            OUT = os.path.abspath(a[6:])
    only = [a[7:] for a in sys.argv[1:] if a.startswith("--only=")]
    only = only[0].split(",") if only else None
    screenshot._set_dpi_aware()
    os.makedirs(OUT, exist_ok=True)
    bak_s = open(SETTINGS, encoding="utf-8").read()
    bak_l = open(LIBRARY, encoding="utf-8").read() if os.path.exists(LIBRARY) else None
    print("临时移出敏感项目：%d 个" % hide_sensitive())

    results = {}
    try:
        for name, patch, first_title, target_title, pos in CASES:
            if only and name not in only:
                continue
            print("\n=== %s ===" % name)
            proc, hwnd = launch(patch, first_title, target_title, pos)
            try:
                if not hwnd:
                    print("  FAIL 窗口没出现")
                    results[name] = {"error": "窗口没出现"}
                    continue
                info = shoot(hwnd, "phase5-%s.png" % name)
                print("  " + json.dumps(info, ensure_ascii=False))

                # 失焦行为：点桌面空白处，看窗口是隐藏还是保持
                r = screenshot.RECT()
                u.GetWindowRect(hwnd, ctypes.byref(r))
                click(r.Left - 60 if r.Left > 100 else 1440, 40)
                time.sleep(1.5)
                still_visible = bool(u.IsWindowVisible(hwnd))
                width_after = 0
                if still_visible:
                    r2 = screenshot.RECT()
                    u.GetWindowRect(hwnd, ctypes.byref(r2))
                    width_after = r2.Right - r2.Left
                info["after_focus_loss"] = {"visible": still_visible, "width": width_after}
                print("  失焦后: " + json.dumps(info["after_focus_loss"], ensure_ascii=False))
                results[name] = info
            finally:
                if proc.poll() is None:
                    proc.kill()
                time.sleep(1.0)
    finally:
        open(SETTINGS, "w", encoding="utf-8").write(bak_s)
        if bak_l is not None:
            open(LIBRARY, "w", encoding="utf-8").write(bak_l)
        print("\n配置已还原")

    with open(os.path.join(OUT, "windows.json"), "w", encoding="utf-8") as f:
        json.dump(results, f, ensure_ascii=False, indent=2)
    print("写入 windows.json")
    return 0


if __name__ == "__main__":
    sys.exit(main())
