"""真实屏幕合成截图（GDI BitBlt，零第三方依赖），输出 PNG。

用途：UI 取证。RenderTargetBitmap 只能抓 WPF 视觉树，
**抓不到 DWM 合成的 Mica / 亚克力毛玻璃，也抓不到桌面壁纸**；
要判断窗口背景材质到底有没有生效，必须用本脚本做屏幕合成截图。

用法：
    python tools/screenshot.py <输出.png> [--window <窗口标题关键字>]

不带 --window 时截取整个虚拟屏幕（含所有显示器）。
带 --window 时先找到标题含关键字的可见窗口，只截它的矩形区域。
"""

import ctypes
import os
import struct
import sys
import time
import zlib

SRCCOPY = 0x00CC0020
DIB_RGB_COLORS = 0
BI_RGB = 0

SM_XVIRTUALSCREEN = 76
SM_YVIRTUALSCREEN = 77
SM_CXVIRTUALSCREEN = 78
SM_CYVIRTUALSCREEN = 79


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", ctypes.c_uint32),
        ("biWidth", ctypes.c_int32),
        ("biHeight", ctypes.c_int32),
        ("biPlanes", ctypes.c_uint16),
        ("biBitCount", ctypes.c_uint16),
        ("biCompression", ctypes.c_uint32),
        ("biSizeImage", ctypes.c_uint32),
        ("biXPelsPerMeter", ctypes.c_int32),
        ("biYPelsPerMeter", ctypes.c_int32),
        ("biClrUsed", ctypes.c_uint32),
        ("biClrImportant", ctypes.c_uint32),
    ]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER),
                ("bmiColors", ctypes.c_uint32 * 3)]


class RECT(ctypes.Structure):
    _fields_ = [("Left", ctypes.c_int32), ("Top", ctypes.c_int32),
                ("Right", ctypes.c_int32), ("Bottom", ctypes.c_int32)]


def _set_dpi_aware():
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except Exception:
        try:
            ctypes.windll.user32.SetProcessDPIAware()
        except Exception:
            pass


# ---- 窗口枚举辅助 ----------------------------------------------------------
# 这几个函数被 glass-proof.py / glass-causal.py / shoot-window.py /
# phase1-capture.py 的「清场」逻辑调用。此前它们根本不存在，调用方又都用
# `except Exception: pass` 包着，于是「最小化其它窗口」从来没生效过 ——
# 拍出来的 Acrylic 背后挡着别的窗口，明暗壁纸读数会一模一样。
# 句柄一律显式转成 c_void_p：64 位下让 ctypes 默认按 c_int 传会截断句柄。
def _class(hwnd):
    buf = ctypes.create_unicode_buffer(256)
    ctypes.windll.user32.GetClassNameW(ctypes.c_void_p(hwnd), buf, 256)
    return buf.value


def _title(hwnd):
    buf = ctypes.create_unicode_buffer(512)
    ctypes.windll.user32.GetWindowTextW(ctypes.c_void_p(hwnd), buf, 512)
    return buf.value


def _write_png(path, width, height, rgb):
    """rgb 为自顶向下的 RGB 三通道字节串。"""
    raw = bytearray()
    stride = width * 3
    for y in range(height):
        raw.append(0)  # filter type 0
        raw += rgb[y * stride:(y + 1) * stride]

    def chunk(typ, data):
        out = struct.pack(">I", len(data)) + typ + data
        return out + struct.pack(">I", zlib.crc32(typ + data) & 0xFFFFFFFF)

    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(bytes(raw), 6))
    png += chunk(b"IEND", b"")
    with open(path, "wb") as f:
        f.write(png)


def read_png(path):
    """读回 _write_png 写的 RGB8 PNG，返回 (width, height, rgb)。

    取证时经常要「改前 / 改后对同一块区域算亮度」，总不能每次重新截屏。
    本模块写出的 PNG 都是 filter type 0、非隔行，所以这里只需反转 zlib。
    其它来源的 PNG（filter 1~4 / 调色板 / RGBA）不支持，直接抛错，避免算错证据。
    """
    data = open(path, "rb").read()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("不是 PNG：%s" % path)
    pos, idat, width, height, depth, color, interlace = 8, bytearray(), None, None, None, None, None
    while pos + 8 <= len(data):
        ln = struct.unpack(">I", data[pos:pos + 4])[0]
        typ = data[pos + 4:pos + 8]
        payload = data[pos + 8:pos + 8 + ln]
        if typ == b"IHDR":
            width, height, depth, color, _, _, interlace = struct.unpack(">IIBBBBB", payload)
        elif typ == b"IDAT":
            idat += payload
        elif typ == b"IEND":
            break
        pos += 12 + ln
    if (depth, color, interlace) != (8, 2, 0):
        raise ValueError("只支持 RGB8 非隔行 PNG：%s" % path)
    raw = zlib.decompress(bytes(idat))
    stride, out, pos = width * 3, bytearray(), 0
    for _ in range(height):
        if raw[pos] != 0:
            raise ValueError("只支持 filter 0 的 PNG：%s" % path)
        pos += 1
        out += raw[pos:pos + stride]
        pos += stride
    return width, height, bytes(out)


EnumWindowsProc = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)


def find_window_by_title(keyword):
    """按标题关键字找可见的非最小化顶层窗口，返回句柄或 0。"""
    user32 = ctypes.windll.user32
    result = []

    @EnumWindowsProc
    def cb(hwnd, _):
        if not user32.IsWindowVisible(hwnd):
            return True
        if user32.IsIconic(hwnd):
            return True
        length = user32.GetWindowTextLengthW(hwnd)
        if length <= 0:
            return True
        buf = ctypes.create_unicode_buffer(length + 1)
        user32.GetWindowTextW(hwnd, buf, length + 1)
        if keyword.lower() in buf.value.lower():
            result.append(hwnd)
        return True

    user32.EnumWindows(cb, 0)
    return result[0] if result else 0


def capture(x, y, width, height, out_path):
    _set_dpi_aware()
    user32 = ctypes.windll.user32
    gdi32 = ctypes.windll.gdi32

    hdc = user32.GetDC(0)
    if not hdc:
        raise RuntimeError("GetDC failed")
    memdc = None
    hbmp = None
    try:
        memdc = gdi32.CreateCompatibleDC(hdc)
        hbmp = gdi32.CreateCompatibleBitmap(hdc, width, height)
        old = gdi32.SelectObject(memdc, hbmp)
        if not gdi32.BitBlt(memdc, 0, 0, width, height, hdc, x, y, SRCCOPY):
            raise RuntimeError("BitBlt failed")

        # 用 32 bpp：每行天然 4 字节对齐，省掉 24 bpp 的行补齐坑。
        bmi = BITMAPINFO()
        bmi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
        bmi.bmiHeader.biWidth = width
        bmi.bmiHeader.biHeight = -height  # 负 = 自顶向下
        bmi.bmiHeader.biPlanes = 1
        bmi.bmiHeader.biBitCount = 32
        bmi.bmiHeader.biCompression = BI_RGB
        bmi.bmiHeader.biSizeImage = width * height * 4

        buf = ctypes.create_string_buffer(width * height * 4)
        got = gdi32.GetDIBits(
            memdc, hbmp, 0, height, buf, ctypes.byref(bmi), DIB_RGB_COLORS)
        if got != height:
            raise RuntimeError("GetDIBits returned %d (w=%d h=%d)" % (got, width, height))

        # 内存里是 BGRA（步长 4），PNG 要 RGB（步长 3）-> 切片重排
        data = buf.raw
        rgb = bytearray(width * height * 3)
        rgb[0::3] = data[2::4]
        rgb[1::3] = data[1::4]
        rgb[2::3] = data[0::4]
        _write_png(out_path, width, height, bytes(rgb))
        return width, height, bytes(rgb)
        return True
    finally:
        if memdc and hbmp:
            gdi32.SelectObject(memdc, ctypes.c_void_p(0))
        if hbmp:
            gdi32.DeleteObject(hbmp)
        if memdc:
            gdi32.DeleteDC(memdc)
        if hdc:
            user32.ReleaseDC(0, hdc)


def main(argv):
    if len(argv) < 2:
        sys.stdout.write(__doc__)
        return 2
    out = os.path.abspath(argv[1])

    if "--window" in argv:
        idx = argv.index("--window")
        keyword = argv[idx + 1] if idx + 1 < len(argv) else ""
        hwnd = find_window_by_title(keyword)
        if not hwnd:
            sys.stdout.write("window not found: %s\n" % keyword)
            return 1
        rect = RECT()
        ctypes.windll.user32.GetWindowRect(hwnd, ctypes.byref(rect))
        x, y = rect.Left, rect.Top
        w = rect.Right - rect.Left
        h = rect.Bottom - rect.Top
    else:
        user32 = ctypes.windll.user32
        x = user32.GetSystemMetrics(SM_XVIRTUALSCREEN)
        y = user32.GetSystemMetrics(SM_YVIRTUALSCREEN)
        w = user32.GetSystemMetrics(SM_CXVIRTUALSCREEN)
        h = user32.GetSystemMetrics(SM_CYVIRTUALSCREEN)

    sys.stdout.write("capturing %dx%d at (%d,%d)\n" % (w, h, x, y))
    ok = capture(x, y, w, h, out)
    if ok:
        sys.stdout.write("saved: %s (%d bytes)\n" % (out, os.path.getsize(out)))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
