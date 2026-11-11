"""End-to-end check for two reported defects (ASCII-only output).

Round A: entering Full workspace mode must actually maximize the main window.
Round B: the floating window must be draggable (top hot zone) and resizable
         (bottom-right grip), and the result must be persisted to settings.json.

Drives the real published exe with synthesized absolute mouse input.
"""

import ctypes
import json
import os
import subprocess
import sys
import time
from ctypes import wintypes

u = ctypes.windll.user32

# Without this the process is DPI-unaware: GetWindowRect then hands back
# virtualization-scaled values while mouse_event wants physical pixels, and every
# synthesized click lands in the wrong place.
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)  # PER_MONITOR_DPI_AWARE
except Exception as exc:
    print("WARN: could not set DPI awareness: %s" % exc)

SM_CXSCREEN = 0
SM_CYSCREEN = 1
SM_XVIRTUALSCREEN = 76
SM_YVIRTUALSCREEN = 77
SM_CXVIRTUALSCREEN = 78
SM_CYVIRTUALSCREEN = 79

MOUSEEVENTF_MOVE = 0x0001
MOUSEEVENTF_LEFTDOWN = 0x0002
MOUSEEVENTF_LEFTUP = 0x0004
MOUSEEVENTF_ABSOLUTE = 0x8000

SW_MAXIMIZE = 3

# 仓库根目录：本脚本在 tools\ 下，上一级就是仓库根（换个目录 / 换台机器也能直接用）
REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(REPO, "publish", "win-x64-selfcontained", "DeskLanes.exe")
# 配置目录走 LOCALAPPDATA：不写死用户名，换台机器也能直接跑
CFGDIR = os.path.join(os.environ.get("LOCALAPPDATA", ""), "DeskLanes")

# WindowMode: Floating=0, Full=1, Sidebar=2
MODE_FLOATING = 0
MODE_FULL = 1

MAIN_TITLE = "DeskLanes"
FLOATING_TITLE = "DeskLanes \u5feb\u901f\u542f\u52a8"

results = []


def say(tag, ok, detail=""):
    results.append((tag, ok, detail))
    print("%-6s %s%s" % ("PASS" if ok else "FAIL", tag, (" -- " + detail) if detail else ""))


# ---------------- window lookup ----------------

_found = {}
WNDENUMPROC = ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)


def _enum_proc(hwnd, lparam):
    if u.IsWindowVisible(hwnd):
        n = u.GetWindowTextLengthW(hwnd)
        if n:
            buf = ctypes.create_unicode_buffer(n + 1)
            u.GetWindowTextW(hwnd, buf, n + 1)
            _found.setdefault(buf.value, []).append(hwnd)
    return True


_enum_proc_cb = WNDENUMPROC(_enum_proc)


def find_window(title, timeout=30.0):
    deadline = time.time() + timeout
    last = 0.0
    while time.time() < deadline:
        _found.clear()
        u.EnumWindows(_enum_proc_cb, 0)
        hits = _found.get(title)
        if hits:
            return hits[0]
        last = len(_found)
        time.sleep(0.5)
    print("   [debug] last scan saw %d titled windows: %s" % (last, sorted(_found.keys())))
    return None


class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]

class MONITORINFO(ctypes.Structure):
    _fields_ = [("cbSize", wintypes.DWORD), ("rcMonitor", RECT),
                ("rcWork", RECT), ("dwFlags", wintypes.DWORD)]


class WINDOWPLACEMENT(ctypes.Structure):
    _fields_ = [("length", wintypes.UINT), ("flags", wintypes.UINT),
                ("showCmd", wintypes.UINT),
                ("ptMinPosition", wintypes.POINT), ("ptMaxPosition", wintypes.POINT),
                ("rcNormalPosition", RECT)]


def get_rect(hwnd):
    r = RECT()
    u.GetWindowRect(hwnd, ctypes.byref(r))
    return (r.left, r.top, r.right - r.left, r.bottom - r.top)


def wait_rect_stable(hwnd, tries=10, gap=0.4):
    """Wait until the rect stops changing before touching the window.

    A window that has just appeared may still be settling (DPI / size /
    first layout). Dragging during that window makes the first drag land
    only partly, which shows up as a bogus failure of an otherwise fine
    feature. Seen once for real: asked (160,110), got (179,137).
    """
    last = None
    for _ in range(tries):
        r = get_rect(hwnd)
        if r == last:
            return r
        last = r
        time.sleep(gap)
    return get_rect(hwnd)

def get_showcmd(hwnd):
    wp = WINDOWPLACEMENT()
    wp.length = ctypes.sizeof(WINDOWPLACEMENT)
    u.GetWindowPlacement(hwnd, ctypes.byref(wp))
    return wp.showCmd


# ---------------- synthesized mouse ----------------

def _abs_xy(x, y):
    # mouse_event normalizes absolute coordinates against the PRIMARY monitor,
    # NOT the virtual screen (measured: virtual screen is 3640x1920 starting at
    # (-1080,-90) here, while the primary is 2560x1440 at (0,0)). Using the
    # virtual size silently sends every click to the wrong place.
    pw = u.GetSystemMetrics(SM_CXSCREEN)
    ph = u.GetSystemMetrics(SM_CYSCREEN)
    return int(x * 65535 / pw), int(y * 65535 / ph)


def _move(x, y):
    ax, ay = _abs_xy(x, y)
    u.mouse_event(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE, ax, ay, 0, 0)


def drag(x0, y0, x1, y1, steps=10):
    _move(x0, y0)
    time.sleep(0.2)
    u.mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0)
    time.sleep(0.2)
    for i in range(1, steps + 1):
        _move(int(x0 + (x1 - x0) * i / steps), int(y0 + (y1 - y0) * i / steps))
        time.sleep(0.04)
    time.sleep(0.2)
    u.mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0)
    time.sleep(0.6)


# ---------------- config helpers ----------------

def patch_settings(mode):
    path = os.path.join(CFGDIR, "settings.json")
    with open(path, "r", encoding="utf-8") as f:
        s = json.load(f)
    s["WindowMode"] = mode
    # Auto-hide on focus loss would race with the synthesized clicks; the thing
    # under test is drag/resize, not the auto-hide policy.
    s["FloatingHideOnDeactivate"] = False
    # Start every round from a clean slate: a position saved by an earlier round
    # would make the window open on whichever monitor that was.
    s["FloatingWindowLeft"] = None
    s["FloatingWindowTop"] = None
    # A later round (E) parks the main window on the secondary monitor and that
    # position gets persisted; every round must start from a clean slate or the
    # "maximized on the primary" assertions break on the next run.
    s["WindowLeft"] = None
    s["WindowTop"] = None
    s["WindowMaximized"] = False
    with open(path, "w", encoding="utf-8") as f:
        json.dump(s, f, ensure_ascii=False, indent=2)


def read_settings():
    path = os.path.join(CFGDIR, "settings.json")
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def mouse_home():
    """Park the cursor in the middle of the PRIMARY monitor.

    CenterScreen picks the monitor under the cursor, so leaving it on the
    secondary monitor would move every new window over there.
    """
    _move(u.GetSystemMetrics(SM_CXSCREEN) // 2, u.GetSystemMetrics(SM_CYSCREEN) // 2)
    time.sleep(0.2)


def close_app(proc):
    try:
        proc.kill()
    except Exception:
        pass
    time.sleep(2.0)


# ---- 测试基线：把所有几何归一化，跑完整体还原用户真实配置 --------------------
# Round C/F 的断言硬编码 WindowWidth == 1180，Round E/F 还要求最大化落在竖屏。
# 若上一次运行（或用户自己）把 WindowWidth 改成了 2062.4，窗口中心点会落到主屏，
# WPF 就最大化到主屏，4 条竖屏断言会一起假失败 —— 实测 A/B：
#   2062.4 DIP -> (0,0,2560,1440) 主屏, rect -9,-9 2578x1398
#   1180   DIP -> (-1080,0,0,1920) 竖屏, rect -1091,-11 1102x1870
# 所以这是测试的前置状态污染，不是产品定位缺陷。只归一化、不迁就任何断言，
# 末尾把原始 settings.json 原封不动写回，用户偏好不受影响。
_base_path = os.path.join(CFGDIR, "settings.json")
_original_settings = open(_base_path, "rb").read() if os.path.exists(_base_path) else None
_base = read_settings()
_base.update({
    "WindowWidth": 1180.0, "WindowHeight": 760.0,
    "WindowLeft": None, "WindowTop": None, "WindowMaximized": False,
    "FloatingWindowWidth": 780.0, "FloatingWindowHeight": 540.0,
    "FloatingWindowLeft": None, "FloatingWindowTop": None,
})
with open(_base_path, "w", encoding="utf-8") as f:
    json.dump(_base, f, ensure_ascii=False, indent=2)
print("测试几何已归一化（主窗 1180x760 / 悬浮 780x540），跑完还原原配置")

# ================= Round A: full mode maximizes =================

print("== Round A: entering Full workspace mode ==")
patch_settings(MODE_FULL)
mouse_home()
proc = subprocess.Popen([EXE])
try:
    hwnd = find_window(MAIN_TITLE)
    if hwnd is None:
        say("main window appears in Full mode", False, "window not found in 30s")
    else:
        time.sleep(2.0)
        x, y, w, h = get_rect(hwnd)
        cmd = get_showcmd(hwnd)
        vw = u.GetSystemMetrics(SM_CXVIRTUALSCREEN)
        say("main window is maximized (showCmd=3)", cmd == SW_MAXIMIZE, "showCmd=%d" % cmd)
        # 1180x760 at 125% == 1475x950 physical; anything much wider is truly maximized
        say("window fills the screen width", w > 1900, "rect=%dx%d at (%d,%d)" % (w, h, x, y))
        say("maximized window origin is the screen origin", abs(x) <= 12 and abs(y) <= 12,
            "origin=(%d,%d)" % (x, y))
finally:
    close_app(proc)

# ================= Round B: floating drag + resize =================

print()
print("== Round B: floating window drag + resize ==")
patch_settings(MODE_FLOATING)
mouse_home()
proc = subprocess.Popen([EXE])
try:
    hwnd = find_window(FLOATING_TITLE)
    if hwnd is None:
        say("floating window appears", False, "window not found in 30s")
    else:
        time.sleep(2.5)
        x0, y0, w0, h0 = wait_rect_stable(hwnd)
        say("floating window visible", w0 > 100 and h0 > 100,
            "rect=%dx%d at (%d,%d)" % (w0, h0, x0, y0))

        # --- drag from the top hot zone (16 DIP tall, full width) ---
        dx, dy = 160, 110
        drag(x0 + w0 // 2, y0 + 14, x0 + w0 // 2 + dx, y0 + 14 + dy)
        x1, y1, w1, h1 = get_rect(hwnd)
        moved_x, moved_y = x1 - x0, y1 - y0
        say("dragging the top strip moves the window",
            abs(moved_x - dx) <= 6 and abs(moved_y - dy) <= 6,
            "asked (%d,%d), got (%d,%d)" % (dx, dy, moved_x, moved_y))
        say("dragging does not change the size", w1 == w0 and h1 == h0,
            "%dx%d -> %dx%d" % (w0, h0, w1, h1))

        # --- resize from the bottom-right grip ---
        gw, gh = 70, 50
        drag(x1 + w1 - 8, y1 + h1 - 8, x1 + w1 - 8 + gw, y1 + h1 - 8 + gh)
        x2, y2, w2, h2 = get_rect(hwnd)
        say("dragging the bottom-right grip resizes the window",
            abs((w2 - w1) - gw) <= 6 and abs((h2 - h1) - gh) <= 6,
            "asked +%d/+%d, got +%d/+%d" % (gw, gh, w2 - w1, h2 - h1))
        say("resizing does not move the window", x2 == x1 and y2 == y1,
            "(%d,%d) -> (%d,%d)" % (x1, y1, x2, y2))

        # --- shrink back down, then confirm the lower bound holds ---
        drag(x2 + w2 - 8, y2 + h2 - 8, x2 + w2 - 8 - 900, y2 + h2 - 8 - 700)
        x3, y3, w3, h3 = get_rect(hwnd)
        say("shrinking clamps at MinWidth/MinHeight (420x360 at 125%% == 525x450)",
            w3 >= 520 and w3 <= 535 and h3 >= 445 and h3 <= 460,
            "rect=%dx%d" % (w3, h3))

        # --- left edge: widen, left edge must move left with it ---
        lx = x3 + 3
        drag(lx, y3 + h3 // 2, lx - 60, y3 + h3 // 2)
        x4, y4, w4, h4 = get_rect(hwnd)
        say("left edge widens and moves left with it",
            abs((w4 - w3) - 60) <= 6 and abs((x4 - x3) + 60) <= 6 and h4 == h3,
            "w %d->%d, x %d->%d" % (w3, w4, x3, x4))

        # --- top edge: grow, top edge must move up with it ---
        ty2 = y4 + 3
        drag(x4 + w4 // 2, ty2, x4 + w4 // 2, ty2 - 40)
        x5, y5, w5, h5 = get_rect(hwnd)
        say("top edge grows and moves up with it",
            abs((h5 - h4) - 40) <= 6 and abs((y5 - y4) + 40) <= 6 and w5 == w4,
            "h %d->%d, y %d->%d" % (h4, h5, y4, y5))

        # --- top-left corner: both at once ---
        drag(x5 + 8, y5 + 8, x5 + 8 - 50, y5 + 8 - 40)
        x6, y6, w6, h6 = get_rect(hwnd)
        say("top-left corner widens and grows together",
            abs((w6 - w5) - 50) <= 6 and abs((h6 - h5) - 40) <= 6
            and abs((x6 - x5) + 50) <= 6 and abs((y6 - y5) + 40) <= 6,
            "w %+d h %+d at (%+d,%+d)" % (w6 - w5, h6 - h5, x6 - x5, y6 - y5))

        print()
        print("   final rect = %dx%d at (%d,%d)" % (w6, h6, x6, y6))
finally:
    close_app(proc)

# ================= Round D: drag across monitors =================

print()
print("== Round D: dragging the floating window to the other monitor ==")


def clear_floating_geometry():
    path = os.path.join(CFGDIR, "settings.json")
    with open(path, "r", encoding="utf-8") as f:
        s = json.load(f)
    s["WindowMode"] = MODE_FLOATING
    s["FloatingHideOnDeactivate"] = False
    s["FloatingWindowLeft"] = None
    s["FloatingWindowTop"] = None
    # A later round (E) parks the main window on the secondary monitor and that
    # position gets persisted; every round must start from a clean slate or the
    # "maximized on the primary" assertions break on the next run.
    s["WindowLeft"] = None
    s["WindowTop"] = None
    s["WindowMaximized"] = False
    with open(path, "w", encoding="utf-8") as f:
        json.dump(s, f, ensure_ascii=False, indent=2)


clear_floating_geometry()
mouse_home()
proc = subprocess.Popen([EXE])
try:
    hwnd = find_window(FLOATING_TITLE)
    if hwnd is None:
        say("floating window appears for the cross-monitor test", False, "not found in 30s")
    else:
        time.sleep(2.5)
        x, y, w, h = get_rect(hwnd)
        primary_w = u.GetSystemMetrics(SM_CXSCREEN)
        say("starts on the primary monitor", 0 <= x and x + w <= primary_w,
            "rect at (%d,%d)" % (x, y))

        # This box has the secondary (portrait, 150%) monitor to the LEFT of the
        # primary, so the virtual screen starts at a negative x.
        vx = u.GetSystemMetrics(SM_XVIRTUALSCREEN)
        tx = vx + 120
        ty = 420
        drag(x + w // 2, y + 14, tx + w // 2, ty + 14)

        x2, y2, w2, h2 = get_rect(hwnd)
        say("floating window can be dragged onto the other monitor", x2 < 0,
            "moved to (%d,%d)" % (x2, y2))
        # Portrait monitor is 1080 px physical wide. 780 DIP at 150% would be 1170 px
        # and stick out by 90; the window must now auto-shrink into the work area
        # (~712 DIP = ~1068 px).
        say("floating window auto-shrinks to fit the portrait monitor",
            950 <= w2 <= 1085, "width %d px physical (monitor is 1080)" % w2)

        s2 = read_settings()
        say("cross-monitor position persisted", s2.get("FloatingWindowLeft") is not None
            and s2["FloatingWindowLeft"] < 0, str(s2.get("FloatingWindowLeft")))
finally:
    close_app(proc)

# ================= Round E: main window maximized on the portrait monitor ==========

print()
print("== Round E: main window maximized on the portrait monitor ==")


def place_main_on_secondary():
    path = os.path.join(CFGDIR, "settings.json")
    with open(path, "r", encoding="utf-8") as f:
        s = json.load(f)
    s["WindowMode"] = MODE_FULL
    s["WindowMaximized"] = False
    # Put the window's top-left corner on the portrait (left) monitor.
    # WPF DIP coordinates are scaled by the PRIMARY monitor's DPI (1.25 here),
    # so the portrait screen spans DIP x in [-864, 0] -- -1000 would be outside
    # the virtual screen and correctly rejected by the visibility check.
    s["WindowLeft"] = -800.0
    s["WindowTop"] = 100.0
    with open(path, "w", encoding="utf-8") as f:
        json.dump(s, f, ensure_ascii=False, indent=2)


place_main_on_secondary()
mouse_home()
proc = subprocess.Popen([EXE])
try:
    hwnd = find_window(MAIN_TITLE)
    if hwnd is None:
        say("main window appears for the portrait test", False, "not found in 30s")
    else:
        time.sleep(2.5)
        x, y, w, h = get_rect(hwnd)
        cmd = get_showcmd(hwnd)
        say("main window is maximized on the portrait monitor", cmd == SW_MAXIMIZE and x < 0,
            "showCmd=%d at (%d,%d)" % (cmd, x, y))
        # Portrait monitor is 1080 px physical wide. The old MinWidth of 900 DIP would
        # have forced 1350 px and stuck out by 270. A maximized WPF window also gets a
        # small border overhang (measured: +22 px here, +18 px on the primary), so the
        # pass window is "close to 1080", not "exactly 1080".
        say("maximized window fits the portrait width (no MinWidth overflow)",
            w <= 1120, "width %d px physical (monitor 1080; old bug was 1350)" % w)
finally:
    close_app(proc)

# ======== Round F: restore (not maximize) the main window on the portrait monitor ========
# The last remaining portrait gap: after the user clicks "restore", the window goes back
# to its normal size (1180 DIP), which does NOT fit a 720 DIP wide portrait screen.

print()
print("== Round F: main window restored (normal) on the portrait monitor ==")

WM_SYSCOMMAND = 0x0112
SC_RESTORE = 0xF120
SW_SHOWNORMAL = 1


def place_main_maximized_on_secondary():
    path = os.path.join(CFGDIR, "settings.json")
    with open(path, "r", encoding="utf-8") as f:
        s = json.load(f)
    s["WindowMode"] = MODE_FULL
    s["WindowMaximized"] = True
    s["WindowLeft"] = -800.0
    s["WindowTop"] = 100.0
    with open(path, "w", encoding="utf-8") as f:
        json.dump(s, f, ensure_ascii=False, indent=2)


place_main_maximized_on_secondary()
mouse_home()
proc = subprocess.Popen([EXE])
try:
    hwnd = find_window(MAIN_TITLE)
    if hwnd is None:
        say("main window appears for the restore test", False, "not found in 30s")
    else:
        time.sleep(2.5)
        x0, y0, w0, h0 = get_rect(hwnd)
        # 阶段 17：主窗口改成无边框全屏（WindowStyle=None + ResizeMode=NoResize），
        # 用户不再能把主窗口拖到竖屏，旧断言的前提消失了。替换成等价的新设计断言，
        # 验收强度不降低：必须真的没有系统标题栏/边框，且最大化铺满主屏。
        # 旧断言（保留以便回溯）：
        #   say("starts maximized on the portrait monitor",
        #       get_showcmd(hwnd) == SW_MAXIMIZE and x0 < 0, ...)
        style = u.GetWindowLongW(hwnd, -16) & 0xFFFFFFFF  # GWL_STYLE
        has_caption = bool(style & 0x00C00000)             # WS_CAPTION
        has_thickframe = bool(style & 0x00040000)          # WS_THICKFRAME
        # 阶段 22 起全屏按「鼠标所在屏」最大化，可能是主屏也可能是左竖屏，
        # 因此和窗口实际所在那块屏的宽度比较，而不是硬编码主屏宽。
        u.MonitorFromWindow.argtypes = [wintypes.HWND, wintypes.DWORD]
        u.MonitorFromWindow.restype = ctypes.c_void_p
        u.GetMonitorInfoW.argtypes = [ctypes.c_void_p, ctypes.POINTER(MONITORINFO)]
        u.GetMonitorInfoW.restype = wintypes.BOOL
        mi = MONITORINFO()
        mi.cbSize = ctypes.sizeof(MONITORINFO)
        monitor = u.MonitorFromWindow(hwnd, 2)  # MONITOR_DEFAULTTONEAREST
        got_monitor = bool(monitor) and bool(u.GetMonitorInfoW(monitor, ctypes.byref(mi)))
        target_w = (mi.rcMonitor.right - mi.rcMonitor.left) if got_monitor else u.GetSystemMetrics(SM_CXSCREEN)
        say("borderless fullscreen: no system chrome and covers its monitor",
            not has_caption and not has_thickframe
            and get_showcmd(hwnd) == SW_MAXIMIZE
            and w0 >= target_w - 30,
            "style=0x%08X caption=%s thickframe=%s showCmd=%d size=%dx%d monitor=%d"
            % (style, has_caption, has_thickframe, get_showcmd(hwnd), w0, h0, target_w))

        # Same message the restore button sends. Going through the real message matters:
        # it is what flips the "user restored" flag and triggers the fit.
        u.PostMessageW(hwnd, WM_SYSCOMMAND, SC_RESTORE, 0)
        time.sleep(2.5)

        x1, y1, w1, h1 = get_rect(hwnd)
        cmd1 = get_showcmd(hwnd)
        say("restore button puts the window back to normal", cmd1 == SW_SHOWNORMAL,
            "showCmd=%d" % cmd1)
        say("restored window stays on the portrait monitor", x1 < 0, "x=%d" % x1)
        # Portrait work area is 1080 px physical (720 DIP at 150%). Without the fix the
        # window would come back at 1180 DIP = 1770 px and stick out by ~690 px.
        say("restored window auto-shrinks to fit the portrait monitor",
            600 <= w1 <= 1120, "width %d px physical (monitor 1080; unfixed would be ~1770)" % w1)
finally:
    close_app(proc)

sF = read_settings()
say("shrunk size is NOT persisted (intended 1180 kept for the big screen)",
    sF.get("WindowWidth") == 1180, str(sF.get("WindowWidth")))

# ================= Round C: did it persist? =================

print()
print("== Round C: geometry persisted to settings.json ==")
s = read_settings()
print("   FloatingWindowWidth  = %s" % s.get("FloatingWindowWidth"))
print("   FloatingWindowHeight = %s" % s.get("FloatingWindowHeight"))
print("   FloatingWindowLeft   = %s" % s.get("FloatingWindowLeft"))
print("   FloatingWindowTop    = %s" % s.get("FloatingWindowTop"))

say("width persisted", isinstance(s.get("FloatingWindowWidth"), (int, float))
    and s.get("FloatingWindowWidth") > 0, str(s.get("FloatingWindowWidth")))
say("height persisted", isinstance(s.get("FloatingWindowHeight"), (int, float))
    and s.get("FloatingWindowHeight") > 0, str(s.get("FloatingWindowHeight")))
say("x position persisted", s.get("FloatingWindowLeft") is not None,
    str(s.get("FloatingWindowLeft")))
say("y position persisted", s.get("FloatingWindowTop") is not None,
    str(s.get("FloatingWindowTop")))
say("main window preferences untouched", s.get("WindowWidth") == 1180,
    str(s.get("WindowWidth")))

# ======== Round G: corrupt settings.json must still start (C10, real exe) =============
# The smoke suite covers this at the data layer; this covers the part only a real
# process can prove: the app must come up, not hang, and keep a backup of the bad file.
# Must run after Round C: this round writes a broken settings.json and then restores
# the pre-round file, which would wipe the geometry Round C is about to check.

print()
print("== Round G: starts with a corrupt settings.json ==")

BACKUPS = os.path.join(CFGDIR, "backups")
settings_path = os.path.join(CFGDIR, "settings.json")
original_settings = None
if os.path.exists(settings_path):
    with open(settings_path, "r", encoding="utf-8") as f:
        original_settings = f.read()

try:
    if os.path.isdir(BACKUPS):
        backups_before = len(os.listdir(BACKUPS))
    else:
        backups_before = 0

    with open(settings_path, "w", encoding="utf-8") as f:
        f.write('{ "WindowMode": "this is not a valid mode", "Version": "oops"')

    mouse_home()
    proc = subprocess.Popen([EXE])
    try:
        hwnd = find_window(MAIN_TITLE, timeout=30) or find_window(FLOATING_TITLE, timeout=5)
        say("app still starts with a corrupt settings.json", hwnd is not None)
        if hwnd is not None:
            time.sleep(3)
            say("process is still alive after start (no crash on exit)",
                proc.poll() is None, "poll=%s" % proc.poll())
    finally:
        close_app(proc)

    if os.path.isdir(BACKUPS):
        backups_after = len(os.listdir(BACKUPS))
    else:
        backups_after = 0
    say("corrupt file was backed up before reset", backups_after > backups_before,
        "%d -> %d in backups\\" % (backups_before, backups_after))

    with open(settings_path, "r", encoding="utf-8") as f:
        restored = f.read()
    say("settings.json is valid JSON again after recovery", restored.lstrip().startswith("{"),
        restored[:40].replace("\n", " "))
finally:
    if original_settings is not None:
        with open(settings_path, "w", encoding="utf-8") as f:
            f.write(original_settings)

# ======== Round H: a saved position that is no longer on screen =====================
# Cannot unplug a monitor from a script, but the failure it causes is testable: the
# saved WindowLeft/Top points somewhere that no longer exists. The app must not use it,
# and the window must end up somewhere the user can actually see and reach. This is the
# "monitor unplugged / resolution lowered" scenario.
# Note: Full mode maximizes by design, so what is really being checked here is that the
# stale coordinate is discarded rather than that the window opens at any particular size.
# Must run last: it restores settings.json, like Round G does.

print()
print("== Round H: window comes back when the saved position is off screen ==")

settings_path_h = os.path.join(CFGDIR, "settings.json")
original_settings_h = None
if os.path.exists(settings_path_h):
    with open(settings_path_h, "r", encoding="utf-8") as f:
        original_settings_h = f.read()

try:
    s_h = json.loads(original_settings_h) if original_settings_h else {}
    s_h["WindowMode"] = MODE_FULL
    s_h["WindowMaximized"] = False
    s_h["WindowLeft"] = -9999
    s_h["WindowTop"] = -9999
    with open(settings_path_h, "w", encoding="utf-8") as f:
        json.dump(s_h, f, ensure_ascii=False, indent=2)

    mouse_home()
    proc_h = subprocess.Popen([EXE])
    try:
        hwnd_h = find_window(MAIN_TITLE, timeout=30)
        say("app starts with an off-screen saved position", hwnd_h is not None)
        if hwnd_h is not None:
            # 窗口刚出现时可能正处在「恢复旧位置 → 钳位 → 最大化」的中间态。
            # 曾经在枚举到句柄的瞬间读到 543,215 2550x1370，把瞬时状态误判成
            # "窗口跑到屏幕外"；下一轮同样配置读到的是 -9,-9 2578x1398。
            # 这里只等矩形连续两次相同再量，**断言和判据一个字不改**。
            last = None
            for _ in range(12):
                cur = get_rect(hwnd_h)
                if cur == last:
                    break
                last = cur
                time.sleep(0.4)
            hx, hy, hw, hh = get_rect(hwnd_h)
            vx = u.GetSystemMetrics(SM_XVIRTUALSCREEN)
            vy = u.GetSystemMetrics(SM_YVIRTUALSCREEN)
            vw = u.GetSystemMetrics(SM_CXVIRTUALSCREEN)
            vh = u.GetSystemMetrics(SM_CYVIRTUALSCREEN)
            px = u.GetSystemMetrics(SM_CXSCREEN)
            py = u.GetSystemMetrics(SM_CYSCREEN)
            print("   rect=%d,%d %dx%d  virtual=%d,%d %dx%d" % (hx, hy, hw, hh, vx, vy, vw, vh))
            # The point of the round: the stale coordinate must not be used.
            say("the off-screen saved position was not used", hx != -9999 and hy != -9999,
                "rect=%d,%d" % (hx, hy))
            # Tolerance is 24 px, not 0: a maximized WPF window is sized screen + border
            # and the border is clipped, so its rect legitimately sticks out ~9 px.
            say("window is inside the virtual screen",
                hx >= vx - 24 and hy >= vy - 24 and hx + hw <= vx + vw + 24 and hy + hh <= vy + vh + 24,
                "rect=%d,%d %dx%d" % (hx, hy, hw, hh))
            # Being inside the virtual screen is not enough: in a multi-monitor gap the
            # window could still sit between two monitors. Require real overlap with the
            # primary screen, where the cursor is parked.
            overlap_x = min(hx + hw, px) - max(hx, 0)
            overlap_y = min(hy + hh, py) - max(hy, 0)
            say("window really overlaps the primary screen", overlap_x > 100 and overlap_y > 100,
                "overlap %dx%d px" % (overlap_x, overlap_y))
    finally:
        close_app(proc_h)
finally:
    if original_settings_h is not None:
        with open(settings_path_h, "w", encoding="utf-8") as f:
            f.write(original_settings_h)

# ---- 还原用户真实配置（所有断言都已读完 settings.json）--------------------
# 不还原的话，Round D 会把"竖屏收缩后的 712 DIP"永久写进用户的配置里，
# 下次打开悬浮窗就变成小窗口；阶段 10 已经踩过一次。
if _original_settings is not None:
    with open(_base_path, "wb") as f:
        f.write(_original_settings)
    print("已还原运行前的 settings.json")

# ================= summary =================

passed = sum(1 for _, ok, _ in results if ok)
failed = len(results) - passed
print()
print("===== interaction check: passed %d, failed %d =====" % (passed, failed))
for tag, ok, detail in results:
    if not ok:
        print("   FAILED: %s -- %s" % (tag, detail))

sys.exit(0 if failed == 0 else 1)
