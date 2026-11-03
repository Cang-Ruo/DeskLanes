#!/usr/bin/env python3
"""玻璃材质的因果实验：把窗口背后的东西换掉，看它跟不跟着变。

相关系数那类指标再怎么算，都是在猜"这是不是背景"。
这个实验是直接问：背景变了，窗口会不会变？

做法：
  1. 窗口化跑 UiSnapshot，窗口停住
  2. 背景是桌面时，抓窗口矩形 A
  3. 开一个**最大化**的记事本（纯白）垫在 DeskLanes 窗口下面
  4. 再抓同一块矩形 B
  5. 比较 A 与 B：如果真的透光，垫上白底之后窗口应该明显变亮

对照组：DESKLANES_BACKDROP=none（不透明回退）跑一遍，AB 应当几乎一样。

用法：
    python tools/glass-causal.py <mode> <输出目录> [--demo]
"""

import ctypes
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # 保证能 import screenshot
import screenshot  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SNAPSHOT_EXE = os.path.join(
    REPO, "tests", "DeskLanes.UiSnapshot", "bin", "Release", "net10.0-windows",
    "DeskLanes.UiSnapshot.exe")

TITLE_KEYWORDS = {
    "category": "DeskLanes",
    "panels": "DeskLanes",
    "floating": "快速启动",
    "sidebar": "侧边栏",
    "settings": "设置",
    # 参照窗口：只贴材质、没有 UI 内容，异步加载那一套它都没有，取样最稳
    "reference": "参照",
}

HWND_TOP = 0
SWP_NOSIZE = 0x0001
SWP_NOMOVE = 0x0002
SWP_NOACTIVATE = 0x0010


def raise_window(hwnd):
    ctypes.windll.user32.SetWindowPos(
        hwnd, HWND_TOP, 0, 0, 0, 0,
        SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE)


def minimize_others(keep_title):
    skip = ("Shell_TrayWn", "Shell_SecondaryTrayWn", "Progman", "WorkerW",
            "DV2ControlHost", "Butt")

    def scan(hwnd, _):
        try:
            cls = screenshot._class(hwnd)
            title = screenshot._title(hwnd)
        except Exception:
            return True
        if not title or not cls or any(s in cls for s in skip):
            return True
        if keep_title and keep_title in title:
            return True
        if not ctypes.windll.user32.IsWindowVisible(hwnd):
            return True
        ctypes.windll.user32.ShowWindow(hwnd, 6)
        return True

    ctypes.windll.user32["EnumWindows"](ctypes.WINFUNCTYPE("__stdcall", ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)(scan), 0)
    return []


def same_enough(a, b, tol=0.5):
    """两次抓帧几乎一样才算稳定（窗口内容还在动时不能抓）。"""
    if len(a) != len(b):
        return False
    step = max(3, len(a) // 9000 * 3)
    diff = 0
    count = 0
    for i in range(0, len(a), step):
        diff += abs(a[i] - b[i])
        count += 1
    return diff / max(1, count) <= tol


def mean_lum(rgb, width, height, cells=40):
    """按格子算平均亮度：总均值会被 UI 内容平均掉，分格才能看清空白处怎么变。"""
    vals = []
    for gy in range(cells):
        y0 = height * gy // cells
        y1 = max(y0 + 1, height * (gy + 1) // cells)
        for gx in range(cells):
            x0 = width * gx // cells
            x1 = max(x0 + 1, width * (gx + 1) // cells)
            total = 0
            count = 0
            for y in range(y0, y1, max(1, (y1 - y0) // 6)):
                base = y * width * 3
                for x in range(x0, x1, max(1, (x1 - x0) // 6)):
                    i = base + x * 3
                    total += (rgb[i] * 299 + rgb[i + 1] * 587 + rgb[i + 2] * 114) // 1000
                    count += 1
            vals.append(total / max(1, count))
    return vals


def run(mode, demo, forced, tag, out_dir):
    env = dict(os.environ)
    if forced:
        env["DESKLANES_BACKDROP"] = forced

    args = [SNAPSHOT_EXE, mode, os.path.join(out_dir, tag + "-self.png"),
            "--screen", "--keep-size", "--hold=70000"]
    if demo:
        args.append("--demo")

    proc = subprocess.Popen(args, env=env, cwd=REPO, stdout=subprocess.DEVNULL,
                            stderr=subprocess.STDOUT)
    try:
        hwnd = 0
        deadline = time.time() + 30
        while time.time() < deadline and proc.poll() is None:
            hwnd = screenshot.find_window_by_title(TITLE_KEYWORDS[mode])
            if hwnd:
                break
            time.sleep(0.4)
        if not hwnd:
            return {"error": "没找到窗口"}

        time.sleep(2.0)
        rect = screenshot.RECT()
        ctypes.windll.user32.GetWindowRect(hwnd, ctypes.byref(rect))
        box = (rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)
        raise_window(hwnd)
        time.sleep(0.8)

        def snapshot_once(path):
            # 抓之前必须确认窗口还活着：否则等它超时关闭之后再抓，
            # 两帧"稳定"其实都是记事本，结论就反了（这个坑踩过一次）。
            for attempt in range(3):
                if not ctypes.windll.user32.IsWindow(hwnd) or \
                   not ctypes.windll.user32.IsWindowVisible(hwnd):
                    raise RuntimeError("窗口已经关闭，这一帧无效")
                time.sleep(1.0)
                w, h, rgb = screenshot.capture(box[0], box[1], box[2], box[3], path)
                time.sleep(0.4)
                w2, h2, rgb2 = screenshot.capture(box[0], box[1], box[2], box[3], path)
                if same_enough(rgb, rgb2):
                    return w, h, rgb
            return w, h, rgb

        wa, ha, a = snapshot_once(os.path.join(out_dir, tag + "-桌面在后.png"))

        # 垫一层纯白的最大化记事本到窗口下面。
        # 之前直接 raise 抬不动（窗口不是 AlwaysOnTop，记事本一开就压上去），
        # 这里临时设成 TOPMOST，抓完立刻撤掉，不改应用自己的行为。
        note = subprocess.Popen(["notepad.exe"])
        time.sleep(2.5)
        nhwnd = 0
        for _ in range(30):
            nhwnd = screenshot.find_window_by_title("记事本")
            if nhwnd:
                break
            time.sleep(0.3)
        if nhwnd:
            ctypes.windll.user32.ShowWindow(nhwnd, 3)  # SW_MAXIMIZE
            time.sleep(1.2)
        ctypes.windll.user32.SetWindowPos(
            hwnd, -1, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE)  # HWND_TOPMOST
        time.sleep(1.5)

        wb, hb, b = snapshot_once(os.path.join(out_dir, tag + "-白底在后.png"))
        return {"a": mean_lum(a, wa, ha), "b": mean_lum(b, wb, hb),
                "size": "%dx%d" % (wa, ha)}
    finally:
        for proc in (proc,):
            if proc.poll() is None:
                proc.kill()
        for h in [screenshot.find_window_by_title("记事本")]:
            if h:
                ctypes.windll.user32.PostMessageW(h, 0x0010, 0, 0)  # WM_CLOSE


def main(argv):
    if len(argv) < 3:
        print(__doc__)
        return 2
    mode = argv[1]
    out_dir = os.path.abspath(argv[2])
    demo = "--demo" in argv[3:]
    os.makedirs(out_dir, exist_ok=True)
    screenshot._set_dpi_aware()

    glass = run(mode, demo, None, mode, out_dir)
    opaque = run(mode, demo, "none", mode + "-opaque", out_dir)
    for name, r in (("玻璃版", glass), ("不透明版", opaque)):
        if "error" in r:
            print("%s: FAIL %s" % (name, r["error"]))
            return 1
        ma = sum(r["a"]) / len(r["a"])
        mb = sum(r["b"]) / len(r["b"])
        delta = mb - ma
        print("%-8s 桌面在后平均亮度 %.1f → 白底在后 %.1f  变化 %+.1f（Δ=%.1f）"
              % (name, ma, mb, delta, delta))
        print("         分格最大变化 %.1f" % max(abs(x - y) for x, y in zip(r["a"], r["b"])))

    d_glass = sum(glass["b"]) / len(glass["b"]) - sum(glass["a"]) / len(glass["a"])
    d_opaque = sum(opaque["b"]) / len(opaque["b"]) - sum(opaque["a"]) / len(opaque["a"])
    ok = d_glass > 8 and d_glass - d_opaque > 5
    print("\n[%s] 背后换成白底后：玻璃版 %+.1f，不透明版 %+.1f"
          % ("PASS" if ok else "FAIL", d_glass, d_opaque))
    print("       判据：玻璃版 Δ>8，且比不透明版多 5 以上")
    if ok:
        print("       窗口确实在透背景——后面是什么，它就跟着带一点颜色")
    else:
        print("       背后怎么变它都不动 —— 就是不透明背景，材质没真的生效")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
