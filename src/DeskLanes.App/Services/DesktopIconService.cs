using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using DeskLanes.Core.Services;

namespace DeskLanes.App.Services;

/// <summary>
/// 桌面图标隐藏 / 恢复。
/// </summary>
/// <remarks>
/// <b>实现方式（2026-10-25 实机复测后重写，旧方案在本机无效）：</b>
///
/// 旧方案是"写 HKCU\...\Explorer\Advanced\HideIcons + 广播 WM_SETTINGCHANGE"。
/// 阶段 7 授权复测（docs/stage7-fixes.md §测试2、tools/verify-hideicons-final.ps1）证明它在
/// 本机（Win11 26H2 / Build 26300）上<b>根本不生效</b>：注册表写成功了、广播也返回成功，
/// 但桌面图标一直显示——历史 stage5 证据是用"全屏截图 MD5 是否变化"判定的，
/// 被屏幕上无关内容的变化干扰，结论是错的。
///
/// 新方案改用资源管理器<b>自己的</b>机制：向桌面图标视图（SHELLDLL_DefView）发送
/// WM_COMMAND 0x7402——与桌面右键菜单「查看 → 显示桌面图标」是同一条命令。
/// 实测它立即生效，并且会把 HideIcons 注册表值同步成切换后的真实状态
/// （它按"当前视觉状态"取反，不是按注册表取反）。
///
/// 因此：
///   - 注册表值只作为"状态记录"读给用户看，不再由本服务直接写；
///   - 切换前按注册表判断是否需要动作（正常使用中注册表与视觉状态保持同步，
///     因为所有变更都走这条命令）；切换后回读注册表校验命令真的被应用了。
///
/// 安全红线（与项目约定一致）：
///   - 不直接写任何注册表值（HideIcons 由资源管理器自己写）；不动桌面文件；不重启 Explorer；
///   - 默认关闭，且每次启用都要用户明确确认（窗口里走 Confirm 授权）；
///   - 找不到桌面窗口或命令未被应用时如实返回失败，不静默吞掉。
/// </remarks>
public interface IDesktopIconService
{
    /// <summary>当前桌面图标是否处于隐藏状态（按注册表记录的状态；切换命令会同步它）。</summary>
    bool IsHidden { get; }

    /// <summary>读取状态时的说明（成功为 null）。</summary>
    string? StatusText { get; }

    /// <summary>重新读取系统当前状态。用户可能在软件运行期间用 Windows 自己的右键菜单改过。</summary>
    void Refresh();

    /// <summary>隐藏或恢复桌面图标。</summary>
    OperationResult SetHidden(bool hidden);
}

public sealed class DesktopIconService : IDesktopIconService
{
    private const string AdvancedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string ValueName = "HideIcons";

    public bool IsHidden { get; private set; }

    public string? StatusText { get; private set; }

    public DesktopIconService()
    {
        Refresh();
    }

    public void Refresh()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(AdvancedKeyPath, writable: false);
            object? value = key?.GetValue(ValueName);
            // 不假设值的类型：Windows 自己写的就是 DWord，但防御性处理其他类型，避免解析异常
            IsHidden = value switch
            {
                int i => i != 0,
                null => false,
                _ => long.TryParse(value.ToString(), out long parsed) && parsed != 0,
            };
            StatusText = null;
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.Security.SecurityException or UnauthorizedAccessException)
        {
            IsHidden = false;
            StatusText = $"无法读取桌面图标设置：{ex.Message}";
        }
    }

    public OperationResult SetHidden(bool hidden)
    {
        Refresh();
        if (StatusText is not null)
        {
            return OperationResult.Fail($"无法确认桌面图标当前状态，拒绝盲切：{StatusText}");
        }

        if (IsHidden == hidden)
        {
            // 注册表与视觉状态在"所有变更都走切换命令"的前提下保持同步
            StatusText = null;
            return OperationResult.Ok();
        }

        IntPtr defView = NativeMethods.FindDesktopDefView();
        if (defView == IntPtr.Zero)
        {
            StatusText = "找不到桌面图标窗口（SHELLDLL_DefView），本次未做任何修改。" +
                         "可能被第三方桌面工具接管了桌面。";
            return OperationResult.Fail(StatusText);
        }

        NativeMethods.SendToggleDesktopIcons(defView);

        // 切换命令会把 HideIcons 同步成新的视觉状态；轮询确认它真的被应用了
        for (int waitedMs = 0; waitedMs <= 1500; waitedMs += 150)
        {
            Refresh();
            if (StatusText is null && IsHidden == hidden)
            {
                StatusText = null;
                return OperationResult.Ok();
            }
            System.Threading.Thread.Sleep(150);
        }

        Refresh();
        StatusText = "已发送切换命令，但系统没有应用（注册表状态与目标不一致）。" +
                     "桌面图标当前状态以屏幕实际显示为准。";
        return OperationResult.Fail(StatusText);
    }

    private static class NativeMethods
    {
        private const uint WmCommand = 0x0111;
        private const int CmdToggleDesktopIcons = 0x7402;

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder buffer, int maxCount);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// 找承载桌面图标的视图窗口（SHELLDLL_DefView）。
        /// 常规情况下挂在 Progman 下；用了壁纸/桌面工具时会移到某个 WorkerW 下，两种都找。
        /// 用 EnumWindows / EnumChildWindows 而不是 FindWindow——实测 FindWindow 在部分进程里不可靠。
        /// </summary>
        public static IntPtr FindDesktopDefView()
        {
            IntPtr progman = FindTopWindowByClass("Progman");
            if (progman != IntPtr.Zero)
            {
                IntPtr dv = FindChildByClass(progman, "SHELLDLL_DefView");
                if (dv != IntPtr.Zero)
                {
                    return dv;
                }
            }

            // 兜底：遍历所有 WorkerW 顶层窗口，看谁托管着 DefView
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr hWnd, IntPtr _)
            {
                var cls = new StringBuilder(64);
                GetClassName(hWnd, cls, 64);
                if (cls.ToString() != "WorkerW")
                {
                    return true;
                }
                IntPtr dv = FindChildByClass(hWnd, "SHELLDLL_DefView");
                if (dv != IntPtr.Zero)
                {
                    found = dv;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>
        /// 发送「显示桌面图标」切换命令（与桌面右键菜单同一条）。
        /// 命令本身无法可靠判断是否被接受（SendMessage 返回值语义不定），
        /// 真正的成败由调用方的注册表回读确认。
        /// </summary>
        public static void SendToggleDesktopIcons(IntPtr defView)
        {
            SendMessage(defView, WmCommand, new IntPtr(CmdToggleDesktopIcons), IntPtr.Zero);
        }

        private static IntPtr FindTopWindowByClass(string className)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr hWnd, IntPtr _)
            {
                var cls = new StringBuilder(64);
                GetClassName(hWnd, cls, 64);
                if (cls.ToString() == className)
                {
                    found = hWnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static IntPtr FindChildByClass(IntPtr parent, string className)
        {
            // FindWindowEx 对"查找子窗口"这一用途在本机实测可靠，直接用它
            return FindWindowEx(parent, IntPtr.Zero, className, null);
        }
    }
}
