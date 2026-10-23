using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskSpace.Core.Models;

namespace DeskSpace.Core.Services;

/// <summary>
/// 通过 SHGetFileInfo 获取系统图标，转为可绑定的 ImageSource（已冻结，可跨线程赋值）。
/// 图标获取失败时返回 null，不影响项目名称展示。
/// </summary>
public sealed class IconService : IIconService
{
    private readonly Dictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ImageSource? GetIcon(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
        {
            return null;
        }

        if (_cache.TryGetValue(fullPath, out ImageSource? cached))
        {
            return cached;
        }

        ImageSource? icon = TryExtractIcon(fullPath);
        _cache[fullPath] = icon;
        return icon;
    }

    public void ClearCache() => _cache.Clear();

    private static ImageSource? TryExtractIcon(string fullPath)
    {
        var info = new ShellIcons.SHFILEINFO();
        try
        {
            IntPtr result = ShellIcons.SHGetFileInfo(
                fullPath, 0, ref info, (uint)Marshal.SizeOf<ShellIcons.SHFILEINFO>(),
                ShellIcons.SHGFI_ICON | ShellIcons.SHGFI_LARGEICON);

            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(
                    info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                ShellIcons.DestroyIcon(info.hIcon);
            }
        }
        catch
        {
            return null;
        }
    }
}
