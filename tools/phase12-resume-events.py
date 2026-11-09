#!/usr/bin/env python3
"""阶段 12：验证睡眠恢复 / 解锁 / 显示器变化的处理路径。

不真锁屏、不真睡眠、不拔显示器：把合成的 WM_POWERBROADCAST /
WM_WTSSESSION_CHANGE / WM_DISPLAYCHANGE 直接 Post 到应用的隐藏消息宿主窗口
（标题 DeskLanesMessageHost），再看它有没有触发全量重扫、亮度重算、窗口钳位。

这是**消息级证据**，不能等同于真实睡眠 24h+ 实测；报告里必须如实标注。

用法：python tools/phase12-resume-events.py
"""

import ctypes
import importlib.util
import json
import os
import subprocess
import sys
import time
from ctypes import wintypes

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
DIAG = os.path.join(CFG, "diagnostics", "dynamic-text.json")
LOGDIR = os.path.join(CFG, "logs")
OUT = os.path.join(REPO, "docs", "ui-acrylic-final")

HOST_TITLE = "DeskLanesMessageHost"
WM_POWERBROADCAST = 0x0218
WM_WTSSESSION_CHANGE = 0x02B1
WM_DISPLAYCHANGE = 0x007E
PBT_APMRESUMESUSPEND = 0x0007
WTS_SESSION_UNLOCK = 0x0008

u = ctypes.WinDLL("user32", use_last_error=True)
u.GetWindowTextLengthW.argtypes = [ctypes.c_void_p]
u.GetWindowTextW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p, ctypes.c_int]
u.GetWindowThreadProcessId.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_ulong)]
u.PostMessageW.argtypes = [ctypes.c_void_p, ctypes.c_uint, ctypes.c_void_p, ctypes.c_void_p]
CB = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)


def find_host(pid):
    """隐藏窗口也在 EnumWindows 里，按标题 + 进程号精确找。"""
    hits = []

    @CB
    def cb(h, _):
        n = u.GetWindowTextLengthW(ctypes.c_void_p(h))
        if n <= 0:
            return True
        b = ctypes.create_unicode_buffer(n + 1)
        u.GetWindowTextW(ctypes.c_void_p(h), b, n + 1)
        if b.value != HOST_TITLE:
            return True
        owner = ctypes.c_ulong()
        u.GetWindowThreadProcessId(ctypes.c_void_p(h), ctypes.byref(owner))
        if owner.value == pid:
            hits.append(h)
        return True

    u.EnumWindows(cb, 0)
    return hits[0] if hits else 0


def diag():
    try:
        d = json.load(open(DIAG, encoding="utf-8"))
        return {k: d.get(k) for k in ("luminance_source", "desktop_refreshes",
                                      "forced_refreshes", "controls", "dark_scheme",
                                      "total_flips", "unusable_controls")}
    except Exception as ex:
        return {"error": str(ex)}


def log_state():
    out = {}
    for n in ("sync.log", "errors.log"):
        p = os.path.join(LOGDIR, n)
        out[n] = os.path.getsize(p) if os.path.exists(p) else None
    p = os.path.join(LOGDIR, "sync.log")
    try:
        text = open(p, encoding="utf-8", errors="replace").read()
        out["resume_lines"] = [ln for ln in text.splitlines() if "电源/会话恢复" in ln][-6:]
        out["resync_lines"] = [ln for ln in text.splitlines() if "重扫" in ln][-6:]
        out["rescan_done_lines"] = [ln for ln in text.splitlines() if "恢复后全量重扫完成" in ln][-6:]
    except Exception:
        out["resume_lines"] = []
        out["resync_lines"] = []
        out["rescan_done_lines"] = []
    return out


def post(hwnd, msg, wparam):
    return bool(u.PostMessageW(ctypes.c_void_p(hwnd), ctypes.c_uint(msg),
                               ctypes.c_void_p(wparam), ctypes.c_void_p(0)))


def main():
    screenshot._set_dpi_aware()
    cfg_bak = open(SETTINGS, "rb").read()
    original_wall = p10.wallpaper()
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

        hidden = p10.set_aside(0)
        time.sleep(1.5)
        env = dict(os.environ, DESKLANES_DIAG_TEXT="1", DESKLANES_DYNAMIC_TEXT="1")
        subprocess.run(["taskkill", "/IM", "DeskLanes.exe", "/F"],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        time.sleep(1.0)
        proc = subprocess.Popen([EXE], cwd=os.path.dirname(EXE), env=env,
                                stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT)
        hwnd_app = 0
        for _ in range(60):
            hwnd_app = screenshot.find_window_by_title("快速启动")
            if hwnd_app:
                break
            time.sleep(0.4)
        if not hwnd_app:
            print("FAIL 悬浮窗没出现")
            return 1
        time.sleep(3.0)
        host = find_host(proc.pid)
        print("消息宿主窗口句柄：%s" % host)
        if not host:
            print("FAIL 找不到消息宿主窗口")
            return 1

        r = screenshot.RECT()
        ctypes.windll.user32.GetWindowRect(hwnd_app, ctypes.byref(r))
        ctypes.windll.user32.SetWindowPos(hwnd_app, 0, 520, 240, 0, 0, 0x0001 | 0x0004 | 0x0010)
        time.sleep(0.8)
        hidden += p10.set_aside(hwnd_app)
        time.sleep(1.5)
        ctypes.windll.user32.GetWindowRect(hwnd_app, ctypes.byref(r))
        ctypes.windll.user32.SetCursorPos(r.Left + 60, r.Bottom - 14)
        ctypes.windll.user32.mouse_event(0x0002, 0, 0, 0, 0)
        ctypes.windll.user32.mouse_event(0x0004, 0, 0, 0, 0)
        time.sleep(1.5)

        result = {"method": "synthetic messages posted to the app's hidden message host",
                  "not_a_real_sleep_test": True,
                  "baseline": {"diag": diag(), "logs": log_state()}}

        steps = [
            ("power_resume", WM_POWERBROADCAST, PBT_APMRESUMESUSPEND),
            ("session_unlock", WM_WTSSESSION_CHANGE, WTS_SESSION_UNLOCK),
            ("display_change", WM_DISPLAYCHANGE, 0),
        ]
        result["steps"] = []
        for name, msg, wp in steps:
            before = diag()
            sent = post(host, msg, wp)
            time.sleep(3.5)  # 1.5s debounce + 壁纸解码 + 重扫
            after = diag()
            result["steps"].append({
                "name": name, "message": hex(msg), "wparam": wp, "posted": sent,
                "forced_before": before.get("forced_refreshes"),
                "forced_after": after.get("forced_refreshes"),
                "desktop_refreshes_after": after.get("desktop_refreshes"),
                "source_after": after.get("luminance_source"),
                "ok": sent and (after.get("forced_refreshes") or 0) > (before.get("forced_refreshes") or 0),
            })

        final_logs = log_state()
        result["final"] = {"diag": diag(), "logs": final_logs,
                           "process_alive": proc.poll() is None}
        result["verdict"] = {
            "all_steps_triggered": all(s["ok"] for s in result["steps"]),
            "luminance_source_wallpaper_bitmap": result["final"]["diag"].get("luminance_source") == "wallpaper_bitmap",
            "resume_logged": any("电源/会话恢复" in ln for ln in final_logs["resume_lines"]),
            "full_rescan_completed": len(final_logs.get("rescan_done_lines", [])) >= 2,
            "errors_log_unchanged": final_logs["errors.log"] == result["baseline"]["logs"]["errors.log"],
            "no_crash": result["final"]["process_alive"],
        }
        out = os.path.join(OUT, "phase12-resume-events.json")
        json.dump(result, open(out, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
        print(json.dumps({"steps": [{k: s[k] for k in ("name", "posted", "forced_before", "forced_after", "ok")}
                                    for s in result["steps"]],
                          "verdict": result["verdict"],
                          "resume_log_tail": final_logs["resume_lines"][-3:],
                          "rescan_done_tail": final_logs.get("rescan_done_lines", [])[-3:]},
                         ensure_ascii=False, indent=2))
        print("证据：%s" % out)
    finally:
        p10.restore(hidden)
        if proc is not None and proc.poll() is None:
            proc.kill()
            proc.wait(timeout=6)
        open(SETTINGS, "wb").write(cfg_bak)
        p10.wallpaper(original_wall)
        print("配置与壁纸已还原")
    return 0


if __name__ == "__main__":
    sys.exit(main())
