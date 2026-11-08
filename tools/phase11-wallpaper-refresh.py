#!/usr/bin/env python3
"""阶段 11：证明「运行中换壁纸」会触发局部亮度重算，不需要重启应用。

流程：暗壁纸启动真实 exe（清场在启动前）-> 读一次诊断 + 截一张图
      -> **不重启**，SystemParametersInfo 切到亮壁纸 -> 等 4 秒
      -> 再读诊断 + 再截图，比较文字方案与像素对比度。

用法：python tools/phase11-wallpaper-refresh.py
"""

import ctypes
import importlib.util
import json
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import screenshot  # noqa: E402

# 复用阶段 10 的像素验收函数，避免两份口径不一致
_spec = importlib.util.spec_from_file_location("p10", os.path.join(HERE, "phase10-dynamic-proof.py"))
p10 = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(p10)

REPO = os.path.dirname(HERE)
EXE = os.path.join(REPO, "publish", "win-x64", "DeskLanes.exe")
CFG = os.path.join(os.environ["LOCALAPPDATA"], "DeskLanes")
SETTINGS = os.path.join(CFG, "settings.json")
DIAG = os.path.join(CFG, "diagnostics", "dynamic-text.json")
WALL = os.path.join(REPO, "docs", "ui-acrylic-final", "wallpapers")
OUT = os.path.join(REPO, "docs", "ui-acrylic-final")

u = ctypes.windll.user32


def diag_snapshot():
    try:
        d = json.load(open(DIAG, encoding="utf-8"))
        return {
            "source": d.get("luminance_source"),
            "refreshes": d.get("desktop_refreshes"),
            "controls": d.get("controls"),
            "dark_scheme": d.get("dark_scheme"),
            "flips": d.get("total_flips"),
            "grid_cells": d.get("grid_cells"),
            "decisions": d.get("decisions"),
        }
    except Exception as ex:
        return {"error": str(ex)}


def pixel_stats(rgb, w, h, r, decisions, tag):
    samples = []
    for d in decisions or []:
        x0 = int(d["x"] - r.Left)
        y0 = int(d["y"] - r.Top)
        got = p10.plate_contrast(rgb, w, h, x0, y0, d["w"], d["h"], d["scheme"])
        if got:
            samples.append(got)
    vals = sorted(s["contrast"] for s in samples)
    q = lambda p: round(vals[min(len(vals) - 1, int(len(vals) * p))], 2) if vals else None
    return {
        "file": "phase11-wallpaper-%s.png" % tag,
        "plates": len(samples),
        "contrast_p50": q(0.50),
        "contrast_min": q(0.0),
    }


def main():
    screenshot._set_dpi_aware()
    original_wall = p10.wallpaper()
    dark = os.path.join(WALL, "dark.bmp")
    light = os.path.join(WALL, "light.bmp")
    reverse = "--reverse" in sys.argv[1:]
    suffix = "-reverse" if reverse else ""
    first, second = (light, dark) if reverse else (dark, light)
    for p in (dark, light):
        if not os.path.exists(p):
            print("FAIL 缺测试壁纸：%s" % p)
            return 1

    cfg_bak = open(SETTINGS, "rb").read()
    try:
        cfg = json.loads(cfg_bak.decode("utf-8"))
        cfg.update({"WindowMode": 0, "LayoutMode": 0, "WindowMaximized": False,
                    "FloatingHideOnDeactivate": False, "SidebarAutoCollapse": False,
                    "WindowLeft": 120, "WindowTop": 80,
                    "FloatingWindowWidth": 780, "FloatingWindowHeight": 540,
                    "FloatingWindowLeft": None, "FloatingWindowTop": None})
        open(SETTINGS, "w", encoding="utf-8").write(json.dumps(cfg, ensure_ascii=False, indent=2))
        try:
            os.remove(DIAG)
        except OSError:
            pass

        p10.wallpaper(first)
        # 启动前清场：应用在 OnStartup 抓一帧纯桌面
        hidden = p10.set_aside(0)
        time.sleep(1.5)
        env = dict(os.environ, DESKLANES_DIAG_TEXT="1")
        subprocess.run(["taskkill", "/IM", "DeskLanes.exe", "/F"],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        time.sleep(1.0)
        proc = subprocess.Popen([EXE], cwd=os.path.dirname(EXE), env=env,
                                stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT)
        result = {"before": {}, "after": {}}
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
            hidden += p10.set_aside(hwnd)
            time.sleep(2.0)
            u.GetWindowRect(hwnd, ctypes.byref(r))
            u.SetCursorPos(r.Left + 60, r.Bottom - 14)
            u.mouse_event(0x0002, 0, 0, 0, 0)
            u.mouse_event(0x0004, 0, 0, 0, 0)
            time.sleep(1.5)
            active = u.GetForegroundWindow() == hwnd
            u.GetWindowRect(hwnd, ctypes.byref(r))
            w, h = r.Right - r.Left, r.Bottom - r.Top
            cw, ch, rgb = screenshot.capture(r.Left, r.Top, w, h,
                                             os.path.join(OUT, "phase11-wallpaper%s-before.png" % suffix))
            a = diag_snapshot()
            result["before"] = {k: a[k] for k in ("source", "refreshes", "controls",
                                                   "dark_scheme", "flips", "grid_cells") if k in a}
            result["before"].update(pixel_stats(rgb, cw, ch, r, a.get("decisions"), "before" + suffix))
            result["before"]["active"] = active

            # ---- 关键：不重启应用，直接换壁纸 ----
            p10.wallpaper(second)
            time.sleep(4.0)  # 1.5s 合并 + 壁纸解码 + DWM 重绘
            u.GetWindowRect(hwnd, ctypes.byref(r))
            active2 = u.GetForegroundWindow() == hwnd
            w2, h2 = r.Right - r.Left, r.Bottom - r.Top
            cw2, ch2, rgb2 = screenshot.capture(r.Left, r.Top, w2, h2,
                                                os.path.join(OUT, "phase11-wallpaper%s-after.png" % suffix))
            b = diag_snapshot()
            result["after"] = {k: b[k] for k in ("source", "refreshes", "controls",
                                                  "dark_scheme", "flips", "grid_cells") if k in b}
            result["after"].update(pixel_stats(rgb2, cw2, ch2, r, b.get("decisions"), "after" + suffix))
            result["after"]["active"] = active2

            # 静置观察：换完壁纸后不应持续翻转颜色
            time.sleep(2.0)
            c = diag_snapshot()
            result["after"]["flips_after_settle"] = c.get("flips")
            result["verdict"] = {
                "source_switched_to_wallpaper_bitmap": result["after"].get("source") == "wallpaper_bitmap",
                "refreshes_increased": (result["after"].get("refreshes") or 0) > (result["before"].get("refreshes") or 0),
                "dark_scheme_changed": (result["after"].get("dark_scheme") or 0) != (result["before"].get("dark_scheme") or 0),
                "pixel_min_ge_4_5": (result["after"].get("contrast_min") or 0) >= 4.5,
                "no_flicker_after_settle": c.get("flips") == b.get("flips"),
            }
            out = os.path.join(OUT, "phase11-wallpaper-refresh%s.json" % ("-reverse" if reverse else ""))
            json.dump(result, open(out, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
            print(json.dumps(result, ensure_ascii=False, indent=2))
            print("证据：%s" % out)
        finally:
            p10.restore(hidden)
            if proc.poll() is None:
                proc.kill()
                proc.wait(timeout=6)
    finally:
        open(SETTINGS, "wb").write(cfg_bak)
        p10.wallpaper(original_wall)
        print("配置与壁纸已还原")
    return 0


if __name__ == "__main__":
    sys.exit(main())
