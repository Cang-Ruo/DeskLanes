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

EXE = r"C:\Users\TTjustTT\Documents\Github\DeskSpace\publish\win-x64-selfcontained\DeskLanes.exe"
CFGDIR = r"C:\Users\TTjustTT\AppData\Local\DeskLanes"

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


class WINDOWPLACEMENT(ctypes.Structure):
    _fields_ = [("length", wintypes.UINT), ("flags", wintypes.UINT),
                ("showCmd", wintypes.UINT),
                ("ptMinPosition", wintypes.POINT), ("ptMaxPosition", wintypes.POINT),
                ("rcNormalPosition", RECT)]


def get_rect(hwnd):
    r = RECT()
    u.GetWindowRect(hwnd, ctypes.byref(r))
    return (r.left, r.top, r.right - r.left, r.bottom - r.top)


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
        x0, y0, w0, h0 = get_rect(hwnd)
        say("floating window visible", w0 > 100 and h0 > 100,
            "rect=%dx%d at (%d,%d)" % (w0, h0, x0, y0))

        # --- drag from the top hot zone (16 DIP tall, full width) ---
        dx, dy = 160, 110
        drag(x0 + w0 // 2, y0 + 8, x0 + w0 // 2 + dx, y0 + 8 + dy)
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

        print()
        print("   final rect = %dx%d at (%d,%d)" % (w3, h3, x3, y3))
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
        drag(x + w // 2, y + 8, tx + w // 2, ty + 8)

        x2, y2, w2, h2 = get_rect(hwnd)
        say("floating window can be dragged onto the other monitor", x2 < 0,
            "moved to (%d,%d)" % (x2, y2))
        say("size survives the monitor change (DIP re-scaled to 150%%)",
            w2 > 1000, "width %d px physical on the 150%% monitor" % w2)

        s2 = read_settings()
        say("cross-monitor position persisted", s2.get("FloatingWindowLeft") is not None
            and s2["FloatingWindowLeft"] < 0, str(s2.get("FloatingWindowLeft")))
finally:
    close_app(proc)

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

# ================= summary =================

passed = sum(1 for _, ok, _ in results if ok)
failed = len(results) - passed
print()
print("===== interaction check: passed %d, failed %d =====" % (passed, failed))
for tag, ok, detail in results:
    if not ok:
        print("   FAILED: %s -- %s" % (tag, detail))

sys.exit(0 if failed == 0 else 1)
