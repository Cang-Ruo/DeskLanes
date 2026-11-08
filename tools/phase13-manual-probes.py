#!/usr/bin/env python3
"""阶段 13-C：为「需要人配合」的四项验收提供可复现的前后状态采集工具。

本工具**不会**锁屏、睡眠、拔显示器、改分辨率或改任何系统设置。
它只在事件前后各拍一份状态快照，再比对。真实事件由你手动触发。

用法：
  # 1) 看四项的操作步骤
  python tools/phase13-manual-probes.py plan

  # 2) 真实锁屏 / 睡眠 / 热插拔 / 远程桌面前后各采集一次
  python tools/phase13-manual-probes.py capture --label=before
  ... 你手动做那件事（锁屏再解锁 / 睡眠再唤醒 / 改分辨率 / 连远程桌面）...
  python tools/phase13-manual-probes.py capture --label=after
  python tools/phase13-manual-probes.py compare --before=... --after=...

  # 3) 只读查看当前是不是远程会话
  python tools/phase13-manual-probes.py rdp

  # 4) 24h+ 长时驻留（另开终端跑，会用私有副本，不占安装版）
  python tools/soak-8h.py --exe publish/win-x64-selfcontained/DeskLanes.exe \
      --out docs/stage13-soak/soak-24h.csv --hours 24 --interval-min 5 \
      --errorlog "%LOCALAPPDATA%/DeskLanes/logs/errors.log"
"""

import argparse
import ctypes
import json
import os
import sys
import time
from ctypes import wintypes

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import screenshot  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CFG = os.path.join(os.environ.get("LOCALAPPDATA", ""), "DeskLanes")
OUT = os.path.join(REPO, "docs", "ui-acrylic-final")

u = ctypes.windll.user32
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass

SM_REMOTESESSION = 0x1000
DWM_ATTRS = {"dark_mode": 20, "system_backdrop": 38, "corner": 33}


class RECT(ctypes.Structure):
    _fields_ = [("l", ctypes.c_long), ("t", ctypes.c_long),
                ("r", ctypes.c_long), ("b", ctypes.c_long)]


class MONITOR_INFO(ctypes.Structure):
    _fields_ = [("cbSize", wintypes.DWORD), ("rcMonitor", RECT), ("rcWork", RECT),
                ("dwFlags", wintypes.DWORD)]


def monitors():
    res = []
    CB = ctypes.WINFUNCTYPE(ctypes.c_int, ctypes.c_ulong, ctypes.c_ulong,
                            ctypes.POINTER(RECT), ctypes.c_double)

    @CB
    def cb(h, hdc, lprc, data):
        mi = MONITOR_INFO()
        mi.cbSize = ctypes.sizeof(MONITOR_INFO)
        u.GetMonitorInfoW(ctypes.c_void_p(h), ctypes.byref(mi))
        m = mi.rcMonitor
        res.append({"rect": [m.l, m.t, m.r, m.b], "primary": bool(mi.dwFlags & 1)})
        return 1

    u.EnumDisplayMonitors(None, None, cb, 0)
    return res


def dwm(hwnd, attr):
    v = ctypes.c_int(0)
    hr = ctypes.windll.dwmapi.DwmGetWindowAttribute(
        ctypes.c_void_p(hwnd), attr, ctypes.byref(v), 4)
    return v.value if hr == 0 else None


def windows():
    out = []
    CB = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)

    @CB
    def cb(h, _):
        n = u.GetWindowTextLengthW(ctypes.c_void_p(h))
        if n <= 0:
            return True
        b = ctypes.create_unicode_buffer(n + 1)
        u.GetWindowTextW(ctypes.c_void_p(h), b, n + 1)
        if "DeskLanes" not in b.value:
            return True
        r = RECT()
        u.GetWindowRect(ctypes.c_void_p(h), ctypes.byref(r))
        out.append({
            "title": b.value,
            # 这两个是隐藏的辅助窗口（GDI+ 内部窗口 / 消息宿主），
            # 本来就在屏幕外，不能拿"窗口是否在虚拟屏内"去判它们。
            "helper": ("GDI+" in b.value or "MessageHost" in b.value),
            "visible": bool(u.IsWindowVisible(ctypes.c_void_p(h))),
            "minimized": bool(u.IsIconic(ctypes.c_void_p(h))),
            "rect": [r.l, r.t, r.r, r.b],
            "dwm": {k: dwm(h, a) for k, a in DWM_ATTRS.items()},
        })
        return True

    u.EnumWindows(cb, 0)
    return out


def diag():
    p = os.path.join(CFG, "diagnostics", "dynamic-text.json")
    try:
        d = json.load(open(p, encoding="utf-8"))
        return {k: d.get(k) for k in ("captured_at", "luminance_source", "desktop_refreshes",
                                      "forced_refreshes", "controls", "dark_scheme", "total_flips")}
    except Exception:
        return None


def snapshot():
    logs = {}
    for n in ("sync.log", "errors.log"):
        p = os.path.join(CFG, "logs", n)
        logs[n] = os.path.getsize(p) if os.path.exists(p) else None
    return {
        "captured_at": time.strftime("%Y-%m-%d %H:%M:%S"),
        "remote_session": bool(u.GetSystemMetrics(SM_REMOTESESSION)),
        "monitor_count": u.GetSystemMetrics(80),
        "virtual_screen": [u.GetSystemMetrics(x) for x in (76, 77, 78, 79)],
        "monitors": monitors(),
        "windows": windows(),
        "logs": logs,
        "dynamic_text": diag(),
    }


def compare(before, after):
    issues = []
    if not before["windows"] or not after["windows"]:
        issues.append("至少一次快照没有采集到 DeskLanes 窗口：应用当时在运行吗？")
    if before["monitor_count"] != after["monitor_count"] or before["virtual_screen"] != after["virtual_screen"]:
        issues.append("显示器拓扑变了（预期，检查窗口是否被钳回可见区）")
    for wa in after["windows"]:
        if wa.get("helper") or not wa["visible"] or wa["minimized"]:
            continue
        vx, vy, vw, vh = after["virtual_screen"]
        r = wa["rect"]
        if r[0] < vx - 24 or r[1] < vy - 24 or r[2] > vx + vw + 24 or r[3] > vy + vh + 24:
            issues.append("窗口跑到虚拟屏外：%s %s" % (wa["title"], r))
        if wa["dwm"].get("system_backdrop") not in (0, 3, 2, None):
            issues.append("DWM 背景属性异常：%s %s" % (wa["title"], wa["dwm"]))
    if (after["logs"].get("errors.log") or 0) > (before["logs"].get("errors.log") or 0):
        issues.append("errors.log 变大了（恢复期间有异常）")
    bd, ad = before.get("dynamic_text") or {}, after.get("dynamic_text") or {}
    refresh_delta = (ad.get("forced_refreshes") or 0) - (bd.get("forced_refreshes") or 0)
    return {
        "before": before["captured_at"], "after": after["captured_at"],
        "remote_session": [before["remote_session"], after["remote_session"]],
        "monitor_count": [before["monitor_count"], after["monitor_count"]],
        "virtual_screen": [before["virtual_screen"], after["virtual_screen"]],
        "windows_before": len(before["windows"]), "windows_after": len(after["windows"]),
        "errors_log": [before["logs"].get("errors.log"), after["logs"].get("errors.log")],
        "forced_refreshes_delta": refresh_delta,
        "resume_event_observed": refresh_delta > 0,
        "issues": issues,
    }


PLAN = """\
四项需要你配合的验收（工具不会替你做这些动作）

1) 真实锁屏 / 睡眠恢复
   capture --label=before
   Win+L 锁屏 -> 解锁（或开始菜单 -> 电源 -> 睡眠 -> 唤醒）
   capture --label=after
   compare
   判据：resume_event_observed=true、errors_log 不增长、窗口都在虚拟屏内。

2) 跨天 24h+ 长时驻留
   python tools/soak-8h.py --exe publish/win-x64-selfcontained/DeskLanes.exe --out docs/stage13-soak/soak-24h.csv --hours 24 --interval-min 5 --errorlog "%LOCALAPPDATA%/DeskLanes/logs/errors.log"
   判据：私有内存走平、句柄/GDI/USER/线程不涨、errors.log 零增长。
   已有 6 小时 72 点结论（docs/stage9-8-soak/report.md），24h+ 仍需补。

3) 运行中真实热插拔 / 改分辨率
   capture --label=before
   在 Windows 显示设置里改分辨率或拔/插显示器 -> 等待 3 秒
   capture --label=after
   compare
   判据：窗口被钳回可见区、DWM backdrop 仍为 3、resume_event_observed=true。
   注意：本工具**不会**自己改分辨率，避免打断你正在做的事。

4) 远程桌面会话
   rdp  （只读，返回当前是否远程会话）
   在远程会话里重复第 1 步的采集/比对。
   判据：DWM 背景若回退为不透明，文字仍可读、无异常日志。

所有 capture 结果默认写到 docs/ui-acrylic-final/phase13-probe-<label>.json。
"""


def main():
    ap = argparse.ArgumentParser(description="人工配合验收的前后状态采集（只读）")
    ap.add_argument("command", choices=("plan", "capture", "compare", "rdp"))
    ap.add_argument("--label", default="snapshot")
    ap.add_argument("--before")
    ap.add_argument("--after")
    ap.add_argument("--out")
    args = ap.parse_args()

    if args.command == "plan":
        print(PLAN)
        return 0
    if args.command == "rdp":
        print(json.dumps({"remote_session": bool(u.GetSystemMetrics(SM_REMOTESESSION)),
                          "note": "只读检查；连上远程桌面后再跑一次对比"},
                         ensure_ascii=False, indent=2))
        return 0
    if args.command == "capture":
        data = snapshot()
        path = args.out or os.path.join(OUT, "phase13-probe-%s.json" % args.label)
        json.dump(data, open(path, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
        print(json.dumps({"saved": path, "captured_at": data["captured_at"],
                          "windows": len(data["windows"]),
                          "remote_session": data["remote_session"]},
                         ensure_ascii=False, indent=2))
        return 0
    if not args.before or not args.after:
        print("compare 需要 --before 和 --after 两个 JSON 路径")
        return 2
    b = json.load(open(args.before, encoding="utf-8"))
    a = json.load(open(args.after, encoding="utf-8"))
    result = compare(b, a)
    path = args.out or os.path.join(OUT, "phase13-probe-compare.json")
    json.dump(result, open(path, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
    print(json.dumps(result, ensure_ascii=False, indent=2))
    print("结果：%s" % path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
