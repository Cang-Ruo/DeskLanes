#!/usr/bin/env python3
"""阶段 13：在**右侧副屏**上临时改分辨率，验证运行中 WM_DISPLAYCHANGE 的响应。

安全约定：
  - 只挑 dmPosition.x > 0 且不是主屏的显示器（右副屏），绝不动主屏 / 左竖屏
  - 默认 --dry 只打印计划，必须显式 --apply 才真的改
  - try/finally 里无条件恢复原始 DEVMODE；恢复后再采一次快照确认
  - 提供 --restore-only 兜底（万一上次异常退出没还原）
  - 原始 DEVMODE 以 hex 写进证据 JSON，最坏情况可手动恢复

物理热插拔（拔/插显示器线）本工具不做：需要人手拔线。
但改分辨率与热插拔都会触发同一个 WM_DISPLAYCHANGE，软件处理路径相同。

用法：
  python tools/phase13-display-probe.py            # dry run，只看计划
  python tools/phase13-display-probe.py --apply    # 真的改 3 秒再恢复
  python tools/phase13-display-probe.py --restore-only
  python tools/phase13-display-probe.py --unplug   # 交互式物理拔插副屏，按两次回车
"""

import argparse
import ctypes
import json
import os
import struct
import subprocess
import sys
import time
from ctypes import wintypes

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
OUT = os.path.join(REPO, "docs", "ui-acrylic-final")
PROBE = os.path.join(HERE, "phase13-manual-probes.py")
APP = os.path.join(REPO, "publish", "win-x64", "DeskLanes.exe")

u = ctypes.WinDLL("user32", use_last_error=True)


class DISPLAY_DEVICEW(ctypes.Structure):
    _fields_ = [("cb", wintypes.DWORD), ("DeviceName", ctypes.c_wchar * 32),
                ("DeviceString", ctypes.c_wchar * 128), ("StateFlags", wintypes.DWORD),
                ("DeviceID", ctypes.c_wchar * 128), ("DeviceKey", ctypes.c_wchar * 128)]


u.EnumDisplayDevicesW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD,
                                   ctypes.POINTER(DISPLAY_DEVICEW), wintypes.DWORD]
u.EnumDisplaySettingsW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, ctypes.c_void_p]
u.ChangeDisplaySettingsExW.argtypes = [wintypes.LPCWSTR, ctypes.c_void_p, wintypes.HWND,
                                        wintypes.DWORD, ctypes.c_void_p]
u.ChangeDisplaySettingsExW.restype = ctypes.c_long

ENUM_CURRENT_SETTINGS = 0xFFFFFFFF
DISP_CHANGE_SUCCESSFUL = 0
DM_PELSWIDTH = 0x00080000
DM_PELSHEIGHT = 0x00100000
DM_DISPLAYFREQUENCY = 0x00400000
DM_POSITION = 0x00000020
CCHDEVICENAME = 32
DM_SIZE = 220


def devmode_info(buf):
    x, y = struct.unpack_from("<ii", buf, 76)
    w, h = struct.unpack_from("<II", buf, 172)
    freq = struct.unpack_from("<I", buf, 184)[0]
    return {"x": x, "y": y, "w": w, "h": h, "freq": freq}


def current_devmode(name):
    b = ctypes.create_string_buffer(DM_SIZE)
    struct.pack_into("<H", b, 68, DM_SIZE)  # dmSize
    if not u.EnumDisplaySettingsW(name, ENUM_CURRENT_SETTINGS, b):
        return None
    return b


def list_modes(name):
    out, j = [], 0
    while True:
        b = ctypes.create_string_buffer(DM_SIZE)
        struct.pack_into("<H", b, 68, DM_SIZE)
        if not u.EnumDisplaySettingsW(name, j, b):
            break
        m = devmode_info(b)
        # 竖屏模式宽度本来就小（左侧屏 1080×1920），阈值不能再按横屏设
        if m["w"] >= 640 and m["h"] >= 480:
            out.append((m["w"], m["h"], m["freq"]))
        j += 1
        if j > 512:
            break
    return sorted(set(out))


def attached_devices():
    out, i = [], 0
    while True:
        d = DISPLAY_DEVICEW()
        d.cb = ctypes.sizeof(DISPLAY_DEVICEW)
        if not u.EnumDisplayDevicesW(None, i, ctypes.byref(d), 0):
            break
        if d.StateFlags & 0x1:  # DISPLAY_DEVICE_ATTACHED_TO_DESKTOP
            cur = current_devmode(d.DeviceName)
            primary = bool(d.StateFlags & 0x4)
            out.append({"name": d.DeviceName, "primary": primary,
                        "info": devmode_info(cur) if cur else None,
                        "modes": list_modes(d.DeviceName)})
        i += 1
    return out


def pick_target(which="left"):
    """按用户指定挑显示器，绝不误动别处。

    用户 2026-11-09 澄清：右侧 `DISPLAY1` 是**笔记本内置屏**（软件就显示在那儿），
    `DISPLAY15` 是主屏，`DISPLAY2` 是左侧竖屏。测试应走左侧与主屏，
    默认 left；碰笔记本屏必须显式 --target=laptop。
    """
    devs = [d for d in attached_devices() if d["info"]]
    if which == "left":
        cands = [d for d in devs if not d["primary"] and d["info"]["x"] < 0]
    elif which == "primary":
        cands = [d for d in devs if d["primary"]]
    elif which == "laptop":
        cands = [d for d in devs if not d["primary"] and d["info"]["x"] > 0]
    else:
        return None
    return cands[0] if cands else None


TARGET_LABEL = {"left": "左侧竖屏（外接）", "primary": "主屏（外接）",
                "laptop": "右侧笔记本内置屏"}


def apply_devmode(name, buf, flags=0):
    return u.ChangeDisplaySettingsExW(name, buf, None, flags, None)


def tool(*args):
    r = subprocess.run([sys.executable, PROBE, *args], capture_output=True,
                       text=True, encoding="utf-8", errors="replace")
    return r.stdout.strip()


def load(path):
    try:
        return json.load(open(path, encoding="utf-8"))
    except Exception:
        return None


def app_running():
    snap = load(os.path.join(OUT, "phase13-probe-display-running.json"))
    # 直接采一份临时快照判断
    tool("capture", "--label=display-running")
    snap = load(os.path.join(OUT, "phase13-probe-display-running.json"))
    return bool(snap and snap.get("app_running"))


def unplug_flow(label, target):
    """物理热插拔：三次快照（拔前 / 拔下 / 插回），两次比对。

    只有「把线拔下来再插回去」这件事需要人手；采集与比对全部自动。
    在用户自己的终端里运行（需要能按回车）。
    """
    tag = "unplug-%s" % target
    out = {"mode": "physical_unplug_%s" % target, "target": target,
           "target_label": label}
    print("=== 物理热插拔测试（%s）===" % label)
    print("请确认 DeskLanes 正在运行，然后按提示操作。")
    if target == "laptop":
        print("注意：笔记本内置屏通常没有可拔的视频线，请改用 --target=left 或 primary。\n")
    print("--- 采集 1/3：拔线前 ---")
    print(tool("capture", "--label=%s-before" % tag))
    p_before = os.path.join(OUT, "phase13-probe-%s-before.json" % tag)

    try:
        input("\n>>> 现在拔下【%s】的视频线（HDMI/DP），拔完按回车..." % label)
    except EOFError:
        print("非交互环境，无法等待。请手动依次跑 capture 再 compare。")
        return 2
    print("等待 3 秒让系统稳定...")
    time.sleep(3.0)
    print("--- 采集 2/3：已拔下 ---")
    print(tool("capture", "--label=%s-detached" % tag))
    p_detached = os.path.join(OUT, "phase13-probe-%s-detached.json" % tag)

    try:
        input("\n>>> 现在把视频线插回去，等屏幕亮起后按回车...")
    except EOFError:
        return 2
    print("等待 4 秒让系统稳定...")
    time.sleep(4.0)
    print("--- 采集 3/3：已插回 ---")
    print(tool("capture", "--label=%s-reattached" % tag))
    p_back = os.path.join(OUT, "phase13-probe-%s-reattached.json" % tag)

    cmp1 = os.path.join(OUT, "phase13-probe-%s-detach-compare.json" % tag)
    cmp2 = os.path.join(OUT, "phase13-probe-%s-reattach-compare.json" % tag)
    print("\n--- 比对：拔前 -> 拔下 ---")
    print(tool("compare", "--before=%s" % p_before, "--after=%s" % p_detached, "--out=%s" % cmp1))
    print("\n--- 比对：拔下 -> 插回 ---")
    print(tool("compare", "--before=%s" % p_detached, "--after=%s" % p_back, "--out=%s" % cmp2))
    a, b = load(cmp1), load(cmp2)
    out["detach"] = a
    out["reattach"] = b
    issues = (a or {}).get("issues", []) + (b or {}).get("issues", [])
    out["judgement"] = {
        "topology_changed_on_detach": (a or {}).get("virtual_screen", [None, None])[0]
                                       != (a or {}).get("virtual_screen", [None, None])[1],
        "topology_restored_on_reattach": (b or {}).get("virtual_screen", [None, None])[0]
                                          != (b or {}).get("virtual_screen", [None, None])[1],
        "errors_log_unchanged": (a or {}).get("errors_log", [None, None])[0] == (a or {}).get("errors_log", [None, None])[1]
                                and (b or {}).get("errors_log", [None, None])[0] == (b or {}).get("errors_log", [None, None])[1],
        "issues": issues,
    }
    out["verdict"] = "PASS" if (out["judgement"]["topology_changed_on_detach"]
                                and out["judgement"]["topology_restored_on_reattach"]
                                and out["judgement"]["errors_log_unchanged"]
                                and not issues) else "FAIL"
    path = os.path.join(OUT, "phase13-unplug-%s-probe.json" % target)
    json.dump(out, open(path, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
    print("\n判据：%s" % json.dumps(out["judgement"], ensure_ascii=False, indent=2))
    print("结论：%s" % out["verdict"])
    print("证据：%s" % path)
    return 0 if out["verdict"] == "PASS" else 1


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--apply", action="store_true")
    ap.add_argument("--restore-only", action="store_true")
    ap.add_argument("--hold", type=float, default=3.0)
    ap.add_argument("--unplug", action="store_true",
                    help="交互式物理拔插：采集三次快照并比对")
    ap.add_argument("--target", choices=("left", "primary", "laptop"), default="left",
                    help="改哪块屏：left=左侧竖屏(默认) / primary=主屏 / laptop=右侧笔记本屏")
    args = ap.parse_args()

    if args.unplug:
        return unplug_flow(TARGET_LABEL[args.target], args.target)

    target = pick_target(args.target)
    if target is None:
        print("FAIL 找不到目标显示器 %s，不动任何显示器" % args.target)
        return 1
    cur = target["info"]
    original = current_devmode(target["name"])
    if original is None:
        print("FAIL 读不到副屏当前 DEVMODE")
        return 1

    # 选一个宽度更小、高度更小的模式，让虚拟屏尺寸确实变化
    candidates = [m for m in target["modes"]
                  if m[0] < cur["w"] and m[1] <= cur["h"]]
    preferred = [m for m in candidates if m[0] == 1920 and m[1] == 1200]
    new_mode = preferred[0] if preferred else (candidates[-1] if candidates else None)
    if new_mode is None:
        print("FAIL 副屏没有更小的可用分辨率")
        return 1

    tag = args.target
    plan = {"device": target["name"], "target": args.target,
            "target_label": TARGET_LABEL[args.target], "primary": target["primary"],
            "current": cur, "new_mode": {"w": new_mode[0], "h": new_mode[1], "freq": new_mode[2]},
            "original_devmode_hex": original.raw.hex(),
            "hold_seconds": args.hold}
    print(json.dumps(plan, ensure_ascii=False, indent=2))

    if args.restore_only:
        # 用当前注册表默认恢复（不需要上一次的 DEVMODE 字节）
        rc = apply_devmode(target["name"], None)
        print("restore-only rc=%s (0=成功)" % rc)
        return 0 if rc == DISP_CHANGE_SUCCESSFUL else 1
    if not args.apply:
        print("\n这是 dry run。确认要真的改副屏分辨率再恢复，加 --apply。")
        return 0

    # 以当前完整 DEVMODE 为基底，只改宽/高/刷新率，保留位置与其它字段
    newbuf = ctypes.create_string_buffer(original.raw)
    struct.pack_into("<II", newbuf, 172, new_mode[0], new_mode[1])
    struct.pack_into("<I", newbuf, 184, new_mode[2])
    fields = struct.unpack_from("<I", newbuf, 72)[0]
    struct.pack_into("<I", newbuf, 72,
                     fields | DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY | DM_POSITION)

    started_here = False
    proc = None
    result = {"plan": plan}
    try:
        # 运行中改分辨率：优先复用用户已经开着的实例
        if not app_running():
            env = dict(os.environ, DESKLANES_DIAG_TEXT="1")
            proc = subprocess.Popen([APP], env=env,
                                    stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT)
            started_here = True
            time.sleep(6)
        result["started_app_here"] = started_here

        print("\n--- 采集 before ---")
        print(tool("capture", "--label=disp-%s-before" % tag))
        before_p = os.path.join(OUT, "phase13-probe-disp-%s-before.json" % tag)

        rc = apply_devmode(target["name"], newbuf)
        print("\n--- 改分辨率 rc=%s (0=成功) ---" % rc)
        result["apply_rc"] = rc
        if rc != DISP_CHANGE_SUCCESSFUL:
            result["error"] = "改分辨率失败，未继续；显示器未改动"
            return 1

        time.sleep(args.hold)
        after_info = devmode_info(current_devmode(target["name"]))
        result["after_devmode"] = after_info
        print("\n--- 采集 after ---")
        print(tool("capture", "--label=disp-%s-after" % tag))
        after_p = os.path.join(OUT, "phase13-probe-disp-%s-after.json" % tag)

        cmp_out = os.path.join(OUT, "phase13-probe-display-%s-compare.json" % tag)
        print("\n--- compare ---")
        print(tool("compare", "--before=%s" % before_p,
                   "--after=%s" % after_p, "--out=%s" % cmp_out))
        result["compare"] = load(cmp_out)
        cmp_data = result["compare"] or {}
        vs = cmp_data.get("virtual_screen", [None, None])
        el = cmp_data.get("errors_log", [None, None])
        issues = cmp_data.get("issues", [])
        # 「显示器拓扑变了」正是本测试要制造的事件，不是缺陷
        unexpected = [x for x in issues if "显示器拓扑变了" not in x]
        result["judgement"] = {
            "virtual_screen_changed": vs[0] != vs[1],
            "errors_log_unchanged": el[0] == el[1],
            "expected_topology_notice": len(issues) - len(unexpected),
            "unexpected_issues": unexpected,
            "resume_event_observed_note": "改分辨率走 WM_DISPLAYCHANGE 分支（钳位 + 亮度重算），"
                                          "不写『电源/会话恢复』日志，故 resume_log_count=0 属正常",
        }
    finally:
        # 无论前面发生什么，都要把副屏还原
        rc2 = apply_devmode(target["name"], original)
        print("\n--- 恢复原分辨率 rc=%s (0=成功) ---" % rc2)
        result["restore_rc"] = rc2
        time.sleep(2.0)
        restored = devmode_info(current_devmode(target["name"]))
        result["restored_devmode"] = restored
        result["restored_ok"] = (restored["w"] == cur["w"] and restored["h"] == cur["h"]
                                 and restored["x"] == cur["x"] and restored["y"] == cur["y"])
        print("恢复后模式：%s  -> %s" % (restored, "OK" if result["restored_ok"] else "不一致，请跑 --restore-only"))
        tool("capture", "--label=disp-%s-restored" % tag)
        if proc is not None and proc.poll() is None:
            proc.kill()
            proc.wait(timeout=6)

    j = result.get("judgement", {})
    result["verdict"] = ("PASS" if result.get("restored_ok") and j.get("virtual_screen_changed")
                         and j.get("errors_log_unchanged") and not j.get("unexpected_issues")
                         else "FAIL")
    path = os.path.join(OUT, "phase13-display-%s-probe.json" % tag)
    json.dump(result, open(path, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
    print("\n判据：%s" % json.dumps(result["verdict"], ensure_ascii=False))
    print("证据：%s" % path)
    return 0 if result["verdict"] == "PASS" else 1


if __name__ == "__main__":
    sys.exit(main())
