using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace DeskLanes.App.Services;

/// <summary>
/// 启动时（任何 DeskLanes 窗口显示之前）抓一帧纯桌面，压成"局部亮度网格"。
///
/// 为什么不抓自己的窗口：Acrylic 会把窗口背后的东西模糊后透出来，但窗口上已经
/// 画好的文字/图标会污染采样；而在窗口 Show 之前抓，得到的就是"文字将要压在什么
/// 亮度上"的干净来源。这与 tools/glass-proof.py 用"带窗口帧 / 不带窗口帧"做因果
/// 对照是同一个道理。
///
/// 网格用**中位数**而不是均值：即使某一格里恰好有个图标或桌面文字，少数亮/暗
/// 像素也不会把整格的亮度带偏。再做一次 3×3 均值，模拟 Acrylic 的大半径模糊。
/// </summary>
internal sealed class DesktopLuminance
{
    /// <summary>每格边长（物理像素）。32px 约为一个项目名块的尺度。</summary>
    public const int CellPx = 32;

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const uint SrcCopy = 0x00CC0020;
    private const uint DibRgbColors = 0;
    private const uint BiRgb = 0;

    private double[,]? _grid;
    private int _left;
    private int _top;
    private int _cols;
    private int _rows;

    public bool IsAvailable => _grid is not null;
    public int CellCount => _cols * _rows;

    /// <summary>当前桌面壁纸文件路径（从 HKCU 读，SPI_SETDESKWALLPAPER 会更新它）。</summary>
    public static string? CurrentWallpaperPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            string? path = key?.GetValue("WallPaper") as string;
            return !string.IsNullOrEmpty(path) && File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int width, int height,
                                     IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr bitmap, uint startScan, uint scanLines,
                                        byte[] bits, ref BitmapInfoHeader info, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    /// <summary>抓帧成功返回 true；任何失败都返回 false，调用方回退到静态文字色。</summary>
    public bool Capture()
    {
        int vx = GetSystemMetrics(SmXVirtualScreen);
        int vy = GetSystemMetrics(SmYVirtualScreen);
        int vw = GetSystemMetrics(SmCxVirtualScreen);
        int vh = GetSystemMetrics(SmCyVirtualScreen);
        if (vw <= 0 || vh <= 0 || vw > 16384 || vh > 16384)
        {
            return false;
        }

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr bitmap = CreateCompatibleBitmap(screenDc, vw, vh);
        byte[]? pixels = null;
        try
        {
            if (memDc == IntPtr.Zero || bitmap == IntPtr.Zero || screenDc == IntPtr.Zero)
            {
                return false;
            }

            IntPtr oldBitmap = SelectObject(memDc, bitmap);
            bool copied = BitBlt(memDc, 0, 0, vw, vh, screenDc, vx, vy, SrcCopy);

            // 32bpp + 负高度 = 自顶向下的 BGRA，避免每行 4 字节对齐的坑
            var info = new BitmapInfoHeader
            {
                biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                biWidth = vw,
                biHeight = -vh,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BiRgb,
            };
            pixels = new byte[checked(vw * vh * 4)];
            int lines = copied ? GetDIBits(memDc, bitmap, 0, (uint)vh, pixels, ref info, DibRgbColors) : 0;
            SelectObject(memDc, oldBitmap);
            if (lines != vh)
            {
                return false;
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            if (bitmap != IntPtr.Zero)
            {
                DeleteObject(bitmap);
            }
            if (memDc != IntPtr.Zero)
            {
                DeleteDC(memDc);
            }
            if (screenDc != IntPtr.Zero)
            {
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        _left = vx;
        _top = vy;
        _cols = Math.Max(1, (vw + CellPx - 1) / CellPx);
        _rows = Math.Max(1, (vh + CellPx - 1) / CellPx);
        double[,] raw = new double[_cols, _rows];
        var samples = new List<byte>(64);

        for (int gy = 0; gy < _rows; gy++)
        {
            for (int gx = 0; gx < _cols; gx++)
            {
                int x0 = gx * CellPx;
                int y0 = gy * CellPx;
                int stepX = Math.Max(1, Math.Min(CellPx, vw - x0) / 8);
                int stepY = Math.Max(1, Math.Min(CellPx, vh - y0) / 8);
                samples.Clear();
                for (int y = y0; y < Math.Min(y0 + CellPx, vh); y += stepY)
                {
                    int row = y * vw * 4;
                    for (int x = x0; x < Math.Min(x0 + CellPx, vw); x += stepX)
                    {
                        int i = row + x * 4; // BGRA
                        samples.Add((byte)((pixels[i + 2] * 299 + pixels[i + 1] * 587 + pixels[i] * 114) / 1000));
                    }
                }
                if (samples.Count == 0)
                {
                    raw[gx, gy] = 128;
                    continue;
                }
                samples.Sort();
                raw[gx, gy] = samples[samples.Count / 2];
            }
        }

        // 3×3 均值：Acrylic 模糊后局部起伏会变缓，别让相邻两格频繁选不同颜色
        var blurred = new double[_cols, _rows];
        for (int gy = 0; gy < _rows; gy++)
        {
            for (int gx = 0; gx < _cols; gx++)
            {
                double sum = 0;
                int n = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = gx + dx;
                        int ny = gy + dy;
                        if (nx >= 0 && nx < _cols && ny >= 0 && ny < _rows)
                        {
                            sum += raw[nx, ny];
                            n++;
                        }
                    }
                }
                blurred[gx, gy] = sum / Math.Max(1, n);
            }
        }

        _grid = blurred;
        return true;
    }

    /// <summary>
    /// 从当前壁纸位图重建亮度网格，**不需要屏幕上没有窗口**。
    ///
    /// 启动时抓屏最准，但运行中换壁纸时窗口是可见的，再抓屏就会把自己的 UI
    /// 一起采进去。壁纸文件本身是干净的来源：按每台显示器的 Fill（等比裁剪居中）
    /// 映射，再把每个网格中心落到的壁纸像素亮度填进去，最后做同样的 3×3 模糊。
    /// 分辨率 / 显示器拓扑变化也走这条路。
    /// </summary>
    public bool SetFromWallpaper(string? path = null)
    {
        path ??= CurrentWallpaperPath();
        if (path is null)
        {
            return false;
        }

        byte[] pixels;
        int imageWidth, imageHeight;
        try
        {
            var decoder = BitmapDecoder.Create(
                new Uri(path, UriKind.Absolute),
                BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            BitmapFrame frame = decoder.Frames[0];
            var bgr = new FormatConvertedBitmap(frame, PixelFormats.Bgr32, null, 0);
            imageWidth = bgr.PixelWidth;
            imageHeight = bgr.PixelHeight;
            if (imageWidth <= 0 || imageHeight <= 0)
            {
                return false;
            }
            pixels = new byte[checked(imageWidth * imageHeight * 4)];
            bgr.CopyPixels(pixels, imageWidth * 4, 0);
        }
        catch
        {
            return false;
        }

        var monitors = new List<MonitorRect>();
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (_, _, rect, _) =>
            {
                if (rect != IntPtr.Zero)
                {
                    monitors.Add(Marshal.PtrToStructure<MonitorRect>(rect));
                }
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            return false;
        }
        if (monitors.Count == 0)
        {
            return false;
        }

        _left = GetSystemMetrics(SmXVirtualScreen);
        _top = GetSystemMetrics(SmYVirtualScreen);
        int vw = GetSystemMetrics(SmCxVirtualScreen);
        int vh = GetSystemMetrics(SmCyVirtualScreen);
        _cols = Math.Max(1, (vw + CellPx - 1) / CellPx);
        _rows = Math.Max(1, (vh + CellPx - 1) / CellPx);

        double[,] raw = new double[_cols, _rows];
        for (int gy = 0; gy < _rows; gy++)
        {
            for (int gx = 0; gx < _cols; gx++)
            {
                double sx = _left + gx * CellPx + CellPx / 2.0;
                double sy = _top + gy * CellPx + CellPx / 2.0;
                raw[gx, gy] = SampleWallpaper(monitors, pixels, imageWidth, imageHeight, sx, sy);
            }
        }

        _grid = Blur(raw);
        return true;
    }

    private static double SampleWallpaper(List<MonitorRect> monitors, byte[] pixels,
                                          int imageWidth, int imageHeight, double sx, double sy)
    {
        foreach (var m in monitors)
        {
            double mw = m.Right - m.Left;
            double mh = m.Bottom - m.Top;
            if (sx < m.Left || sx >= m.Right || sy < m.Top || sy >= m.Bottom)
            {
                continue;
            }

            // Fill：等比放大到铺满整块屏，超出部分居中裁掉
            double scale = Math.Max(mw / imageWidth, mh / imageHeight);
            double sw = imageWidth * scale;
            double sh = imageHeight * scale;
            double px = (sx - m.Left - (mw - sw) / 2) / scale;
            double py = (sy - m.Top - (mh - sh) / 2) / scale;
            int ix = Math.Clamp((int)px, 0, imageWidth - 1);
            int iy = Math.Clamp((int)py, 0, imageHeight - 1);
            int i = (iy * imageWidth + ix) * 4; // Bgr32
            return (pixels[i + 2] * 299 + pixels[i + 1] * 587 + pixels[i] * 114) / 1000.0;
        }
        return 0; // 虚拟屏里没有显示器的空隙，与抓屏帧一致
    }

    private double[,] Blur(double[,] raw)
    {
        var blurred = new double[_cols, _rows];
        for (int gy = 0; gy < _rows; gy++)
        {
            for (int gx = 0; gx < _cols; gx++)
            {
                double sum = 0;
                int n = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = gx + dx;
                        int ny = gy + dy;
                        if (nx >= 0 && nx < _cols && ny >= 0 && ny < _rows)
                        {
                            sum += raw[nx, ny];
                            n++;
                        }
                    }
                }
                blurred[gx, gy] = sum / Math.Max(1, n);
            }
        }
        return blurred;
    }

    /// <summary>返回该屏幕物理坐标处的桌面亮度（0~255）；无数据或越界返回 -1。</summary>
    /// <remarks>
    /// 越界不能钳到边缘格：ScrollViewer 里滚出可视区的控件 PointToScreen 会给到
    /// y=2554 这种屏幕外坐标，钳到最后一格（通常是虚拟屏里的黑色空隙）会让亮度
    /// 变成 0，进而把不可见的文字也算进决策。
    /// </remarks>
    public double LumaAt(double screenX, double screenY)
    {
        if (_grid is null)
        {
            return -1;
        }
        int gx = (int)Math.Floor((screenX - _left) / CellPx);
        int gy = (int)Math.Floor((screenY - _top) / CellPx);
        if (gx < 0 || gx >= _cols || gy < 0 || gy >= _rows)
        {
            return -1;
        }
        return _grid[gx, gy];
    }
}
