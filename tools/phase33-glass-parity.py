#!/usr/bin/env python3
"""阶段 33：侧边栏 / 悬浮窗玻璃配方一致性取证。

把两个窗口强制摆到同一屏幕位置、同一尺寸，各自做 GDI 屏幕合成截图，
比较窗口内「肯定没有内容」的底部空白条的平均 RGB。
若两者共用同一套窗口底色 + 根 Border tint，同一块壁纸上的合成色应几乎相同。

零第三方依赖。用法：python tools/phase33-glass-parity.py
"""

import ctypes
import json
import os
import subprocess
import sys
import time
from ctypes import wintypes

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from screenshot import capture, find_window_by_title, read_png, _set_dpi_aware  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SNAP = os.path.join(REPO, "tests", "DeskLanes.UiSnapshot", "bin", "Release",
                    "net10.0-windows", "DeskLanes.UiSnapshot.exe")
OUT = os.path.join(REPO, "docs", "ui-acrylic-final")
X, Y, W, H = 100, 100, 500, 600
MODES = [("sidebar", "DeskLanes 侧边栏"), ("floating", "DeskLanes 快速启动")]

u = ctypes.windll.user32
u.SetWindowPos.argtypes = [ctypes.c_void_p, ctypes.c_void_p,
                           ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                           wintypes.UINT]


def mean_rgb(path, x0, y0, x1, y1):
    w, h, data = read_png(path)
    total = [0, 0, 0]
    n = 0
    for y in range(y0, min(y1, h)):
        row = y * w * 3
        for x in range(x0, min(x1, w)):
            i = row + x * 3
            total[0] += data[i]
            total[1] += data[i + 1]
            total[2] += data[i + 2]
            n += 1
    return [round(c / max(n, 1), 1) for c in total] if n else None


def main():
    _set_dpi_aware()
    if not os.path.isfile(SNAP):
        print(json.dumps({"error": "UiSnapshot.exe 不存在"}))
        return 1

    results = []
    for label, title in MODES:
        png = os.path.join(OUT, "phase33-%s-glass.png" % label)
        snap = os.path.join(OUT, "phase33-tmp.png")
        proc = subprocess.Popen([SNAP, label, snap, "--demo", "--hold=12000"])
        hwnd = 0
        for _ in range(40):
            time.sleep(0.4)
            hwnd = find_window_by_title(title)
            if hwnd:
                break
        if not hwnd:
            results.append({"mode": label, "error": "窗口没出现"})
            proc.terminate()
            continue

        u.SetWindowPos(ctypes.c_void_p(hwnd), ctypes.c_void_p(-1), X, Y, W, H,
                       0x0010)  # HWND_TOPMOST + SWP_NOACTIVATE
        time.sleep(1.6)
        capture(X, Y, W, H, png)
        rgb = mean_rgb(png, 40, 492, 250, 570)  # 底部空白条
        results.append({"mode": label, "shot": png, "blank_area_mean_rgb": rgb})
        try:
            proc.terminate()
        except Exception:
            pass
        if os.path.isfile(snap):
            try:
                os.remove(snap)
            except OSError:
                pass

    a = results[0].get("blank_area_mean_rgb") if len(results) > 0 else None
    b = results[1].get("blank_area_mean_rgb") if len(results) > 1 else None
    delta = None
    if a and b:
        delta = round(sum(abs(a[i] - b[i]) for i in range(3)) / 3, 2)
    print(json.dumps({
        "rect": [X, Y, W, H], "results": results,
        "mean_rgb_delta": delta,
        "verdict": "配方一致" if delta is not None and delta < 12 else "仍有差异，需人工看图",
    }, ensure_ascii=False, indent=2))
    return 0 if delta is not None and delta < 12 else 1


if __name__ == "__main__":
    sys.exit(main())
