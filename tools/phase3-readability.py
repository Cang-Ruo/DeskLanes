#!/usr/bin/env python3
"""阶段 3：亮 / 暗两张壁纸下的可读性取证。

做法：
  1. 生成两张测试壁纸（24bit BMP，SystemParametersInfo 支持最稳）：
     - dark : 深蓝黑底 + 少量高光块（明暗变化明显）
     - light: 接近白底 + 极浅灰渐变 + 几块淡色形状（大量浅色区域）
  2. 临时切换壁纸（记下原壁纸路径，跑完还原并校验）
  3. 每种壁纸 × 每个模式，用**真实 exe** 起窗、激活、截图
  4. 逐格估背景亮度，按背景亮度百分位算文字对比度（WCAG 公式），
     并给出"最亮背景处"的最坏对比度
  5. 额外检查：悬浮窗圆角外圈在亮壁纸下是否露一圈没染色的玻璃

用法：
    python tools/phase3-readability.py
    python tools/phase3-readability.py --clear --tag=glass   # 清场后量「玻璃状态」

--clear 很重要：Acrylic 模糊的是窗口背后**实际挡着的那个东西**。若背后有个白色
应用窗口，暗/亮壁纸测出来会一模一样（都是均匀浅灰），数字只能当"最坏均匀亮底"，
不是玻璃常规值。清场会临时最小化其它可见窗口，拍完在 finally 里逐个还原。
"""

import ctypes
import json
import os
import struct
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
WALL_DIR = os.path.join(OUT, "wallpapers")

u = ctypes.windll.user32

# --dynamic：额外读应用写出的"每块文字实际用了什么颜色 / 对比度多少"，
# 而不是继续假设所有文字都是固定浅色。诊断开关只在本脚本启动的进程里打开。
DYNAMIC = False
DIAG_JSON = os.path.join(CFG, "diagnostics", "dynamic-text.json")


def read_dynamic_metrics():
    """等待应用写出决策 JSON，按真实文字色算对比度分布。"""
    for _ in range(20):
        if os.path.exists(DIAG_JSON):
            break
        time.sleep(0.2)
    if not os.path.exists(DIAG_JSON):
        return {"error": "应用没有写出 dynamic-text.json"}
    try:
        d = json.load(open(DIAG_JSON, encoding="utf-8"))
    except Exception as ex:
        return {"error": "诊断 JSON 解析失败：%s" % ex}
    vals = sorted(x["contrast"] for x in d["decisions"] if x["contrast"] > 0)
    if not vals:
        return {"error": "诊断里没有有效决策"}
    q = lambda p: round(vals[min(len(vals) - 1, int(len(vals) * p))], 2)
    return {
        "controls": d["controls"],
        "dark_scheme": d["dark_scheme"],
        "total_flips": d["total_flips"],
        "contrast_p50": q(0.50),
        "contrast_worst_p05": q(0.05),   # 最差的 5%
        "contrast_min": round(vals[0], 2),
        "grid_cells": d["grid_cells"],
    }

SPI_SETDESKWALLPAPER = 20
SPIF_UPDATEINIFILE, SPIF_SENDCHANGE = 0x01, 0x02
SW_MINIMIZE, SW_RESTORE = 6, 9

TEXT_COLORS = {           # GlassTheme 里的文字色
    "primary":   (0xF1, 0xF5, 0xFA),
    "secondary": (0xB7, 0xC6, 0xD8),
    "tertiary":  (0x8C, 0xA0, 0xB8),
}

SENSITIVE = ("学号", "简历", "个人材料", "厦门", "团组织", "毕业", "2026届",
             "副本", "新建 DOCX", "~$")


# ---------------- 壁纸生成（24bit BMP，不依赖第三方库） ----------------

def write_bmp(path, w, h, pixel_fn):
    row_pad = (4 - (w * 3) % 4) % 4
    data = bytearray()
    for y in range(h - 1, -1, -1):          # BMP 自底向上
        for x in range(w):
            r, g, b = pixel_fn(x, y)
            data += bytes((b, g, r))
        data += b"\x00" * row_pad
    size = 54 + len(data)
    header = struct.pack("<2sIHHI", b"BM", size, 0, 0, 54)
    info = struct.pack("<IiiHHIIiiII", 40, w, h, 1, 24, 0, len(data), 2835, 2835, 0, 0)
    with open(path, "wb") as f:
        f.write(header + info + data)


def make_wallpapers(w=2560, h=1440):
    os.makedirs(WALL_DIR, exist_ok=True)
    dark = os.path.join(WALL_DIR, "dark.bmp")
    light = os.path.join(WALL_DIR, "light.bmp")

    def dark_px(x, y):
        # 深蓝黑底 + 斜向明暗 + 几个亮斑（制造明显的高光与暗部）
        base = 18 + int(26 * (x / w)) + int(10 * (y / h))
        lum = base
        for cx, cy, rad, amp in ((int(w * 0.30), int(h * 0.35), 260, 150),
                                 (int(w * 0.72), int(h * 0.62), 200, 110),
                                 (int(w * 0.55), int(h * 0.15), 140, 90)):
            d2 = (x - cx) ** 2 + (y - cy) ** 2
            lum += int(amp * max(0.0, 1.0 - d2 / (rad * rad)))
        lum = min(255, lum)
        return (max(0, lum - 6), max(0, lum), min(255, lum + 14))   # 偏冷

    def light_px(x, y):
        # 接近白 + 极浅灰渐变 + 几块淡色形状（最坏情况：文字压在亮底上）
        lum = 226 + int(22 * (y / h)) - int(12 * (x / w))
        for cx, cy, rad, amp in ((int(w * 0.25), int(h * 0.30), 320, 22),
                                 (int(w * 0.70), int(h * 0.70), 260, 18)):
            d2 = (x - cx) ** 2 + (y - cy) ** 2
            lum -= int(amp * max(0.0, 1.0 - d2 / (rad * rad)))
        lum = max(0, min(255, lum))
        return (lum, lum - 2 if lum > 2 else lum, max(0, lum - 6))  # 略暖的白

    write_bmp(dark, w, h, dark_px)
    write_bmp(light, w, h, light_px)
    return dark, light


# ---------------- 壁纸切换 / 还原 ----------------

def current_wallpaper():
    import winreg
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Control Panel\Desktop") as k:
        try:
            return winreg.QueryValueEx(k, "WallPaper")[0]
        except FileNotFoundError:
            return ""


def set_wallpaper(path):
    ok = u.SystemParametersInfoW(SPI_SETDESKWALLPAPER, 0, path,
                                 SPIF_UPDATEINIFILE | SPIF_SENDCHANGE)
    time.sleep(2.5)     # 等 DWM 更新背景，否则会拍到旧壁纸
    return bool(ok)


# ---------------- 清场（--clear） ----------------

# 句柄必须按指针宽传：早先版本直接把 HWND 当 c_int 用，再调一个不存在的
# screenshot._class()，异常被 except 吞掉 → 一个窗口都没最小化（清场形同虚设）。
_u32 = ctypes.WinDLL("user32", use_last_error=True)
_u32.IsWindowVisible.argtypes = [ctypes.c_void_p]
_u32.IsIconic.argtypes = [ctypes.c_void_p]
_u32.GetWindowTextLengthW.argtypes = [ctypes.c_void_p]
_u32.ShowWindow.argtypes = [ctypes.c_void_p, ctypes.c_int]
_u32.GetClassNameW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p, ctypes.c_int]


def window_class(h):
    buf = ctypes.create_unicode_buffer(256)
    _u32.GetClassNameW(ctypes.c_void_p(h), buf, 256)
    return buf.value


def set_aside(keep):
    """最小化其它可见窗口，保证 Acrylic 背后只剩壁纸。返回句柄列表供还原。"""
    hidden = []
    CB = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)

    def cb(h, _):
        try:
            if h == keep or not _u32.IsWindowVisible(h) or _u32.IsIconic(h):
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

    ctypes.windll.user32.EnumWindows(CB(cb), 0)
    return hidden


def restore_windows(hidden):
    for h in hidden:
        try:
            _u32.ShowWindow(ctypes.c_void_p(h), SW_RESTORE)
        except Exception:
            pass


# ---------------- 对比度 ----------------

def srgb_to_lin(c):
    c = c / 255.0
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def rel_lum(rgb):
    r, g, b = (srgb_to_lin(v) for v in rgb[:3])
    return 0.2126 * r + 0.7152 * g + 0.0722 * b


def contrast(fg, bg_lum):
    l1, l2 = rel_lum(fg), bg_lum
    if l1 < l2:
        l1, l2 = l2, l1
    return (l1 + 0.05) / (l2 + 0.05)


def cell_lums(rgb, w, h, cells=32):
    """把窗口切成 32×32 格，每格取平均亮度。

    文字笔画在格子里只占很小一部分，格均值 ≈ 该处的背景亮度；
    这样比"整幅平均"更接近文字实际所处的背景。
    """
    out = []
    for gy in range(cells):
        y0, y1 = h * gy // cells, max(h * gy // cells + 1, h * (gy + 1) // cells)
        for gx in range(cells):
            x0, x1 = w * gx // cells, max(w * gx // cells + 1, w * (gx + 1) // cells)
            tot = n = 0
            for y in range(y0, y1, max(1, (y1 - y0) // 6)):
                base = y * w * 3
                for x in range(x0, x1, max(1, (x1 - x0) // 6)):
                    i = base + x * 3
                    tot += (rgb[i] * 299 + rgb[i + 1] * 587 + rgb[i + 2] * 114) // 1000
                    n += 1
            out.append(tot / max(1, n))
    return out


def analyse(rgb, w, h):
    lums = cell_lums(rgb, w, h)
    lum_arr = sorted(lums)
    p = lambda q: lum_arr[min(len(lum_arr) - 1, int(len(lum_arr) * q))]
    res = {"bg_p05": p(0.05), "bg_p50": p(0.50), "bg_p95": p(0.95),
           "bg_max": lum_arr[-1]}
    for name, color in TEXT_COLORS.items():
        # 把 0-255 的亮度粗略还原成灰阶，用于算相对亮度
        def cr(v):
            g = int(max(0, min(255, v)))
            return round(contrast(color, rel_lum((g, g, g))), 2)
        res[name] = {"p50": cr(res["bg_p50"]), "p95_worst": cr(res["bg_p95"]),
                     "max_worst": cr(res["bg_max"])}
    return res


# ---------------- 采集 ----------------

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
    ("category", 1, 0, "DeskLanes", None),
    ("floating", 0, 0, "DeskLanes 快速启动", (520, 240)),
    ("sidebar", 2, 0, "DeskLanes 侧边栏", None),
]


def capture(case_name, mode, layout, title, pos, wall_tag, tag="", clear=False):
    cfg = json.load(open(SETTINGS, encoding="utf-8"))
    cfg.update({"WindowMode": mode, "LayoutMode": layout, "WindowMaximized": (mode == 1),
                "SidebarExpanded": (mode == 2), "WindowLeft": 120, "WindowTop": 80,
                # 端到端 Round D 会把"悬浮窗在竖屏收缩后的尺寸/位置"持久化进配置。
                # 不归一化的话，可读性用例会拿 712 DIP 的窗口和阶段 9 的 780 DIP 比，
                # 尺寸与采样区域都变了，数字没有可比性（阶段 10 踩过）。
                "FloatingWindowWidth": 780, "FloatingWindowHeight": 540,
                "FloatingWindowLeft": None, "FloatingWindowTop": None})
    # --clear 会最小化其它窗口，等于制造一次失焦：侧边栏会自动收起成 55px 窄条、
    # 悬浮窗可能直接隐藏，拍到就不是要验的展开态了。测试期间关掉这两个行为
    #（与 tools/phase5-windows.py 对 FloatingHideOnDeactivate 的处理同理）。
    if clear:
        cfg["SidebarAutoCollapse"] = False
        cfg["FloatingHideOnDeactivate"] = False
    json.dump(cfg, open(SETTINGS, "w", encoding="utf-8"), ensure_ascii=False, indent=2)

    if DYNAMIC:
        try:
            os.remove(DIAG_JSON)
        except OSError:
            pass
    env = dict(os.environ)
    if DYNAMIC:
        env["DESKLANES_DIAG_TEXT"] = "1"
    subprocess.run(["taskkill", "/IM", "DeskLanes.exe", "/F"],
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(1.2)
    # 动态文字色是在**应用启动时**抓一帧纯桌面。原来的 --clear 在启动之后才清场，
    # 那一帧里可能还盖着别的应用窗口，亮度网格就被污染了（各例数字会对不上）。
    # 所以动态口径下必须先把其它窗口最小化，再启动 DeskLanes。
    pre_hidden = []
    if DYNAMIC and clear:
        pre_hidden = set_aside(0)
        time.sleep(1.5)
    proc = subprocess.Popen([EXE], cwd=os.path.dirname(EXE), env=env,
                            stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT)
    hidden = []
    try:
        hwnd = 0
        for _ in range(60):
            hwnd = screenshot.find_window_by_title(title)
            if hwnd:
                break
            time.sleep(0.4)
        if not hwnd:
            return {"error": "窗口没出现"}
        time.sleep(3.5)
        if pos:
            u.SetWindowPos(hwnd, 0, pos[0], pos[1], 0, 0, 0x0001 | 0x0004 | 0x0010)
            time.sleep(1.0)
        u.SetWindowPos(hwnd, -1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010)
        time.sleep(0.6)
        # --clear：先最小化其它窗口，让 Acrylic 背后只剩壁纸；
        # DWM 换背景要一点时间，等 2 秒再激活窗口，否则拍到的是旧帧。
        if clear:
            hidden = set_aside(hwnd)
            time.sleep(2.0)
        r = screenshot.RECT()
        u.GetWindowRect(hwnd, ctypes.byref(r))
        u.SetCursorPos(r.Left + 60, r.Bottom - 14)
        u.mouse_event(0x0002, 0, 0, 0, 0)
        u.mouse_event(0x0004, 0, 0, 0, 0)
        time.sleep(1.2 if clear else 0.8)
        active = u.GetForegroundWindow() == hwnd
        u.GetWindowRect(hwnd, ctypes.byref(r))
        w, h = r.Right - r.Left, r.Bottom - r.Top
        suffix = ("-" + tag) if tag else ""
        name = "readability-%s-%s%s.png" % (wall_tag, case_name, suffix)
        cw, ch, rgb = screenshot.capture(r.Left, r.Top, w, h, os.path.join(OUT, name))
        res = analyse(rgb, cw, ch)
        res.update({"mode": case_name, "wallpaper": wall_tag, "active": active,
                    "size": [cw, ch], "file": name, "cleared": clear,
                    "cleared_windows": len(hidden),
                    # 动态口径下启动前已清过一次场，这一项才是那次的数量；
                    # 只看 cleared_windows 会误以为清场没生效。
                    "pre_cleared_windows": len(pre_hidden)})
        if DYNAMIC:
            # 固定浅色口径仍然保留（历史对照），但真实文字色以这一组为准。
            res["dynamic_text"] = read_dynamic_metrics()
        # 悬浮窗：顺手取左上角 26px 方块，看圆角外圈有没有"未染色"的亮边
        if case_name == "floating":
            corner = []
            for yy in range(0, 26):
                for xx in range(0, 26):
                    i = (yy * cw + xx) * 3
                    corner.append((rgb[i] * 299 + rgb[i + 1] * 587 + rgb[i + 2] * 114) // 1000)
            inner = []
            for yy in range(40, 70):
                for xx in range(40, 70):
                    i = (yy * cw + xx) * 3
                    inner.append((rgb[i] * 299 + rgb[i + 1] * 587 + rgb[i + 2] * 114) // 1000)
            res["corner_mean"] = round(sum(corner) / len(corner), 1)
            res["inner_mean"] = round(sum(inner) / len(inner), 1)
        return res
    finally:
        if proc.poll() is None:
            proc.kill()
            try:
                proc.wait(timeout=6)
            except Exception:
                pass
        restore_windows(hidden)        # --clear 最小化的窗口必须逐个还原
        restore_windows(pre_hidden)    # 启动前为干净桌面帧最小化的窗口
        time.sleep(1.0)


def main():
    global DYNAMIC
    DYNAMIC = "--dynamic" in sys.argv[1:]
    screenshot._set_dpi_aware()
    os.makedirs(OUT, exist_ok=True)

    orig_wall = current_wallpaper()
    print("原壁纸：%s" % orig_wall)
    dark, light = make_wallpapers()
    print("测试壁纸已生成：%s / %s" % (dark, light))

    bak_s = open(SETTINGS, encoding="utf-8").read()
    bak_l = open(LIBRARY, encoding="utf-8").read() if os.path.exists(LIBRARY) else None
    hidden = hide_sensitive()
    print("临时移出敏感项目：%d 个" % hidden)

    want_walls = [a[8:] for a in sys.argv[1:] if a.startswith("--walls=")]
    want_walls = want_walls[0].split(",") if want_walls else ["dark", "light"]
    want_cases = [a[8:] for a in sys.argv[1:] if a.startswith("--cases=")]
    want_cases = want_cases[0].split(",") if want_cases else None
    # --tag=<名字>：把这一轮的 PNG / JSON 单独存一份，做前后对照时不覆盖基线证据。
    want_tag = [a[6:] for a in sys.argv[1:] if a.startswith("--tag=")]
    run_tag = want_tag[0] if want_tag else ""
    clear = "--clear" in sys.argv[1:]
    if clear:
        if not run_tag:
            run_tag = "glass"   # 不带 --tag 也别覆盖基线证据
        print("清场模式：每个用例拍照前最小化其它窗口，拍完还原")

    results = []
    try:
        for tag, path in (("dark", dark), ("light", light)):
            if tag not in want_walls:
                continue
            if not set_wallpaper(path):
                print("FAIL 切壁纸失败：%s" % path)
                return 1
            for case_name, mode, layout, title, pos in CASES:
                if want_cases and case_name not in want_cases:
                    continue
                print("\n=== %s / %s ===" % (tag, case_name))
                res = capture(case_name, mode, layout, title, pos, tag, run_tag, clear)
                print("  " + json.dumps(res, ensure_ascii=False))
                results.append(res)
    finally:
        open(SETTINGS, "w", encoding="utf-8").write(bak_s)
        if bak_l is not None:
            open(LIBRARY, "w", encoding="utf-8").write(bak_l)
        if orig_wall:
            set_wallpaper(orig_wall)
        print("配置已还原；壁纸已切回：%s" % current_wallpaper())

    json_name = "readability-%s.json" % run_tag if run_tag else "readability.json"
    with open(os.path.join(OUT, json_name), "w", encoding="utf-8") as f:
        json.dump(results, f, ensure_ascii=False, indent=2)
    print("指标写入 %s" % json_name)
    return 0


if __name__ == "__main__":
    sys.exit(main())
