#!/usr/bin/env python3
"""阶段 13-A：把窗口里**所有**可见文字块逐块做像素级对比度审计。

应用在 DESKLANES_DIAG_TEXT=1 时导出 all-text-blocks.json（矩形 + 当前前景色 +
是否接入了局部亮度自适应）。本脚本用真实截图像素算每块的对比度，
重点看「没接入自适应的固定底色控件文字」在亮壁纸下到底够不够 4.5。

用法：python tools/phase13-all-text-audit.py --wall=light|dark
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

_spec = importlib.util.spec_from_file_location("p10", os.path.join(HERE, "phase10-dynamic-proof.py"))
p10 = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(p10)

REPO = os.path.dirname(HERE)
EXE = os.path.join(REPO, "publish", "win-x64", "DeskLanes.exe")
CFG = os.path.join(os.environ["LOCALAPPDATA"], "DeskLanes")
SETTINGS = os.path.join(CFG, "settings.json")
DIAG = os.path.join(CFG, "diagnostics", "all-text-blocks.json")
WALL = os.path.join(REPO, "docs", "ui-acrylic-final", "wallpapers")
OUT = os.path.join(REPO, "docs", "ui-acrylic-final")

u = ctypes.windll.user32


def fg_rgb(hex_color):
    """#AARRGGBB -> (r,g,b)。取不到时按主文字色兜底。"""
    if not hex_color or len(hex_color) < 9:
        return (0xF1, 0xF5, 0xFA)
    return (int(hex_color[3:5], 16), int(hex_color[5:7], 16), int(hex_color[7:9], 16))


def rel_lum(rgb):
    def lin(v):
        v /= 255.0
        return v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4
    return 0.2126 * lin(rgb[0]) + 0.7152 * lin(rgb[1]) + 0.0722 * lin(rgb[2])


def block_contrast(rgb, w, h, x0, y0, pw, ph, fg_hex):
    """前景色用视觉树导出的真实值，背景色用截图里该块的中位数。

    不用"极值像素当前景色"：一整行宽的 TextBlock 里文字只占约 3%，
    97 分位取到的仍是背景，会算出 1.0 这种假数字（阶段 13 踩过）。
    """
    vals = []
    for y in range(max(0, y0), min(h, y0 + int(ph))):
        base = y * w * 3
        for x in range(max(0, x0), min(w, x0 + int(pw))):
            vals.append(p10.luma(rgb, base + x * 3))
    if len(vals) < 12:
        return None
    bg = p10.percentile(vals, 0.50)
    l1, l2 = rel_lum(fg_rgb(fg_hex)), p10.srgb_lin(bg)
    if l1 < l2:
        l1, l2 = l2, l1
    return (l1 + 0.05) / (l2 + 0.05)


def q(vals, p):
    vals = sorted(vals)
    return round(vals[min(len(vals) - 1, int(len(vals) * p))], 2) if vals else None


def main():
    args = sys.argv[1:]
    tag = next((a[7:] for a in args if a.startswith("--wall=")), "light")
    wall = os.path.join(WALL, tag + ".bmp")
    if not os.path.exists(wall):
        print("FAIL 缺测试壁纸 %s" % wall)
        return 1
    screenshot._set_dpi_aware()
    original = p10.wallpaper()
    cfg_bak = open(SETTINGS, "rb").read()
    hidden = []
    proc = None
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

        p10.wallpaper(wall)
        hidden = p10.set_aside(0)
        time.sleep(1.5)
        env = dict(os.environ, DESKLANES_DIAG_TEXT="1", DESKLANES_DYNAMIC_TEXT="1")
        subprocess.run(["taskkill", "/IM", "DeskLanes.exe", "/F"],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        time.sleep(1.0)
        proc = subprocess.Popen([EXE], cwd=os.path.dirname(EXE), env=env,
                                stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT)
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
        time.sleep(1.2)
        # --scroll=N：把项目列表滚下去，让原本在可视区外的模板实例也进入像素审计
        scroll = next((int(a[9:]) for a in args if a.startswith("--scroll=")), 0)
        if scroll:
            u.SetCursorPos(r.Left + 480, r.Top + 400)
            for _ in range(scroll):
                u.mouse_event(0x0800, 0, 0, ctypes.c_uint(-120).value, 0)
                time.sleep(0.12)
            time.sleep(2.0)  # 等 ScrollChanged 触发的亮度刷新写完诊断
        u.GetWindowRect(hwnd, ctypes.byref(r))
        active = u.GetForegroundWindow() == hwnd
        w, h = r.Right - r.Left, r.Bottom - r.Top
        suffix = "-scroll" if scroll else ""
        shot = os.path.join(OUT, "phase13-all-text-%s%s.png" % (tag, suffix))
        # 诊断 JSON 每次 RefreshAll 都会重写。滚动后可能还在刷新，若截图与 JSON
        # 不是同一次状态，就会拿"新前景色"去配"旧底色"算出假的低对比度。
        # 反复确认截图前后 captured_at 不变，再开始逐块计算。
        d = None
        for attempt in range(4):
            before = json.load(open(DIAG, encoding="utf-8"))["captured_at"]
            cw, ch, rgb = screenshot.capture(r.Left, r.Top, w, h, shot)
            time.sleep(0.4)
            d = json.load(open(DIAG, encoding="utf-8"))
            if d["captured_at"] == before:
                break
            time.sleep(1.5)
        else:
            print("WARN 诊断状态在截图期间持续变化，本次结果可能不同步")
        # 滚动后有些项目的布局坐标仍在窗口矩形内，但已被顶部搜索框/分类栏裁掉。
        # 拿那些坐标去采样会读到搜索框的深色像素，算出假的"深字压深底"。
        # 只对项目网格可视区内的文字块做像素审计。
        view_top, view_bottom = (180, 640) if scroll else (0, ch)
        fixed, managed, skipped, below = [], [], 0, []
        for item in d["items"]:
            x0, y0 = int(item["x"] - r.Left), int(item["y"] - r.Top)
            center_y = y0 + item["h"] / 2
            if (x0 < -4 or y0 < -4 or x0 + item["w"] > cw + 4 or y0 + item["h"] > ch + 4
                    or center_y < view_top or center_y > view_bottom):
                skipped += 1
                continue
            c = block_contrast(rgb, cw, ch, x0, y0, item["w"], item["h"], item["fg"])
            if c is None:
                skipped += 1
                continue
            rec = {"label": item["label"], "fg": item["fg"], "size": round(item["font_size"], 1),
                   "contrast": round(c, 2)}
            (managed if item["managed"] else fixed).append(rec)
            if c < 4.5:
                below.append(rec)

        # 滚动态下导出的矩形会与被裁掉的布局位置错位，逐块像素比对不可靠；
        # 补一份应用自己的决策记录（含每块算出的对比度）覆盖所有已注册控件。
        app_decisions = {"error": "no diagnostics"}
        try:
            dd = json.load(open(os.path.join(CFG, "diagnostics", "dynamic-text.json"), encoding="utf-8"))
            cs = sorted(x["contrast"] for x in dd["decisions"] if x.get("contrast", 0) > 0)
            app_decisions = {"controls": dd.get("controls"), "min": q(cs, 0.0),
                             "p50": q(cs, 0.5), "all_ge_4_5": bool(cs) and cs[0] >= 4.5}
        except Exception as ex:
            app_decisions = {"error": str(ex)}

        result = {
            "wallpaper": tag, "scroll": scroll, "active": active, "size": [cw, ch],
            "pixel_audit_valid": scroll == 0,
            "pixel_audit_note": "" if scroll == 0 else
                "滚动后导出矩形与被裁掉的布局位置错位，此组逐块像素值不可采信；"
                "滚动态覆盖以 app_reported_decisions 为准。",
            "app_reported_decisions": app_decisions,
            "exported_blocks": d["text_blocks"], "measured": len(fixed) + len(managed),
            "skipped_out_of_view": skipped,
            "adaptive_blocks": {"n": len(managed), "p50": q([x["contrast"] for x in managed], 0.5),
                                "min": q([x["contrast"] for x in managed], 0.0)},
            "fixed_background_blocks": {"n": len(fixed), "p50": q([x["contrast"] for x in fixed], 0.5),
                                        "min": q([x["contrast"] for x in fixed], 0.0),
                                        "below_4_5": len([x for x in fixed if x["contrast"] < 4.5])},
            "all_below_4_5": below[:20],
            "verdict_fixed_blocks_all_pass": (
                "not_applicable_no_fixed_blocks_in_view" if not fixed
                else all(x["contrast"] >= 4.5 for x in fixed)),
        }
        out = os.path.join(OUT, "phase13-all-text-%s%s.json" % (tag, suffix))
        json.dump(result, open(out, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
        print(json.dumps({k: v for k, v in result.items() if k != "all_below_4_5"},
                         ensure_ascii=False, indent=2))
        if below:
            print("低于 4.5 的块（最多 20）：")
            for b in below[:20]:
                print("  ", b)
        print("证据：%s / %s" % (shot, out))
    finally:
        p10.restore(hidden)
        if proc is not None and proc.poll() is None:
            proc.kill()
            proc.wait(timeout=6)
        open(SETTINGS, "wb").write(cfg_bak)
        p10.wallpaper(original)
        print("配置与壁纸已还原")
    return 0


if __name__ == "__main__":
    sys.exit(main())
