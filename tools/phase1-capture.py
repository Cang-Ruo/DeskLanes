#!/usr/bin/env python3
"""阶段 1：用**真实 exe**拍全屏/悬浮/侧边栏截图，并记录 DWM 与显示环境。

要点（都是踩过的坑）：
  - 必须是真实运行的 DeskLanes.exe，不是快照工具；
  - 窗口必须处于**活动**状态：Win11 的 Acrylic 只对活动窗口渲染，
    非活动会退成近乎实心的底色，拍出来像"材质没生效"；
  - 拍照前断言 GetForegroundWindow() == 目标句柄；
  - 同时保留整屏原始帧（raw/）与窗口矩形图，避免"只给裁剪美化图"。

隐私：桌面里有真实文件名（学号表 / 简历 …）。脚本会临时把这些项目加进
library.json 的 DismissedPaths（等价于界面里的"从工作区移除"，不改动任何文件），
跑完把配置原样还原。原文见 library.json 备份。

用法：
    python tools/phase1-capture.py [--out docs/ui-acrylic-final]
"""

import ctypes
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import screenshot  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(REPO, "publish", "win-x64", "DeskLanes.exe")
CFG = os.path.join(os.environ["LOCALAPPDATA"], "DeskLanes")
SETTINGS = os.path.join(CFG, "settings.json")
LIBRARY = os.path.join(CFG, "library.json")

u = ctypes.windll.user32
SW_MINIMIZE, SW_RESTORE = 6, 9

# 托盘菜单里「设置…」的命令 id（AppController.TrayIdSettings）
TRAY_ID_SETTINGS = 2005

TARGETS = [
    # (文件名, WindowMode, LayoutMode, 窗口标题)
    ("fullscreen-category", 1, 0, "DeskLanes"),
    ("fullscreen-panels", 1, 1, "DeskLanes"),
    ("floating", 0, 0, "DeskLanes 快速启动"),
    ("sidebar", 2, 0, "DeskLanes 侧边栏"),
    # 设置窗口没有命令行入口，只能给托盘消息窗口发 WM_COMMAND（等同点菜单）
    ("settings", 1, 0, "DeskLanes 设置"),
]


# 句柄按指针宽传；此前调用了不存在的 screenshot._class()，异常被 except 吞掉，
# 导致清场从未生效（整屏原始帧里其它窗口一直挡在 Acrylic 后面）。
_u32 = ctypes.WinDLL("user32", use_last_error=True)
_u32.IsWindowVisible.argtypes = [ctypes.c_void_p]
_u32.IsIconic.argtypes = [ctypes.c_void_p]
_u32.GetWindowTextLengthW.argtypes = [ctypes.c_void_p]
_u32.ShowWindow.argtypes = [ctypes.c_void_p, ctypes.c_int]
_u32.GetClassNameW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p, ctypes.c_int]
_u32.GetWindowThreadProcessId.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_ulong)]
_u32.GetWindowThreadProcessId.restype = ctypes.c_ulong


def window_class(h):
    buf = ctypes.create_unicode_buffer(256)
    _u32.GetClassNameW(ctypes.c_void_p(h), buf, 256)
    return buf.value


def window_pid(h):
    pid = ctypes.c_ulong()
    _u32.GetWindowThreadProcessId(ctypes.c_void_p(h), ctypes.byref(pid))
    return pid.value


def set_aside(keep, own_pid=0):
    """最小化其它应用窗口，让 Acrylic 背后只剩壁纸。

    注意：属于 own_pid 的窗口一律不动。设置窗口是主窗口的 owned window，
    最小化 owner 会把它一起藏起来（清场修好后第一次重拍就踩到，settings.png
    拍成了纯壁纸）。
    """
    hidden = []
    CB = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)

    def cb(h, _):
        try:
            if h == keep or (own_pid and window_pid(h) == own_pid):
                return True
            if not _u32.IsWindowVisible(h) or _u32.IsIconic(h):
                return True
            if _u32.GetWindowTextLengthW(h) <= 0:
                return True
            cls = window_class(h) or ""
            if any(k in cls for k in ("Shell_TrayWn", "Shell_SecondaryTrayWn", "Progman",
                                      "WorkerW", "DV2ControlHost", "Butt", "LockApp")):
                return True
            _u32.ShowWindow(ctypes.c_void_p(h), SW_MINIMIZE)
            hidden.append(h)
        except Exception as ex:
            print("  set_aside: %s" % ex)
        return True

    u.EnumWindows(CB(cb), 0)
    return hidden


def restore_windows(hidden):
    for h in hidden:
        try:
            _u32.ShowWindow(ctypes.c_void_p(h), SW_RESTORE)
        except Exception:
            pass


def click_at(x, y):
    u.SetCursorPos(int(x), int(y))
    time.sleep(0.2)
    u.mouse_event(0x0002, 0, 0, 0, 0)
    time.sleep(0.08)
    u.mouse_event(0x0004, 0, 0, 0, 0)
    time.sleep(0.5)


def dwm_attr(hwnd, attr):
    v = ctypes.c_int(0)
    hr = ctypes.windll.dwmapi.DwmGetWindowAttribute(hwnd, attr, ctypes.byref(v), 4)
    return {"hr": hr & 0xFFFFFFFF, "value": v.value}


def env_info():
    """记录分辨率 / DPI / 壁纸 / 系统版本 —— 截图必须带上这些上下文。"""
    build = sys.getwindowsversion()
    wall = None
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Control Panel\Desktop") as k:
            wall, _ = winreg.QueryValueEx(k, "WallPaper")
    except Exception:
        pass
    try:
        dpi = u.GetDpiForSystem()
    except Exception:
        dpi = 96
    return {
        "dpi": dpi,
        "scale": round(dpi / 96.0, 3),
        "windows_build": "%d.%d.%d" % (build.major, build.minor, build.build),
        "primary": {"w": u.GetSystemMetrics(0), "h": u.GetSystemMetrics(1)},
        "virtual": {
            "x": u.GetSystemMetrics(76), "y": u.GetSystemMetrics(77),
            "w": u.GetSystemMetrics(78), "h": u.GetSystemMetrics(79),
        },
        "monitor_count": u.GetSystemMetrics(80),
        "wallpaper": wall,
    }


def patch_json(path, mutate):
    with open(path, encoding="utf-8") as f:
        data = json.load(f)
    mutate(data)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)


def hide_personal_items():
    """临时把含中日韩字符的项目移出工作区。

    注意：只往 DismissedPaths 里加是不够的 —— 那个列表只拦"新扫描到的"项目，
    已经在 library.json 的 Items 里躺着的照旧显示。所以两处都要处理。
    所有改动都在备份之后，跑完还原，不动桌面上的任何文件。
    """
    if not os.path.exists(LIBRARY):
        return 0
    with open(LIBRARY, encoding="utf-8") as f:
        lib = json.load(f)
    # 只挡真正私人的几类（学号表 / 简历 / 个人材料 …），保留一般项目，
    # 否则分类会被清空，截图反而看不出真实布局。
    sensitive = ("学号", "简历", "个人材料", "厦门", "团组织", "毕业", "2026届",
                 "副本", "新建 DOCX", "~$")
    keep, drop = [], []
    for it in lib.get("Items", []):
        nm = it.get("Name", "")
        if any(k in nm for k in sensitive):
            drop.append(it)
        else:
            keep.append(it)
    if not drop:
        return 0
    lib["Items"] = keep
    lib.setdefault("DismissedPaths", [])
    for it in drop:
        key = it["FullPath"].upper()
        if key not in lib["DismissedPaths"]:
            lib["DismissedPaths"].append(key)
    with open(LIBRARY, "w", encoding="utf-8") as f:
        json.dump(lib, f, ensure_ascii=False, indent=2)
    return len(drop)


def open_settings(pid):
    """给该进程的每个顶层窗口发 WM_COMMAND(2005)，等价于点托盘菜单的「设置…」。

    比 UI 自动化可靠：托盘图标位置不固定，而且本机策略禁用了 UIA。
    """
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


def kill_existing():
    """先清残留实例：单实例应用会把新启动的进程转交给旧实例，导致抓到的是旧窗口。"""
    subprocess.run(["taskkill", "/IM", "DeskLanes.exe", "/F"],
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(1.2)


def capture_one(name, mode, layout, title, out_dir):
    def mutate(s):
        s["WindowMode"] = mode
        s["LayoutMode"] = layout
        s["WindowMaximized"] = (mode == 1)
        if mode == 1:
            # 全屏要落在主屏：先给一个主屏内的位置，应用会先落位再最大化
            s["WindowLeft"] = 120
            s["WindowTop"] = 80
        s["SidebarExpanded"] = (mode == 2)
        s["FloatingHideOnDeactivate"] = False
        # 清场会最小化其它窗口＝制造一次失焦；不临时关掉自动收起，
        # 侧边栏会被拍成 55px 收起态（阶段 8 踩过）。
        s["SidebarAutoCollapse"] = False

    kill_existing()
    patch_json(SETTINGS, mutate)
    proc = subprocess.Popen([EXE], cwd=os.path.dirname(EXE),
                            stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT)
    hidden = []
    try:
        # 设置窗口要先等主窗口起来；其它模式直接等目标窗口
        first_title = "DeskLanes" if name == "settings" else title
        hwnd = 0
        deadline = time.time() + 40
        while time.time() < deadline and proc.poll() is None:
            hwnd = screenshot.find_window_by_title(first_title)
            if hwnd:
                break
            time.sleep(0.4)
        if not hwnd:
            return {"name": name, "error": "窗口没出现（title=%s）" % title}

        if name == "settings":
            # 先等主窗口起来，再发命令打开设置
            time.sleep(2.5)
            open_settings(proc.pid)
            for _ in range(30):
                hwnd = screenshot.find_window_by_title(title)
                if hwnd:
                    break
                time.sleep(0.4)
            if not hwnd:
                return {"name": name, "error": "设置窗口没打开（WM_COMMAND %d）" % TRAY_ID_SETTINGS}

        time.sleep(3.5)  # 等首屏扫描与图标加载
        r = screenshot.RECT()
        u.GetWindowRect(hwnd, ctypes.byref(r))

        # 全屏模式必须真的占满屏幕，不能拿窗口化画面冒充
        if name.startswith("fullscreen"):
            pw, ph = u.GetSystemMetrics(0), u.GetSystemMetrics(1)
            if r.Right - r.Left < pw - 8 or r.Bottom - r.Top < ph - 8:
                u.ShowWindow(hwnd, 3)  # SW_MAXIMIZE
                time.sleep(1.5)
                u.GetWindowRect(hwnd, ctypes.byref(r))

        hidden = set_aside(hwnd, proc.pid)
        time.sleep(2.0)  # 等 DWM 把 Acrylic 背后的新画面重算完
        # 点一下窗口内部空白处，把它变成活动窗口（Acrylic 只对活动窗口渲染）。
        # 设置窗口底部一排是「保存 / 取消」按钮，改点标题栏中央，别误关窗口。
        if name == "settings":
            click_at(r.Left + (r.Right - r.Left) / 2, r.Top + 12)
        else:
            click_at(r.Left + 60, r.Bottom - 14)  # 状态栏空白处，点了不触发任何操作
        time.sleep(0.8)
        active = u.GetForegroundWindow() == hwnd
        if not active:
            # 前台锁可能让第一次点击没生效，重试一次并显式请求前台
            u.ShowWindow(hwnd, SW_RESTORE)
            u.BringWindowToTop(hwnd)
            u.SetForegroundWindow(hwnd)
            click_at(r.Left + (r.Right - r.Left) / 2, r.Top + 12)
            time.sleep(0.8)
            active = u.GetForegroundWindow() == hwnd

        # 置顶，避免被别的窗口压住导致抓到别人
        u.SetWindowPos(hwnd, -1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010)
        time.sleep(0.8)
        u.GetWindowRect(hwnd, ctypes.byref(r))

        attrs = {
            "dark_mode(20)": dwm_attr(hwnd, 20),
            "system_backdrop(38)": dwm_attr(hwnd, 38),
            "corner(33)": dwm_attr(hwnd, 33),
        }

        raw_dir = os.path.join(out_dir, "raw")
        os.makedirs(raw_dir, exist_ok=True)
        vx, vy = u.GetSystemMetrics(76), u.GetSystemMetrics(77)
        vw, vh = u.GetSystemMetrics(78), u.GetSystemMetrics(79)
        screenshot.capture(vx, vy, vw, vh, os.path.join(raw_dir, name + "-fullscreen.png"))

        w, h = r.Right - r.Left, r.Bottom - r.Top
        cw, ch, rgb = screenshot.capture(r.Left, r.Top, w, h,
                                         os.path.join(out_dir, name + ".png"))
        info = {
            "name": name,
            "window_mode": mode,
            "layout_mode": layout,
            "active": active,
            "visible": bool(_u32.IsWindowVisible(ctypes.c_void_p(hwnd))),
            "cleared": True,
            "cleared_windows": len(hidden),
            "rect": [r.Left, r.Top, r.Right, r.Bottom],
            "size": [cw, ch],
            "dwm": attrs,
        }
        return info
    finally:
        restore_windows(hidden)
        if proc.poll() is None:
            proc.kill()
            try:
                proc.wait(timeout=6)
            except Exception:
                pass
        time.sleep(1.0)


def main(argv):
    out_dir = os.path.join(REPO, "docs", "ui-acrylic-final")
    for a in argv[1:]:
        if a.startswith("--out="):
            out_dir = os.path.abspath(a[6:])
    os.makedirs(out_dir, exist_ok=True)
    screenshot._set_dpi_aware()

    if not os.path.isfile(EXE):
        print("FAIL 找不到 exe：%s（先 dotnet publish）" % EXE)
        return 1

    backup = tempfile.mkdtemp(prefix="dl-cfg-bak-")
    for p in (SETTINGS, LIBRARY):
        if os.path.exists(p):
            shutil.copy2(p, os.path.join(backup, os.path.basename(p)))
    print("配置已备份到 %s" % backup)

    info = env_info()
    hidden_n = hide_personal_items()
    print("临时移出敏感项目：%d 个（跑完还原）" % hidden_n)

    only = [a[7:] for a in argv[1:] if a.startswith("--only=")]
    only = only[0].split(",") if only else None

    results = []
    try:
        for name, mode, layout, title in TARGETS:
            if only and name not in only:
                continue
            print("\n=== %s ===" % name)
            res = capture_one(name, mode, layout, title, out_dir)
            print("  " + json.dumps(res, ensure_ascii=False))
            results.append(res)
            time.sleep(1.5)
    finally:
        for p in (SETTINGS, LIBRARY):
            src = os.path.join(backup, os.path.basename(p))
            if os.path.exists(src):
                shutil.copy2(src, p)
        print("\n配置已还原（备份留在 %s）" % backup)

    with open(os.path.join(out_dir, "env.json"), "w", encoding="utf-8") as f:
        json.dump({"env": info, "shots": results}, f, ensure_ascii=False, indent=2)
    print("\n环境信息与截图元数据写入 env.json")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
