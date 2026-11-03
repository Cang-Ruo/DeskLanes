#!/usr/bin/env python3
"""玻璃材质取证：证明窗口里透出来的确实是桌面，而不是一块灰底。

做法（关键是有参照帧）：
  1. 清场：把挡在桌面上的其它窗口最小化
  2. 以窗口化（不最大化）的方式跑 DeskLanes.UiSnapshot --screen --keep-size
  3. 窗口稳住后，抓一帧**带窗口**的全屏帧 A
  4. 等窗口关闭，再抓一帧**不带窗口**的全屏帧 B —— 这就是"窗口背后到底有什么"
  5. 把 A 里窗口那块和 B 里同一块做量化对比：
     - 结构相关系数 corr：能不能看出背后桌面的明暗轮廓（有图形 ≠ 纯色）
     - 唯一颜色数 / 亮度标准差：颜色有没有层次
     - 高频锐度 hf：透出来的应该是**模糊后**的内容，不是清晰贴上去的

再跑一次对照组：DESKLANES_BACKDROP=none 强制不透明回退。两条一比，
差别是不是真的来自材质，而不是巧合。

用法：
    python tools/glass-proof.py <category|panels|floating|sidebar|settings> <输出目录> [--demo]
"""

import ctypes
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import screenshot  # noqa: E402  复用同一套 GDI BitBlt 屏幕合成截图

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
    # 参照窗口：只有材质、没有任何 UI 内容，用来给"透多少"定标
    "reference": "参照",
}

SW_MINIMIZE = 6
SW_RESTORE = 9

EnumWindowsProc = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)


def minimize_desktop_windows():
    minimized = []

    def cb(hwnd, _):
        try:
            cls = screenshot._class(hwnd)
            title = screenshot._title(hwnd)
        except Exception:
            return True
        if not screenshot.ctypes.windll.user32.IsWindowVisible(hwnd):
            return True
        return True

    # 保持简单：只最小化可见且有标题的窗口，跳过外壳自身
    skip = ("Shell_TrayWn", "Shell_SecondaryTrayWn", "Progman", "WorkerW",
            "DV2ControlHost", "Button")

    def scan(hwnd, _):
        try:
            cls = screenshot._class(hwnd)
            title = screenshot._title(hwnd)
        except Exception:
            return True
        if not title or not cls:
            return True
        if any(s in cls for s in skip):
            return True
        if not ctypes.windll.user32.IsWindowVisible(hwnd):
            return True
        ctypes.windll.user32.ShowWindow(hwnd, SW_MINIMIZE)
        minimized.append(hwnd)
        return True

    ctypes.windll.user32.EnumWindows(EnumWindowsProc(scan), 0)
    return minimized


def restore_windows(handles):
    for hwnd in handles[:]:
        ctypes.windll.user32.ShowWindow(hwnd, SW_RESTORE)


def wait_rect_stable(hwnd, tries=20, gap=0.3):
    last = None
    for _ in range(tries):
        r = screenshot.RECT()
        ctypes.windll.user32.GetWindowRect(hwnd, ctypes.byref(r))
        cur = (r.Left, r.Top, r.Right, r.Bottom)
        if cur == last:
            return cur
        last = cur
        time.sleep(gap)
    return last


def _lum(rgb, i):
    return (rgb[i] * 299 + rgb[i + 1] * 587 + rgb[i + 2] * 114) // 1000


def downsample(rgb, width, height, cells=24):
    grid = []
    for gy in range(cells):
        row = []
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
                    total += _lum(rgb, base + x * 3)
                    count += 1
            row.append(total / max(1, count))
        grid.append(row)
    return grid


def pearson(a, b):
    flat_a = [v for row in a for v in row]
    flat_b = [v for row in b for v in row]
    n = len(flat_a)
    ma = sum(flat_a) / n
    mb = sum(flat_b) / n
    sa = sum((v - ma) ** 2 for v in flat_a)
    sb = sum((v - mb) ** 2 for v in flat_b)
    if sa < 1e-6 or sb < 1e-6:
        return 0.0
    cov = sum((flat_a[i] - ma) * (flat_b[i] - mb) for i in range(n))
    return cov / ((sa ** 0.5) * (sb ** 0.5))


def high_freq(rgb, width, height):
    diffs = 0
    count = 0
    for y in range(0, height - 2, 3):
        base = y * width * 3
        for x in range(0, width - 6, 6):
            diffs += abs(_lum(rgb, base + x * 3) - _lum(rgb, base + x * 3 + 9))
            count += 1
    return diffs / max(1, count)


def stats(rgb, width, height):
    lum = []
    step_y = max(1, height // 150)
    step_x = max(1, width // 150)
    for y in range(0, height, step_y):
        base = y * width * 3
        for x in range(0, width, step_x):
            lum.append(_lum(rgb, base + x * 3))
    mean = sum(lum) / len(lum)
    var = sum((v - mean) ** 2 for v in lum) / len(lum)
    return {"mean": mean, "std": var ** 0.5, "hf": high_freq(rgb, width, height)}


def crop(rgb, width, height, rect):
    left, top, right, bottom = rect
    left = max(0, min(width, left))
    top = max(0, min(height, top))
    right = max(left, min(width, right))
    bottom = max(top, min(height, bottom))
    w = right - left
    h = bottom - top
    if w <= 0 or h <= 0:
        return 0, 0, b""
    out = bytearray(w * h * 3)
    for y in range(h):
        src = ((top + y) * width + left) * 3
        dst = y * w * 3
        out[dst:dst + w * 3] = rgb[src:src + w * 3]
    return w, h, bytes(out)


def run_case(mode, demo, forced_backdrop, tag, out_dir, single_screen=False):
    env = dict(os.environ)
    if forced_backdrop:
        env["DESKLANES_BACKDROP"] = forced_backdrop

    args = [SNAPSHOT_EXE, mode, os.path.join(out_dir, tag + "-self.png"),
            "--screen", "--keep-size", "--hold=9000"]
    if demo:
        args.append("--demo")

    proc = subprocess.Popen(args, env=env, cwd=REPO,
                            stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT)
    try:
        hwnd = 0
        deadline = time.time() + 30
        while time.time() < deadline and proc.poll() is None:
            candidate = screenshot.find_window_by_title(TITLE_KEYWORDS[mode])
            if candidate:
                hwnd = candidate
                break
            time.sleep(0.4)
        if not hwnd:
            return {"error": "没找到窗口（标题关键字 %s）" % TITLE_KEYWORDS[mode]}

        time.sleep(2.0)
        rect = wait_rect_stable(hwnd)

        if single_screen:
            vx = vy = 0
            vw = ctypes.windll.user32.GetSystemMetrics(0)
            vh = ctypes.windll.user32.GetSystemMetrics(1)
        else:
            vx = ctypes.windll.user32.GetSystemMetrics(76)
            vy = ctypes.windll.user32.GetSystemMetrics(77)
            vw = ctypes.windll.user32.GetSystemMetrics(78)
            vh = ctypes.windll.user32.GetSystemMetrics(79)

        fw, fh, with_rgb = screenshot.capture(
            vx, vy, vw, vh, os.path.join(out_dir, tag + "-frame-with.png"))
        try:
            proc.wait(timeout=30)
        except subprocess.TimeoutExpired:
            proc.kill()
        time.sleep(1.2)
        bw, bh, without_rgb = screenshot.capture(
            vx, vy, vw, vh, os.path.join(out_dir, tag + "-frame-without.png"))

        shifted = (rect[0] - vx, rect[1] - vy, rect[2] - vx, rect[3] - vy)
        ww, wh, win_rgb = crop(with_rgb, fw, fh, shifted)
        cw, ch, behind_rgb = crop(without_rgb, bw, bh, shifted)
        if ww == 0 or cw == 0:
            return {"error": "裁剪失败（窗口 %s 落在虚拟屏 %d,%d,%d,%d 之外）"
                    % (rect, vx, vy, vw, vh)}

        screenshot._write_png(os.path.join(out_dir, tag + "-window.png"), ww, wh, win_rgb)
        screenshot._write_png(os.path.join(out_dir, tag + "-behind.png"), cw, ch, behind_rgb)

        res = stats(win_rgb, ww, wh)
        behind = stats(behind_rgb, cw, ch)
        res["corr"] = pearson(downsample(win_rgb, ww, wh), downsample(behind_rgb, cw, ch))
        res["behind_std"] = behind["std"]
        res["behind_hf"] = behind["hf"]
        res["behind_rgb"] = behind_rgb
        # 留下像素数据：空白区归属判定要在三张图之间做（只看单张的指标会被 UI 内容带偏）
        res["win_rgb"] = win_rgb
        res["win_size"] = (ww, wh)
        res["rect"] = "(%d,%d)-(%d,%d) = %dx%d" % (
            rect[0], rect[1], rect[2], rect[3], rect[2] - rect[0], rect[3] - rect[1])
        return res
    finally:
        if proc.poll() is None:
            proc.kill()


def cell_grid(rgb, width, height, cells=32):
    """把图切成 cells×cells，返回每格的 (均值, 内部标准差)。

    标准差用来挑「空玻璃区」：这一格在两次渲染里都不该有 UI 内容，
    它的起伏只可能来自透出来的桌面。
    """
    means = []
    stds = []
    for gy in range(cells):
        y0 = height * gy // cells
        y1 = max(y0 + 1, height * (gy + 1) // cells)
        for gx in range(cells):
            x0 = width * gx // cells
            x1 = max(x0 + 1, width * (gx + 1) // cells)
            vals = []
            step_y = max(1, (y1 - y0) // 8)
            step_x = max(1, (x1 - x0) // 8)
            for y in range(y0, y1, step_y):
                base = y * width * 3
                for x in range(x0, x1, step_x):
                    vals.append(_lum(rgb, base + x * 3))
            mean = sum(vals) / len(vals)
            var = sum((v - mean) ** 2 for v in vals) / len(vals)
            means.append(mean)
            stds.append(var ** 0.5)
    return means, stds


def flat_cell_scores(glass_rgb, opaque_rgb, behind_rgb, width, height,
                     opaque_stds, cells=32, flat_thresh=3.0):
    """只在「空玻璃格」上比较：玻璃版 vs 桌面、不透明版 vs 桌面。

    空玻璃格 = 不透明对照组里很平的格子（说明那一片本来没有 UI 内容）。
    在这一类格子上，如果玻璃版的整体明暗跟着桌面走、而不透明版不跟，
    那就是透光的直接因果证据，不依赖肉眼判断。
    """
    g_means, _ = cell_grid(glass_rgb, width, height, cells)
    o_means, _ = cell_grid(opaque_rgb, width, height, cells)
    b_means, _ = cell_grid(behind_rgb, width, height, cells)

    idx = [i for i, s in enumerate(opaque_stds) if s <= flat_thresh]
    if len(idx) < 8:
        return None, len(idx)

    def corr_of(pairs):
        n = len(pairs)
        ma = sum(p[0] for p in pairs) / n
        mb = sum(p[1] for p in pairs) / n
        sa = sum((p[0] - ma) ** 2 for p in pairs)
        sb = sum((p[1] - mb) ** 2 for p in pairs)
        if sa < 1e-9 or sb < 1e-9:
            return 0.0
        cov = sum((p[0] - ma) * (p[1] - mb) for p in pairs)
        return cov / ((sa ** 0.5) * (sb ** 0.5))

    glass_pairs = [(g_means[i], b_means[i]) for i in idx]
    opaque_pairs = [(o_means[i], b_means[i]) for i in idx]
    g_std = (sum((g_means[i] - sum(g_means[i] for i in idx) / len(idx)) ** 2
                 for i in idx) / len(idx)) ** 0.5
    o_std = (sum((o_means[i] - sum(o_means[i] for i in idx) / len(idx)) ** 2
                 for i in idx) / len(idx)) ** 0.5
    b_std = (sum((b_means[i] - sum(b_means[i] for i in idx) / len(idx)) ** 2
                 for i in idx) / len(idx)) ** 0.5
    return {
        "glass": corr_of(glass_pairs),
        "opaque": corr_of(opaque_pairs),
        "glass_std": g_std,
        "opaque_std": o_std,
        "behind_std": b_std,
    }, len(idx)


def main(argv):
    if len(argv) < 3:
        print(__doc__)
        return 2
    mode = argv[1]
    out_dir = os.path.abspath(argv[2])
    demo = "--demo" in argv[3:]

    screenshot._set_dpi_aware()
    os.makedirs(out_dir, exist_ok=True)

    minimized = minimize_desktop_windows()
    print("minimized %d window(s)" % len(minimized))
    try:
        normal = run_case(mode, demo, None, mode, out_dir)
        if "error" in normal:
            print("FAIL " + normal["error"])
            return 1
        control = run_case(mode, demo, "none", mode + "-opaque", out_dir)
    finally:
        restore_windows(minimized)

    if "error" in control:
        print("WARN 对照组失败：" + control["error"])
        control = None

    print("\n=== %s 玻璃取证 ===" % mode)
    print("窗口矩形: %s" % normal["rect"])
    print("%-22s %-16s %s" % ("指标", "当前窗口", "对照组(不透明)"))

    def line(name, key, fmt):
        ctrl = fmt.format(control[key]) if control else "—"
        print("%-22s %-16s %s" % (name, fmt.format(normal[key]), ctrl))

    line("与背后的结构相关系数", "corr", "{:.3f}")
    line("窗口内亮度标准差", "std", "{:.1f}")
    line("窗口内高频锐度", "hf", "{:.2f}")
    line("背后桌面亮度标准差", "behind_std", "{:.1f}")
    line("背后桌面高频锐度", "behind_hf", "{:.2f}")

    corr = normal["corr"]
    ctrl_corr = control["corr"] if control else 0.0
    print("\n--- 只看空玻璃格（整窗相关系数会被 UI 内容带偏，这一项才是判据）---")
    ok = False
    if control is None:
        print("没有对照组，无法判定")
    else:
        w, h = normal["win_size"]
        _, opaque_stds = cell_grid(control["win_rgb"], w, h)
        scores, n = flat_cell_scores(normal["win_rgb"], control["win_rgb"],
                                     normal["behind_rgb"], w, h, opaque_stds)
        if scores is None:
            print("空玻璃格太少（%d 个），窗口被 UI 占满了，判不了" % n)
        else:
            print("空白格 %d / %d 个；桌面自身的起伏 std=%.1f"
                  % (n, 32 * 32, scores["behind_std"]))
            print("%-26s %-14s %s" % ("指标", "玻璃版", "不透明版"))
            print("%-26s %-14.3f %.3f" % ("相对桌面的相关系数",
                                          scores["glass"], scores["opaque"]))
            print("%-26s %-14.1f %.1f" % ("自身明暗起伏 std",
                                          scores["glass_std"], scores["opaque_std"]))
            ok = scores["glass"] >= 0.5 and scores["glass"] - scores["opaque"] >= 0.3
            print("\n[%s] 空玻璃区相对桌面的相关系数 %.3f（不透明版 %.3f）"
                  % ("PASS" if ok else "FAIL", scores["glass"], scores["opaque"]))
            print("       判据：玻璃版 ≥ 0.5，且比不透明版高出 0.3 以上")
            if ok:
                print("       空白处的明暗确实跟着桌面走 —— 这是透光，不是灰底")
            else:
                print("       空白处基本不随桌面变化 —— 需要继续排查（根容器遮住？材质被拒？）")

    blur_ok = control is None or normal["hf"] < max(control["behind_hf"], 1.0) * 3
    print("[%s] 窗口内高频锐度 %.2f vs 背后桌面 %.2f —— 透出来的是模糊后的内容"
          % ("PASS" if blur_ok else "WARN", normal["hf"], normal["behind_hf"]))
    print("\n窗口证据图: %s" % os.path.join(out_dir, mode + "-window.png"))
    print("背后参照图: %s" % os.path.join(out_dir, mode + "-behind.png"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
