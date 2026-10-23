using System.Runtime.InteropServices;
using System.Text;

namespace DeskSpace.Core;

/// <summary>
/// 通过 Windows Known Folder API 获取桌面路径：不硬编码，自动处理文件夹重定向。
/// </summary>
public static class KnownFolders
{
    public static readonly Guid FOLDERID_Desktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");

    // FOLDERID_PublicDesktop（注册表登记名 "Common Desktop"；经本机 FolderDescriptions 核实）
    public static readonly Guid FOLDERID_PublicDesktop = new("C4AA340D-F20F-4863-AFEF-F87EF2E6BA25");

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetFolderPath(IntPtr hwnd, int csidl, IntPtr hToken, uint dwFlags, StringBuilder pszPath);

    /// <summary>获取指定 Known Folder 的物理路径；失败返回 null。</summary>
    public static string? TryGetPath(Guid folderId)
    {
        int hr = SHGetKnownFolderPath(folderId, 0, IntPtr.Zero, out IntPtr pointer);
        if (hr != 0 || pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    /// <summary>公共桌面路径（FOLDERID_PublicDesktop，失败时用 CSIDL_COMMON_DESKTOPDIRECTORY 兜底）。</summary>
    public static string? TryGetPublicDesktopPath()
        => TryGetPath(FOLDERID_PublicDesktop) ?? TryGetCsidlPath(0x0019);

    private static string? TryGetCsidlPath(int csidl)
    {
        var builder = new StringBuilder(260);
        int hr = SHGetFolderPath(IntPtr.Zero, csidl, IntPtr.Zero, 0, builder);
        return hr == 0 ? builder.ToString() : null;
    }
}
