// ============================================================================
// DeskLanes 界面快照工具（UI 改版视觉验证用）
// ----------------------------------------------------------------------------
// 用法：DeskLanes.UiSnapshot.exe <category|panels|floating|sidebar|settings|dialog> <输出.png>
//
// 行为：
//   1. 把真实配置目录（%LocalAppData%\DeskLanes）复制到临时目录，绝不写回真实配置；
//   2. 组装与真实应用完全相同的服务与视图模型，启动对应模式的窗口；
//   3. 按模式预置演示数据（分类导航：2 个分类 + 归属；自由分区：2 个分区 + 归属）；
//   4. 渲染完成后截取窗口，保存 PNG 后退出。
// 退出码：0 = 成功；1 = 失败（消息打到 stderr）。
//
// 截图方式分两种：
//   category / panels / floating / sidebar → RenderTargetBitmap（视觉树快照）；
//   settings / dialog → GDI BitBlt 屏幕合成截图（能看到 DWM 的 Mica 毛玻璃）。
//
// 限制：RenderTargetBitmap 抓不到 DWM 合成的 Mica / 亚克力毛玻璃与桌面壁纸；
//       真机玻璃效果请用 settings / dialog 模式或屏幕截图确认。
//
// 注意：本工具只用于开发期视觉验证，不参与应用运行时逻辑。
// ============================================================================

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskLanes.App;
using DeskLanes.App.Services;
using DeskLanes.App.ViewModels;
using DeskLanes.App.Views;
using DeskLanes.Core.Models;
using DeskLanes.Core.Services;

namespace DeskLanes.UiSnapshot;

public static class Program
{
    private static readonly string[] Modes = { "category", "panels", "floating", "sidebar", "settings", "dialog" };

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 2 || !Modes.Contains(args[0]))
        {
            Console.Error.WriteLine("用法：DeskLanes.UiSnapshot.exe <category|panels|floating|sidebar|settings|dialog> <输出.png>");
            return 1;
        }

        string mode = args[0];
        string output = Path.GetFullPath(args[1]);

        // 屏幕合成截图需要物理像素坐标（与 GetWindowRect / GetDC 一致）
        SetProcessDPIAware();

        try
        {
            RunAsync(mode, output).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"快照失败：{ex}");
            return 1;
        }
    }

    private static async Task RunAsync(string mode, string output)
    {
        // 1. 复制真实配置到临时目录（只读使用，不污染真实配置）
        string realDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskLanes");
        string tempDir = Path.Combine(Path.GetTempPath(), "DeskLanesSnap_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        if (Directory.Exists(realDir))
        {
            foreach (string file in Directory.EnumerateFiles(realDir, "*.json"))
            {
                File.Copy(file, Path.Combine(tempDir, Path.GetFileName(file)), overwrite: true);
            }
        }

        try
        {
            // 2. 组装依赖（与 App.xaml.cs 一致）
            IShortcutResolver resolver = new ShortcutResolver();
            IDesktopScanner scanner = new DesktopScanner(resolver);
            IIconService iconService = new IconService();
            ILaunchService launchService = new LaunchService();
            var dialogService = new WpfDialogService();
            IWorkspaceService workspace = new WorkspaceService(
                new LibraryStore(tempDir), new ConfigStore(tempDir), resolver);

            var shellState = new ShellState();
            var mainViewModel = new MainViewModel(
                workspace, scanner, iconService, launchService, dialogService, shellState);

            // 快照工具不启动完整应用：传入未 Start 的控制器（其方法均为空转），
            // 初始化由本工具自行触发，与阶段 2 行为保持一致。
            var controller = new AppController(scanner, iconService, launchService, dialogService, tempDir);

            // 3. 演示数据
            if (mode == "panels")
            {
                workspace.SetLayoutMode(LayoutMode.FreePanels);
                SeedPanels(workspace);
            }
            else
            {
                SeedCategories(workspace);
            }

            // 4. 先建立 Application 并合并轻玻璃主题：
            //    窗口构造（InitializeComponent）时就要解析 StaticResource，
            //    所以这一步必须在创建任何窗口之前完成。
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/DeskLanes;component/Themes/GlassTheme.xaml")
            });

            // 5. 按模式创建窗口（数据来自同一份 workspace，三种模式共用）
            Window window;
            if (mode == "settings")
            {
                // 设置窗口是普通（非分层）窗口：屏幕合成截图可验证真实 Mica 效果。
                // HotkeyService 只在「测试快捷键」时真正用句柄，这里给一个
                // 隐藏宿主窗口的句柄即可（不注册任何热键）。
                var host = new Window
                {
                    Width = 0,
                    Height = 0,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    WindowStyle = WindowStyle.None,
                    Visibility = Visibility.Hidden,
                };
                host.Show();
                var hotkeyService = new HotkeyService(new WindowInteropHelper(host).Handle);
                host.Close();

                var vm = new SettingsViewModel(workspace, hotkeyService, dialogService);
                window = new SettingsWindow(vm)
                {
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    ShowActivated = false,
                };
            }
            else if (mode == "dialog")
            {
                window = new TextInputDialog("重命名分类", "请输入新的分类名称：", "工作")
                {
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    ShowActivated = false,
                };
            }
            else if (mode == "floating")
            {
                var vm = new FloatingWindowViewModel(workspace, dialogService, launchService, shellState);
                window = new FloatingWindow(vm, controller)
                {
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    ShowActivated = false,
                };
            }
            else if (mode == "sidebar")
            {
                var vm = new SidebarViewModel(workspace, dialogService, launchService, shellState);
                window = new SidebarWindow(vm, controller)
                {
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    ShowActivated = false,
                };
                // 快照固定展示展开态（收起态只有 44 DIP 窄条，无法验证视觉规范）；
                // 顺带关掉失焦自动收起，避免快照期间窗口被收起。
                // 这里只写临时配置副本，不影响真实设置
                if (!vm.IsExpanded)
                {
                    vm.IsExpanded = true;
                }

                vm.AutoCollapseOnFocusLost = false;
            }
            else
            {
                window = new MainWindow(mainViewModel, controller)
                {
                    DataContext = mainViewModel,
                    Width = 1400,
                    Height = 900,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    ShowActivated = false,
                };
            }

            dialogService.SetOwner(window);

            // 窗口渲染后：先自行触发首次扫描（快照工具不走完整应用启动流程），
            // 再等待初始化与图标加载完成后截图。
            window.ContentRendered += async (_, _) =>
            {
                try
                {
                    await mainViewModel.InitializeAsync();

                    // 侧边栏快照：确保有选中分类，否则项目区为空
                    if (window.DataContext is SidebarViewModel sidebarVm &&
                        sidebarVm.SelectedCategory == null &&
                        sidebarVm.Categories.Count > 0)
                    {
                        sidebarVm.SelectedCategory = sidebarVm.Categories[0];
                    }

                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    await Task.Delay(300);
                    while ((mainViewModel.IsLoading || mainViewModel.Items.Count == 0) &&
                           stopwatch.ElapsedMilliseconds < 30000)
                    {
                        await Task.Delay(200);
                    }

                    await Task.Delay(800); // 布局与图标稳定

                    // 设置窗口内容需要滚动：取证时滚到底，才能拍到末尾的分区
                    if (mode == "settings")
                    {
                        // 注意：窗口里还有 TextBox 模板自带的 ScrollViewer（PART_ContentHost），
                        // 只取"第一个"会滚错对象，这里按实际高度取内容区那个
                        var scrollers = FindVisualChildren<ScrollViewer>(window);
                        ScrollViewer? content = scrollers
                            .OrderByDescending(s => s.ActualHeight)
                            .FirstOrDefault();
                        content?.ScrollToBottom();

                        // 滚动需要一次渲染才会上屏，屏幕合成截图必须等它画完再拍
                        await Task.Delay(400);
                    }

                    window.UpdateLayout();

                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);

                    if (mode is "settings" or "dialog")
                    {
                        // 普通窗口：屏幕合成截图，能看到 DWM 实际渲染的 Mica 毛玻璃
                        CaptureWindowToPng(new WindowInteropHelper(window).Handle, output);
                        Console.WriteLine($"快照已保存：{output}（屏幕合成，模式 {mode}）");
                    }
                    else
                    {
                        // 分层窗口 / 大窗口：视觉树快照（抓不到 DWM 玻璃，布局可验）
                        int width = (int)window.ActualWidth;
                        int height = (int)window.ActualHeight;
                        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(window);

                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var stream = File.Create(output);
                        encoder.Save(stream);

                        Console.WriteLine($"快照已保存：{output}（{width}x{height}，模式 {mode}）");
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"截图失败：{ex}");
                    Environment.ExitCode = 1;
                }
                finally
                {
                    window.Close();
                    app.Shutdown();
                }
            };

            app.Run(window);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                // 临时目录清理失败不影响结果
            }
        }
    }

    // ============ 屏幕合成截图（GDI BitBlt，零依赖） ============
    // 用于普通窗口：能捕捉 DWM 实际合成的 Mica / 亚克力毛玻璃。

    private const int SRCCOPY = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint biSize;
        public int biWidth;
        public int biHeight; // 负数 = 自顶向下
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr hdcDest, int x, int y, int width, int height,
        IntPtr hdcSrc, int xSrc, int ySrc, int rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        IntPtr hdc, IntPtr hBitmap, uint startScan, uint scanLines,
        byte[] buffer, ref BitmapInfo bitmapInfo, uint usage);

    /// <summary>按窗口矩形做屏幕合成截图并保存 PNG（物理像素）。</summary>
    private static void CaptureWindowToPng(IntPtr hwnd, string path)
    {
        GetWindowRect(hwnd, out RECT rect);
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;

        IntPtr screenDc = GetDC(IntPtr.Zero);
        try
        {
            IntPtr memDc = CreateCompatibleDC(screenDc);
            try
            {
                IntPtr bitmap = CreateCompatibleBitmap(screenDc, width, height);
                IntPtr old = SelectObject(memDc, bitmap);
                try
                {
                    BitBlt(memDc, 0, 0, width, height, screenDc, rect.Left, rect.Top, SRCCOPY);
                }
                finally
                {
                    // GetDIBits 要求位图不被选入 DC，先恢复旧对象
                    SelectObject(memDc, old);
                }

                var info = new BitmapInfo
                {
                    biSize = (uint)Marshal.SizeOf<BitmapInfo>(),
                    biWidth = width,
                    biHeight = -height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0,
                };
                var pixels = new byte[width * 4 * height];
                GetDIBits(memDc, bitmap, 0, (uint)height, pixels, ref info, 0);
                DeleteObject(bitmap);

                var source = BitmapSource.Create(
                    width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));
                using var stream = File.Create(path);
                encoder.Save(stream);
            }
            finally
            {
                DeleteDC(memDc);
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>收集视觉树里所有指定类型的子元素（取证用，不影响应用行为）。</summary>
    private static List<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        var found = new List<T>();
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
            {
                found.Add(typed);
            }

            found.AddRange(FindVisualChildren<T>(child));
        }

        return found;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        => FindVisualChildren<T>(parent).FirstOrDefault();

    private static void SeedCategories(IWorkspaceService workspace)
    {
        ShortcutGroup work = workspace.CreateGroup("工作");
        ShortcutGroup tools = workspace.CreateGroup("常用工具");

        int assigned = 0;
        foreach (DesktopItem item in workspace.Items)
        {
            if (assigned < 8)
            {
                workspace.AssignItemToGroup(item, work);
            }
            else if (assigned < 14)
            {
                workspace.AssignItemToGroup(item, tools);
            }
            else
            {
                break;
            }

            assigned++;
        }
    }

    private static void SeedPanels(IWorkspaceService workspace)
    {
        WorkspacePanel left = workspace.CreatePanel("左屏 · 常用");
        left.X = 60;
        left.Y = 60;
        left.Width = 420;
        left.Height = 300;

        WorkspacePanel right = workspace.CreatePanel("右屏 · 文档");
        right.X = 800;
        right.Y = 60;
        right.Width = 460;
        right.Height = 340;

        int assigned = 0;
        foreach (DesktopItem item in workspace.Items)
        {
            if (assigned < 6)
            {
                workspace.AssignItemToPanel(item, left);
            }
            else if (assigned < 12)
            {
                workspace.AssignItemToPanel(item, right);
            }
            else
            {
                break;
            }

            assigned++;
        }
    }
}
