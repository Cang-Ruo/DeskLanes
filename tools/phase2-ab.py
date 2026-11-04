#!/usr/bin/env python3
"""阶段 2 的前后对照：同一壁纸、同一位置、同一尺寸、活动窗口，各拍一张。

用法：
    python tools/phase2-ab.py before
    # …改代码、重新 publish…
    python tools/phase2-ab.py after

输出：docs/ui-acrylic-final/phase2-<tag>-<mode>.png + metrics.json 里的均值/标准差。
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
OUT = os.path.join(REPO, "docs", "ui-acrylic-final")

u = ctypes.windll.user32

# (名字, WindowMode, LayoutMode, 标题, 位置)
CASES = [
    ("panels", 1, 1, "DeskLanes", None),            # 全屏自由分区：画布那块叠了两层基底
    ("floating", 0, 0, "DeskLanes 快速启动", (520, 240)),
    ("sidebar", 2, 0, "DeskLanes 侧边栏", None),
]


def lum(rgb, i):
    return (rgb[i] * 299 + rgb[i + 1] * 587 + rgb[i + 2] * 114) // 1000


def blank_stats(rgb, w, h):
    """取右侧中部一块"通常没有控件"的区域，比较明暗。"""
    x0, x1 = int(w * 0.55), int(w * 0.90)
    y0, y1 = int(h * 0.30), int(h * 0.55)
    vals = []
    for y in range(y0, y1, max(1, (y1 - y0) // 40)):
        base = y * w * 3
        for x in range(x0, x1, max(1, (x1 - x0) // 40)):
            vals.append(lum(rgb, base + x * 3))
    m = sum(vals) / len(vals)
    return {"mean": round(m, 1), "std": round((sum((v - m) ** 2 for v in vals) / len(vals)) ** 0.5, 1)}


def dwm_int(hwnd, attr):
    v = ctypes.c_int(0)
    hr = ctypes.windll.dwmapi.DwmGetWindowAttribute(hwnd, attr, ctypes.byref(v), 4)
    return v.value if hr == 0 else None


def click_at(x, y):
    u.SetCursorPos(int(x), int(y))
    time.sleep(0.2)
    u.mouse_event(0x0002, 0, 0, 0, 0)
    time.sleep(0.08)
    u.mouse_event(0x0004, 0, 0, 0, 0)
    time.sleep(0.5)


def run_case(name, mode, layout, title, pos, tag):
    cfg = json.load(open(SETTINGS, encoding="utf-8"))
    cfg.update({"WindowMode": mode, "LayoutMode": layout,
                "WindowMaximized": (mode == 1), "SidebarExpanded": (mode == 2),
                "WindowLeft": 120, "WindowTop": 80})
    json.dump(cfg, open(SETTINGS, "w", encoding="utf-8"), ensure_ascii=False, indent=2)

    subprocess.run(["taskkill", "/IM", "DeskLanes.exe", "/F"],
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(1.2)
    proc = subprocess.Popen([EXE], cwd=os.path.dirname(EXE),
                            stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT)
    try:
        hwnd = 0
        for _ in range(60):
            hwnd = screenshot.find_window_by_title(title)
            if hwnd:
                break
            time.sleep(0.4)
        if not hwnd:
            return {"mode": name, "error": "窗口没出现"}
        time.sleep(3.0)
        if pos:
            u.SetWindowPos(hwnd, 0, pos[0], pos[1], 0, 0, 0x0001 | 0x0004 | 0x0010)
            time.sleep(1.0)
        u.SetWindowPos(hwnd, -1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010)
        time.sleep(0.6)
        r = screenshot.RECT()
        u.GetWindowRect(hwnd, ctypes.byref(r))
        click_at(r.Left + 60, r.Bottom - 14)
        u.GetWindowRect(hwnd, ctypes.byref(r))
        active = u.GetForegroundWindow() == hwnd
        w, h = r.Right - r.Left, r.Bottom - r.Top
        cw, ch, rgb = screenshot.capture(r.Left, r.Top, w, h,
                                         os.path.join(OUT, "phase2-%s-%s.png" % (tag, name)))
        st = blank_stats(rgb, cw, ch)
        st.update({"mode": name, "active": active, "size": [cw, ch],
                   "dwm_backdrop": dwm_int(hwnd, 38)})
        return st
    finally:
        if proc.poll() is None:
            proc.kill()
            try:
                proc.wait(timeout=6)
            except Exception:
                pass
        time.sleep(1.0)


def main(argv):
    tag = argv[1] if len(argv) > 1 else "before"
    shot = [a[7:] for a in argv[1:] if a.startswith("--only=")]
    only = shot[0].split(",") if shot else None
    screenshot._set_dpi_aware()
    os.makedirs(OUT, exist_ok=True)

    bak = open(SETTINGS, encoding="utf-8").read()
    results = []
    try:
        for name, mode, layout, title, pos in CASES:
            if only and name not in only:
                continue
            print("=== %s (%s) ===" % (name, tag))
            res = run_case(name, mode, layout, title, pos, tag)
            print("  " + json.dumps(res, ensure_ascii=False))
            results.append(res)
    finally:
        open(SETTINGS, "w", encoding="utf-8").write(bak)
        print("配置已还原")

    path = os.path.join(OUT, "metrics-%s.json" % tag)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(results, f, ensure_ascii=False, indent=2)
    print("指标写入 %s" % path)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
