#!/usr/bin/env python3
"""阶段 10：局部亮度自适应文字色的像素级验收。

不依赖应用自报的对比度：应用只导出"哪块文字用了哪种方案 + 它的屏幕矩形"，
本脚本用真实截图里那块矩形的像素，自己算文字色 / 底色 / WCAG 对比度。
同时静置 8 秒观察颜色翻转次数，验证没有闪烁。

用法：
    python tools/phase10-dynamic-proof.py [--wall=light|dark] [--out=...]
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
DIAG = os.path.join(CFG, "diagnostics", "dynamic-text.json")
WALL = os.path.join(REPO, "docs", "ui-acrylic-final", "wallpapers")
OUT = os.path.join(REPO, "docs", "ui-acrylic-final")

u = ctypes.windll.user32
SPI_SETDESKWALLPAPER, SPI_GETDESKWALLPAPER = 20, 115
SPIF = 0x01 | 0x02
_u32 = ctypes.WinDLL("user32", use_last_error=True)
_u32.IsWindowVisible.argtypes = [ctypes.c_void_p]
_u32.IsWindowVisible.restype = ctypes.c_int
_u32.IsIconic.argtypes = [ctypes.c_void_p]
_u32.ShowWindow.argtypes = [ctypes.c_void_p, ctypes.c_int]
_u32.GetClassNameW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p, ctypes.c_int]
_u32.GetWindowTextLengthW.argtypes = [ctypes.c_void_p]


def wallpaper(path=None):
    if path:
        u.SystemParametersInfoW(SPI_SETDESKWALLPAPER, 0, path, SPIF)
        # 必须等 DWM 真正把新壁纸画出来（phase3 实测 2.5s，1.2s 会抓到旧壁纸）
        time.sleep(2.5)
        return path
    buf = ctypes.create_unicode_buffer(512)
    u.SystemParametersInfoW(SPI_GETDESKWALLPAPER, 512, buf, 0)
    return buf.value


def cls(h):
    b = ctypes.create_unicode_buffer(256)
    _u32.GetClassNameW(ctypes.c_void_p(h), b, 256)
    return b.value


def set_aside(keep):
    hidden = []
    CB = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
    skip = ("Shell_TrayWn", "Shell_SecondaryTrayWn", "Progman", "WorkerW",
            "DV2ControlHost", "Butt", "LockApp")

    @CB
    def cb(h, _):
        if h == keep or not _u32.IsWindowVisible(h) or _u32.IsIconic(h):
            return True
        if _u32.GetWindowTextLengthW(h) <= 0:
            return True
        if any(k in cls(h) for k in skip):
            return True
        _u32.ShowWindow(ctypes.c_void_p(h), 6)
        hidden.append(h)
        return True

    u.EnumWindows(cb, 0)
    return hidden


def restore(hidden):
    for h in hidden:
        _u32.ShowWindow(ctypes.c_void_p(h), 9)


def luma(rgb, i):
    return (rgb[i] * 299 + rgb[i + 1] * 587 + rgb[i + 2] * 114) // 1000


def srgb_lin(v):
    v = v / 255.0
    return v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4


def contrast(a, b):
    l1, l2 = srgb_lin(a), srgb_lin(b)
    if l1 < l2:
        l1, l2 = l2, l1
    return (l1 + 0.05) / (l2 + 0.05)


def percentile(vals, q):
    vals = sorted(vals)
    return vals[min(len(vals) - 1, max(0, int(len(vals) * q)))]


def plate_contrast(rgb, w, h, x0, y0, pw, ph, scheme):
    """在截图里取这块名字牌的像素：中位数当底色，极值当文字色。"""
    vals = []
    for y in range(max(0, y0), min(h, y0 + int(ph))):
        base = y * w * 3
        for x in range(max(0, x0), min(w, x0 + int(pw))):
            vals.append(luma(rgb, base + x * 3))
    if len(vals) < 12:
        return None
    bg = percentile(vals, 0.50)
    fg = percentile(vals, 0.03) if scheme == "dark_text" else percentile(vals, 0.97)
    return {"bg": bg, "fg": fg, "contrast": round(contrast(fg, bg), 2)}


def main():
    args = sys.argv[1:]
    wall_tag = next((a[7:] for a in args if a.startswith("--wall=")), "light")
    out_dir = next((a[6:] for a in args if a.startswith("--out=")), OUT)
    os.makedirs(out_dir, exist_ok=True)
    screenshot._set_dpi_aware()

    original = wallpaper()
    path = os.path.join(WALL, wall_tag + ".bmp")
    if not os.path.exists(path):
        print("FAIL 找不到测试壁纸：%s（先跑一次 phase3-readability.py 生成）" % path)
        return 1
    wallpaper(path)

    cfg_bak = open(SETTINGS, encoding="utf-8").read()
    try:
        cfg = json.loads(cfg_bak)
        cfg.update({"WindowMode": 0, "LayoutMode": 0, "WindowMaximized": False,
                    "FloatingHideOnDeactivate": False, "WindowLeft": 120, "WindowTop": 80,
                    "FloatingWindowWidth": 780, "FloatingWindowHeight": 540,
                    "FloatingWindowLeft": None, "FloatingWindowTop": None})
        json.dump(cfg, open(SETTINGS, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
        try:
            os.remove(DIAG)
        except OSError:
            pass

        env = dict(os.environ, DESKLANES_DIAG_TEXT="1")
        subprocess.run(["taskkill", "/IM", "DeskLanes.exe", "/F"],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        time.sleep(1.2)
        # 关键顺序：应用在启动时抓桌面帧，所以**先清场再启动**，
        # 否则那一帧里会有别的窗口，亮度网格被污染。
        hidden = set_aside(0)
        time.sleep(1.5)
        proc = subprocess.Popen([EXE], cwd=os.path.dirname(EXE), env=env,
                                stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT)
        try:
            hwnd = 0
            for _ in range(60):
                hwnd = screenshot.find_window_by_title("快速启动")
                if hwnd:
                    break
                time.sleep(0.4)
            if not hwnd:
                print("FAIL 悬浮窗没出现")
                return 1
            time.sleep(3.0)
            r = screenshot.RECT()
            u.GetWindowRect(hwnd, ctypes.byref(r))
            u.SetWindowPos(hwnd, 0, 520, 240, 0, 0, 0x0001 | 0x0004 | 0x0010)
            time.sleep(0.8)
            hidden += set_aside(hwnd)   # 已经在启动前清过场，这里只是补漏
            time.sleep(2.0)
            # 重新读矩形：上面把窗口挪到了 (520,240)，清场又可能改变层级。
            # 用旧坐标点击会点空，窗口没被激活，Acrylic 就不生效（踩过一次）。
            u.GetWindowRect(hwnd, ctypes.byref(r))
            u.SetCursorPos(r.Left + 60, r.Bottom - 14)
            u.mouse_event(0x0002, 0, 0, 0, 0)
            u.mouse_event(0x0004, 0, 0, 0, 0)
            time.sleep(1.5)
            active = u.GetForegroundWindow() == hwnd
            if not active:
                u.SetForegroundWindow(hwnd)
                u.GetWindowRect(hwnd, ctypes.byref(r))
                u.SetCursorPos(r.Left + 60, r.Bottom - 14)
                u.mouse_event(0x0002, 0, 0, 0, 0)
                u.mouse_event(0x0004, 0, 0, 0, 0)
                time.sleep(1.2)
                active = u.GetForegroundWindow() == hwnd
            time.sleep(1.0)  # 等 LocationChanged / Activated 引发的刷新写完诊断
            u.GetWindowRect(hwnd, ctypes.byref(r))
            w, h = r.Right - r.Left, r.Bottom - r.Top
            shot = os.path.join(out_dir, "phase10-dynamic-%s.png" % wall_tag)
            cw, ch, rgb = screenshot.capture(r.Left, r.Top, w, h, shot)

            diag = json.load(open(DIAG, encoding="utf-8"))
            samples, baseline = [], []
            for d in diag["decisions"]:
                x0 = int(d["x"] - r.Left)
                y0 = int(d["y"] - r.Top)
                got = plate_contrast(rgb, cw, ch, x0, y0, d["w"], d["h"], d["scheme"])
                if got is None:
                    continue
                samples.append({"scheme": d["scheme"], **got})
                # 同一块底色上，如果还是固定浅字（#F1F5FA ≈ 灰 244），对比度会是多少
                baseline.append(round(contrast(244, got["bg"]), 2))

            def q(vals, p):
                vals = sorted(vals)
                return round(vals[min(len(vals) - 1, int(len(vals) * p))], 2)

            dynamic = [s["contrast"] for s in samples]
            result = {
                "wallpaper": wall_tag, "active": active, "size": [cw, ch],
                "app_controls": diag["controls"], "app_dark_scheme": diag["dark_scheme"],
                "measured_plates": len(samples),
                "pixel_dark_scheme": sum(1 for s in samples if s["scheme"] == "dark_text"),
                "pixel_contrast_p50": q(dynamic, 0.50),
                "pixel_contrast_min": q(dynamic, 0.0),
                "pixel_contrast_worst_p05": q(dynamic, 0.05),
                "fixed_light_text_p50": q(baseline, 0.50),
                "fixed_light_text_min": q(baseline, 0.0),
                "flips_at_capture": diag["total_flips"],
            }

            # 静置观察：不再动窗口、不滚动，看颜色还翻不翻
            series = []
            for _ in range(16):
                time.sleep(0.5)
                try:
                    d = json.load(open(DIAG, encoding="utf-8"))
                    series.append(d["total_flips"])
                except Exception:
                    series.append(None)
            clean = [v for v in series if v is not None]
            result["idle_flips_delta"] = (clean[-1] - clean[0]) if clean else None
            result["idle_flip_series"] = clean
            result["samples_head"] = samples[:3]

            out_json = os.path.join(out_dir, "phase10-dynamic-%s.json" % wall_tag)
            json.dump(result, open(out_json, "w", encoding="utf-8"),
                      ensure_ascii=False, indent=2)
            print(json.dumps({k: v for k, v in result.items()
                              if k not in ("idle_flip_series", "samples_head")},
                             ensure_ascii=False, indent=2))
            print("证据：%s / %s" % (shot, out_json))
        finally:
            restore(hidden)
            if proc.poll() is None:
                proc.kill()
                proc.wait(timeout=6)
    finally:
        open(SETTINGS, "w", encoding="utf-8").write(cfg_bak)
        wallpaper(original)
        print("配置与壁纸已还原")
    return 0


if __name__ == "__main__":
    sys.exit(main())
