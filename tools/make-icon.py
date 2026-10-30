"""生成 DeskSpace 应用图标（纯标准库，不引入第三方依赖）。

设计：圆角方形底板（蓝紫渐变）+ 2x2 白色面板网格，右下格用琥珀色高亮，
对应"桌面工作空间 / 自由分区"的语义。输出多尺寸 PNG 压缩的 .ico。
"""
import math
import os
import struct
import zlib

SIZES = [256, 128, 64, 48, 32, 16]

BG_TOP = (91, 140, 255)
BG_BOTTOM = (68, 74, 214)
PANEL = (255, 255, 255)
ACCENT = (255, 201, 77)


def smoothstep(e0, e1, x):
    t = (x - e0) / (e1 - e0)
    t = 0.0 if t < 0.0 else (1.0 if t > 1.0 else t)
    return t * t * (3 - 2 * t)


def rounded_rect_sdf(x, y, left, top, right, bottom, r):
    """标准 sdRoundBox：到圆角矩形的有符号距离（负=内部）。"""
    hw = (right - left) / 2.0
    hh = (bottom - top) / 2.0
    qx = abs(x - (left + hw)) - (hw - r)
    qy = abs(y - (top + hh)) - (hh - r)
    ax = max(qx, 0.0)
    ay = max(qy, 0.0)
    return math.hypot(ax, ay) + min(max(qx, qy), 0.0) - r


class Canvas:
    def __init__(self, n):
        self.n = n
        self.px = [[0.0, 0.0, 0.0, 0.0] for _ in range(n * n)]

    def blend(self, i, color, alpha):
        if alpha <= 0.0:
            return
        dst = self.px[i]
        a = alpha
        inv = 1.0 - a
        na = a + dst[3] * inv
        if na <= 0.0:
            return
        for c in range(3):
            dst[c] = (color[c] * a + dst[c] * dst[3] * inv) / na
        dst[3] = na

    def to_rgba_rows(self):
        out = bytearray()
        for y in range(self.n):
            out.append(0)  # filter type 0
            for x in range(self.n):
                r, g, b, a = self.px[y * self.n + x]
                out.append(int(round(min(1.0, max(0.0, r)) * 255)))
                out.append(int(round(min(1.0, max(0.0, g)) * 255)))
                out.append(int(round(min(1.0, max(0.0, b)) * 255)))
                out.append(int(round(min(1.0, max(0.0, a)) * 255)))
        return bytes(out)


def over(dst, col, a):
    """标准 over 合成，dst / 返回值均为非预乘 RGBA（0..1）。"""
    if a >= 1.0:
        return (col[0], col[1], col[2], 1.0)
    out_a = a + dst[3] * (1.0 - a)
    if out_a <= 0.0:
        return (0.0, 0.0, 0.0, 0.0)
    k = dst[3] * (1.0 - a)
    return (
        (col[0] * a + dst[0] * k) / out_a,
        (col[1] * a + dst[1] * k) / out_a,
        (col[2] * a + dst[2] * k) / out_a,
        out_a,
    )


def render(size, ss=3):
    """渲染一个尺寸，ss = 每轴超采样数（抗锯齿）。

    每个采样点独立合成出最终颜色，再对采样点取平均——
    不能对"预乘合成"的中间结果取平均，否则颜色会被压暗。
    """
    c = Canvas(size)
    n = size
    radius = n * 0.22
    margin = n * 0.215
    gap = max(1.0, n * 0.085)
    cell_r = max(1.0, n * 0.075)
    cell = (n - 2 * margin - gap) / 2.0

    bg_top = tuple(v / 255.0 for v in BG_TOP)
    bg_bot = tuple(v / 255.0 for v in BG_BOTTOM)
    panel = tuple(v / 255.0 for v in PANEL)
    accent = tuple(v / 255.0 for v in ACCENT)

    for y in range(n):
        for x in range(n):
            sr = sg = sb = sa = 0.0
            for sy in range(ss):
                for sx in range(ss):
                    px = x + (sx + 0.5) / ss
                    py = y + (sy + 0.5) / ss

                    # 底板（垂直渐变）
                    d = rounded_rect_sdf(px, py, 0.0, 0.0, float(n), float(n), radius)
                    a_bg = 1.0 - smoothstep(-0.8, 0.8, d)
                    t = py / float(n)
                    bg = tuple(bg_top[k] + (bg_bot[k] - bg_top[k]) * t for k in range(3))

                    dst = (0.0, 0.0, 0.0, 0.0)
                    if a_bg > 0.0:
                        dst = over(dst, bg, a_bg)

                    # 2x2 面板
                    for row in range(2):
                        for col in range(2):
                            left = margin + col * (cell + gap)
                            top = margin + row * (cell + gap)
                            d2 = rounded_rect_sdf(px, py, left, top,
                                                  left + cell, top + cell, cell_r)
                            a_p = 1.0 - smoothstep(-0.8, 0.8, d2)
                            if a_p <= 0.0:
                                continue
                            is_accent = (row == 1 and col == 1)
                            rgb = accent if is_accent else panel
                            alpha = a_p * (1.0 if is_accent else 0.92)
                            dst = over(dst, rgb, alpha)

                    sr += dst[0]; sg += dst[1]; sb += dst[2]; sa += dst[3]

            div = float(ss * ss)
            c.px[y * n + x] = [sr / div, sg / div, sb / div, sa / div]
    return c


def png_bytes(canvas):
    raw = canvas.to_rgba_rows()
    n = canvas.n

    def chunk(tag, data):
        return (struct.pack('>I', len(data)) + tag + data
                + struct.pack('>I', zlib.crc32(tag + data) & 0xFFFFFFFF))

    ihdr = struct.pack('>IIBBBBB', n, n, 8, 6, 0, 0, 0)
    return (b'\x89PNG\r\n\x1a\n'
            + chunk(b'IHDR', ihdr)
            + chunk(b'IDAT', zlib.compress(raw, 9))
            + chunk(b'IEND', b''))


def build_ico(path):
    images = []
    for s in SIZES:
        # 小尺寸用更强的超采样，保证边缘干净
        ss = 4 if s <= 32 else 3
        images.append((s, png_bytes(render(s, ss))))

    out = bytearray()
    out += struct.pack('<HHH', 0, 1, len(images))
    offset = 6 + 16 * len(images)
    for s, data in images:
        b = 0 if s >= 256 else s
        out += struct.pack('<BBBBHHII', b, b, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for _, data in images:
        out += data

    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'wb') as f:
        f.write(bytes(out))
    print('written', path, os.path.getsize(path), 'bytes; sizes =', SIZES)


if __name__ == '__main__':
    # 相对本脚本推导仓库根，不写死某一台机器的用户名
    _repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    build_ico(os.path.join(_repo, 'src', 'DeskLanes.App', 'Assets', 'app.ico'))
