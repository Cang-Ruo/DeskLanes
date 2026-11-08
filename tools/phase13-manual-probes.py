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
  python tools/phase13-manual-probes.py compare     # 自动读上面两个文件，不用带长参数

  # 3) 只读查看当前是不是远程会话
  python tools/phase13-manual-probes.py rdp

  # 4) 24h+ 长时驻留：跑 `plan` 看完整单行命令
"""

# PowerShell 里换行符是反引号 ` 而不是反斜杠 \，命令行一律给单行，避免用户踩坑。

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
# 真正要检查的四个业务窗口；同进程里的 WPF/IME/消息宿主辅助窗口一律标 helper
APP_TITLES = {"DeskLanes", "DeskLanes 快速启动", "DeskLanes 侧边栏", "DeskLanes 设置"}


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


k32 = ctypes.windll.kernel32
k32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
k32.OpenProcess.restype = wintypes.HANDLE
k32.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD,
                                           wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
k32.CloseHandle.argtypes = [wintypes.HANDLE]
u.GetWindowThreadProcessId.argtypes = [ctypes.c_void_p, ctypes.POINTER(wintypes.DWORD)]


def process_name(pid):
    """取进程可执行文件名，用来精确识别 DeskLanes.exe 的窗口。"""
    handle = k32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
    if not handle:
        return None
    try:
        buf = ctypes.create_unicode_buffer(1024)
        size = wintypes.DWORD(1024)
        ok = k32.QueryFullProcessImageNameW(handle, 0, buf, ctypes.byref(size))
        return os.path.basename(buf.value) if ok else None
    finally:
        k32.CloseHandle(handle)


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
        # 只认 DeskLanes.exe 自己的窗口。按标题匹配会把
        # 「DeskLanes - 文件资源管理器」也算进来（阶段 13 踩过：软件其实没运行，
        # 却采到 1 个窗口，看起来像成功）。
        pid = wintypes.DWORD()
        u.GetWindowThreadProcessId(ctypes.c_void_p(h), ctypes.byref(pid))
        pname = process_name(pid.value)
        if not pname or pname.lower() != "desklanes.exe":
            return True
        r = RECT()
        u.GetWindowRect(ctypes.c_void_p(h), ctypes.byref(r))
        out.append({
            "title": b.value,
            "pid": pid.value,
            # 同进程还有一堆 WPF/IME/消息宿主辅助窗口（Hidden Window、
            # MSCTFIME UI 等），本来就在屏幕外，不能拿"窗口是否在虚拟屏内"判它们。
            "helper": b.value not in APP_TITLES,
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
    wins = windows()
    return {
        # 带毫秒：两次采集若在同一秒也能分辨，避免"看起来做过、其实没做"
        "captured_at": time.strftime("%Y-%m-%d %H:%M:%S") + ".%03d" % (int(time.time() * 1000) % 1000),
        "app_running": len(wins) > 0,
        "remote_session": bool(u.GetSystemMetrics(SM_REMOTESESSION)),
        "monitor_count": u.GetSystemMetrics(80),
        "virtual_screen": [u.GetSystemMetrics(x) for x in (76, 77, 78, 79)],
        "monitors": monitors(),
        "windows": wins,
        "logs": logs,
        "dynamic_text": diag(),
    }


def compare(before, after):
    issues = []
    if not before.get("app_running", bool(before["windows"])) or not after.get("app_running", bool(after["windows"])):
        issues.append("采集时 DeskLanes 没有运行：本次结果无效，请先启动软件再重做")
    if before["captured_at"] == after["captured_at"]:
        issues.append("两次采集时间戳相同：中间那步操作很可能没有真正执行")
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
        "app_running": [before.get("app_running"), after.get("app_running")],
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

【前提】先让 DeskLanes 跑起来：按 Win 键输入 DeskLanes 回车。
       采集结果里 app_running 必须是 true，否则这次数据无效。
【注意】下面命令都在 PowerShell 里跑，全部单行；不要加反斜杠 \\ 续行
       （那是 Bash 的写法，PowerShell 的续行符是反引号 `）。

1) 真实锁屏 / 睡眠恢复
   python tools/phase13-manual-probes.py capture --label=before
   >>> 现在真的按 Win+L 锁屏，输入密码解锁回到桌面，等 3 秒 <<<
   python tools/phase13-manual-probes.py capture --label=after
   python tools/phase13-manual-probes.py compare
   判据：resume_event_observed=true、errors_log 不增长、issues=[]。

2) 跨天 24h+ 长时驻留（新开一个终端挂着别关）
   mkdir docs\\stage13-soak
   python tools/soak-8h.py --exe publish/win-x64-selfcontained/DeskLanes.exe --out docs/stage13-soak/soak-24h.csv --hours 24 --interval-min 5 --errorlog "C:/Users/TTjustTT/AppData/Local/DeskLanes/logs/errors.log"
   判据：私有内存走平、句柄/GDI/USER/线程不涨、errors.log 零增长。
   已有 6 小时 72 点结论（docs/stage9-8-soak/report.md），24h+ 仍需补。
   注意：这个脚本会阻止机器睡眠，别和第 1 条的"睡眠唤醒"同时做。

3) 运行中真实热插拔 / 改分辨率
   python tools/phase13-manual-probes.py capture --label=res-before
   >>> 在 Windows 显示设置里改一下分辨率再改回，或拔/插显示器，等 3 秒 <<<
   python tools/phase13-manual-probes.py capture --label=res-after
   python tools/phase13-manual-probes.py compare --before=docs/ui-acrylic-final/phase13-probe-res-before.json --after=docs/ui-acrylic-final/phase13-probe-res-after.json
   判据：窗口被钳回可见区、DWM backdrop 仍为 3、resume_event_observed=true。
   注意：本工具不会自己改分辨率，避免打断你正在做的事。

4) 远程桌面会话
   python tools/phase13-manual-probes.py rdp
   在远程会话里重复第 1 步的三条命令。
   判据：DWM 背景若回退为不透明，文字仍可读、无异常日志。

compare 不带参数时自动读 phase13-probe-before.json / phase13-probe-after.json。
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
                          "app_running": data["app_running"],
                          "windows": len(data["windows"]),
                          "remote_session": data["remote_session"]},
                         ensure_ascii=False, indent=2))
        if not data["app_running"]:
            print("警告：DeskLanes 没在运行，这份快照无效。先启动软件再采集。")
        return 0
    # 默认读 before/after 两个文件，用户不必敲 --before/--after 长参数
    before_path = args.before or os.path.join(OUT, "phase13-probe-before.json")
    after_path = args.after or os.path.join(OUT, "phase13-probe-after.json")
    for p in (before_path, after_path):
        if not os.path.exists(p):
            print("找不到 %s" % p)
            print("请先跑：python tools/phase13-manual-probes.py capture --label=before / after")
            return 2
    b = json.load(open(before_path, encoding="utf-8"))
    a = json.load(open(after_path, encoding="utf-8"))
    result = compare(b, a)
    path = args.out or os.path.join(OUT, "phase13-probe-compare.json")
    json.dump(result, open(path, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
    print(json.dumps(result, ensure_ascii=False, indent=2))
    print("结果：%s" % path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
