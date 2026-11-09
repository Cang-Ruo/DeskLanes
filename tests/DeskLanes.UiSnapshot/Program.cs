// ============================================================================
// DeskLanes 界面快照工具（UI 改版视觉验证用）
// ----------------------------------------------------------------------------
// 用法：DeskLanes.UiSnapshot.exe <category|panels|floating|sidebar|settings|dialog|perf|reference|perf200|mem|memtoggle> <输出> [--demo] [--top] [--screen]
//   mem / memtoggle 不出图：输出内存分解 / 连续切换曲线 JSON（阶段 7）。
//   perf 模式不出图：实测「分类导航 ↔ 自由分区」来回切换的耗时（B6 最后一项）。
//   配 --demo 会用 220 个演示项目跑压力版，不加则用真实桌面。
//   配 --screen 强制所有模式走 GDI 屏幕合成截图（默认只有 settings / dialog 走）：
//          视觉树快照抓不到 DWM 材质与壁纸，展示玻璃效果必须加它；
//          主窗口模式下会顺便最大化，避免把真实桌面图标拍进对外截图。
//
// 行为：
//   1. 把真实配置目录（%LocalAppData%\DeskLanes）复制到临时目录，绝不写回真实配置；
//      （加 --demo 则不复制，改用内置的演示项目，截图里不会出现真实桌面文件）
//   2. 组装与真实应用完全相同的服务与视图模型，启动对应模式的窗口；
//   3. 按模式预置演示数据（分类导航：2 个分类 + 归属；自由分区：2 个分区 + 归属）；
//   4. 渲染完成后截取窗口，保存 PNG 后退出。
// 退出码：0 = 成功；1 = 失败（消息打到 stderr）。
//
// 截图方式分两种：
//   category / panels / floating / sidebar → RenderTargetBitmap（视觉树快照）；
//   settings / dialog → GDI BitBlt 屏幕合成截图（能看到 DWM 的 Mica 毛玻璃）。
//   perf     → 不开截图，改为实测「分类导航 ↔ 自由分区」来回切换的耗时（B6 最后一项）。
//              配 --demo 会用 220 个演示项目做压力版，不加则用真实桌面。
//   settings 默认滚到底（为了拍到末尾的分区）；加 --top 则保持顶部，用于展示上半部分。
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
using System.Threading;
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
    private static readonly string[] Modes =
        { "category", "panels", "floating", "floatingfolders", "sidebar", "settings", "dialog",
          "perf", "reference", "perf200", "mem", "memtoggle", "toggle" };

    /// <summary>进程启动时刻：用来量"从双击到首屏可用"的端到端时间。</summary>
    private static readonly System.Diagnostics.Stopwatch ProgramClock =
        System.Diagnostics.Stopwatch.StartNew();

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length < 2 || !Modes.Contains(args[0]))
        {
            Console.Error.WriteLine(
                "用法：DeskLanes.UiSnapshot.exe <category|panels|floating|sidebar|settings|dialog|perf|reference|perf200|mem|memtoggle> <输出> [--demo] [--top] [--screen]");
            return 1;
        }

        string mode = args[0];
        string output = Path.GetFullPath(args[1]);

        // --demo：用内置演示项目替代真实桌面内容，用于对外发布的截图（避免泄露私人文件名）
        bool demo = args.Skip(2).Any(a => string.Equals(a, "--demo", StringComparison.OrdinalIgnoreCase));

        // --top：设置窗口不滚到底，保持顶部（默认滚到底是为了拍到末尾分区）
        bool keepTop = args.Skip(2).Any(a => string.Equals(a, "--top", StringComparison.OrdinalIgnoreCase));

        // --screen：所有模式都用 GDI BitBlt 屏幕合成截图，而不只是 settings / dialog。
        // 视觉树快照抓不到 DWM 材质与桌面壁纸，对外展示「玻璃效果」必须带这个开关。
        bool screen = args.Skip(2).Any(a => string.Equals(a, "--screen", StringComparison.OrdinalIgnoreCase));

        // --keep-size：与 --screen 一起用时**不最大化**。
        // 最大化虽然避免了把真实桌面图标拍进对外截图，但窗口铺满整屏后
        // 也就没东西可"透"了——验证玻璃材质需要窗口化的窗口浮在真实桌面上。
        bool keepSize = args.Skip(2).Any(a => string.Equals(a, "--keep-size", StringComparison.OrdinalIgnoreCase));

        // --hold <毫秒>：截完图再保留窗口一段时间，让外部脚本能抓同一位置的
        // 桌面参照帧（用来证明"透出来的是壁纸"，而不是一块灰色。见 tools/glass-proof.py）。
        int holdMs = 0;
        foreach (string a in args.Skip(2))
        {
            if (a.StartsWith("--hold=", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(a["--hold=".Length..], out int v))
            {
                holdMs = v;
            }
        }

        // 屏幕合成截图需要物理像素坐标（与 GetWindowRect / GetDC 一致）
        SetProcessDPIAware();

        try
        {
            RunAsync(mode, output, demo, keepTop, screen, keepSize, holdMs).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"快照失败：{ex}");
            return 1;
        }
    }

    private static async Task RunAsync(string mode, string output, bool demo, bool keepTop,
                                       bool screen = false, bool keepSize = false, int holdMs = 0)
    {
        // 1. 复制真实配置到临时目录（只读使用，不污染真实配置）；--demo 时不复制
        string realDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskLanes");
        string tempDir = Path.Combine(Path.GetTempPath(), "DeskLanesSnap_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        if (!demo && Directory.Exists(realDir))
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

            // --demo 时用空扫描器顶掉真实桌面扫描：既不读取真实桌面，
            // 也因为 ScopeDirectories 为空而不会把演示项目误判为失效。
            IDesktopScanner scanner = demo ? new EmptyScanner() : new DesktopScanner(resolver);
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

            // 3. 演示数据（--demo 时项目也是演示的；否则分类/分区套在真实项目上）
            if (demo)
            {
                // perf 模式按 B6 的"200+ 项目"来压：12 个文件 + 3 个文件夹 + 205 个批量项 = 220。
                // mem 模式用 DESKLANES_MEM_ITEMS 控制批量项数量，用来画「项目数 → 内存」曲线。
                int bulk = mode switch
                {
                    "perf" or "perf200" => 205,
                    "mem" or "memtoggle" or "toggle" => ParseMemItemCount(),
                    _ => 0,
                };
                SeedDemoItems(workspace, tempDir, bulk);
            }

            if (mode == "panels")
            {
                workspace.SetLayoutMode(LayoutMode.FreePanels);
                SeedPanels(workspace);
            }
            else if (mode is "perf200" or "mem" or "memtoggle" or "toggle")
            {
                // 分区先建好但不切过去：切换耗时要在测量里现算
                SeedPanels(workspace);
                SeedCategories(workspace);
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
                    ShowActivated = screen,
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
                    ShowActivated = screen,
                };
            }
            else if (mode == "dialog")
            {
                window = new TextInputDialog("重命名分类", "请输入新的分类名称：", "工作")
                {
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    ShowActivated = screen,
                };
            }
            else if (mode == "floating" || mode == "floatingfolders")
            {
                var vm = new FloatingWindowViewModel(workspace, dialogService, launchService, shellState);
                if (mode == "floatingfolders")
                {
                    vm.ToggleViewModeCommand.Execute(null);   // 拍阶段 19 的文件夹视图
                }
                window = new FloatingWindow(vm, controller)
                {
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    ShowActivated = screen,
                };
            }
            else if (mode == "sidebar")
            {
                var vm = new SidebarViewModel(workspace, dialogService, launchService, shellState);
                window = new SidebarWindow(vm, controller)
                {
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    ShowActivated = screen,
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
            else if (mode == "reference")
            {
                // 参照窗口：只贴材质、不放任何内容。
                // 用来区分两种失败：材质本身没生效（参照也一样平），
                // 还是我们的 XAML 把它糊住了（参照很透、主窗口很平）。
                window = new Window
                {
                    Title = "参照-纯玻璃",
                    Width = 900,
                    Height = 620,
                    Background = Brushes.Transparent,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    ShowActivated = true,
                };
                WindowBackdropService.Attach(window);
            }
            else
            {
                window = new MainWindow(mainViewModel, controller)
                {
                    DataContext = mainViewModel,
                    Width = 1400,
                    Height = 900,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    // WPF 不允许 ShowActivated=false 与 WindowState=Maximized 同时存在，
                    // 否则 Show() 直接抛 InvalidOperationException。--screen 要最大化
                    // （不然会把真实桌面图标拍进对外截图），所以这里放开激活——
                    // 截图工具抢一下焦点无所谓。
                    ShowActivated = screen,
                };

                // --screen：屏幕合成截图会把窗口之外的桌面一起拍进来，
                // 真实桌面图标的文件名就跟着泄露了。默认主窗口最大化——背景只剩壁纸，
                // 而且这也正好是"全屏工作空间"的真实形态。
                // 但 --keep-size 时不最大化：验证玻璃材质必须留一圈真实桌面在窗外。
                if (screen && !keepSize)
                {
                    window.WindowState = WindowState.Maximized;
                }
            }

            dialogService.SetOwner(window);

            // 窗口渲染后：先自行触发首次扫描（快照工具不走完整应用启动流程），
            // 再等待初始化与图标加载完成后截图。
            window.ContentRendered += async (_, _) =>
            {
                try
                {
                    await mainViewModel.InitializeAsync();

                    // --demo：默认停在一个真实分类上，否则主区域只有"未分组"里的 1 个项目
                    if (demo && mainViewModel.CategoryNav.Categories.Count > 1)
                    {
                        mainViewModel.CategoryNav.SelectedCategory =
                            mainViewModel.CategoryNav.Categories[1];
                    }

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

                    // perf 模式：不截图，改测「分类导航 ↔ 自由分区」切换耗时（B6 最后一项）
                    if (mode == "perf")
                    {
                        await MeasureLayoutToggleAsync(window, mainViewModel, demo);
                        return;
                    }

                    if (mode == "perf200")
                    {
                        await MeasurePerf200Async(window, mainViewModel, output);
                        return;
                    }

                    if (mode == "mem")
                    {
                        await MeasureMemoryAsync(window, mainViewModel, output, ParseMemItemCount());
                        return;
                    }

                    if (mode == "memtoggle")
                    {
                        await MeasureMemoryToggleAsync(window, mainViewModel, output, ParseMemItemCount());
                        return;
                    }

                    if (mode == "toggle")
                    {
                        // 与 perf200 一样先切到项目最多的分类，否则默认分类只有十来项。
                        var largest = mainViewModel.CategoryNav.Categories
                            .OrderByDescending(c => c.ItemCount)
                            .FirstOrDefault();
                        if (largest is not null)
                        {
                            mainViewModel.CategoryNav.SelectedCategory = largest;
                        }
                        await window.Dispatcher.InvokeAsync(
                            () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                        await MeasureLayoutToggleAsync(window, mainViewModel, demo);
                        return;
                    }

                    // 设置窗口内容需要滚动：取证时滚到底，才能拍到末尾的分区
                    if (mode == "settings" && !keepTop)
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

                    if (mode is "settings" or "dialog" || screen)
                    {
                        // 屏幕合成截图：能看到 DWM 实际渲染的 Acrylic 毛玻璃与透出的壁纸。
                        // 视觉树快照做不到这一点，所以对外展示玻璃效果必须走这条路。
                        IntPtr captureHwnd = new WindowInteropHelper(window).Handle;
                        ActivateForCapture(captureHwnd);
                        CaptureWindowToPng(captureHwnd, output);
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

                    // --hold：截完先别关，留给外部脚本抓「窗口后面原本是什么」的参照帧
                    if (holdMs > 0)
                    {
                        await Task.Delay(holdMs);
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
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);

    /// <summary>
    /// 把窗口点成活动窗口再截图。Win11 的 Acrylic 只对**活动窗口**渲染，
    /// 非活动会退成近乎实心的底色 —— 不点一下，截出来的永远是"平板"，
    /// 看起来像材质没生效（这个坑踩过）。
    /// 点的是窗口右下角的状态栏区域，不触发任何操作。
    /// </summary>
    private static void ActivateForCapture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        SetForegroundWindow(hwnd);
        System.Threading.Thread.Sleep(250);
        if (GetForegroundWindow() != hwnd)
        {
            if (GetWindowRect(hwnd, out RECT r))
            {
                SetCursorPos(r.Right - 40, r.Bottom - 12);
                System.Threading.Thread.Sleep(120);
                mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);   // LEFTDOWN
                mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);   // LEFTUP
                System.Threading.Thread.Sleep(350);
            }
        }
    }

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

    // ============ 布局切换性能（B6：分类导航 ↔ 自由分区切换 < 1 秒） ============

    /// <summary>切换一次布局并等待渲染完成，返回耗时（毫秒）。</summary>
    private static async Task<double> ToggleLayoutOnceAsync(Window window, MainViewModel viewModel)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        viewModel.ToggleLayoutCommand.Execute(null);

        // ContextIdle 的优先级低于 Render / Loaded：等它被执行到，说明这一轮的
        // 布局与渲染请求都已经被处理完（比 Thread.Sleep 可靠，也不依赖固定延时）。
        await window.Dispatcher.InvokeAsync(
            () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);

        window.UpdateLayout();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }


    /// <summary>把一组耗时压成 (中位数, 最小, 最大)，报表只用中位数判断。</summary>
    private static (double med, double min, double max) Summarize(List<double> xs)
    {
        if (xs.Count == 0)
        {
            return (0, 0, 0);
        }

        var sorted = xs.OrderBy(v => v).ToList();
        double med = sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
        return (med, sorted[0], sorted[^1]);
    }

    // ============ 内存分解诊断（阶段 7） ============
    // 阶段 4 量到 206 个瓦片 ≈508 MB 工作集，但托管堆只有 30 MB。
    // 单看这两个数字分不清：真泄漏 / 瓦片实体化 / 渲染中间表面 / 只是工作集高水位。
    // 这个模式在同一进程里逐层拆除，每一步都记录四个口径，用差值定位。

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(
        IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

    private static readonly List<string> MemPoints = new();

    private static int ParseMemItemCount()
    {
        string? raw = Environment.GetEnvironmentVariable("DESKLANES_MEM_ITEMS");
        return int.TryParse(raw, out int n) ? Math.Clamp(n, 0, 2000) : 205;
    }

    /// <summary>强制完整 GC（含 LOH 压缩），否则工作集里可能还挂着已死对象。</summary>
    private static void ForceGc()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    /// <summary>把工作集交还给系统；用来区分"还活着的内存"与"曾经到过的高水位"。</summary>
    private static void TrimWorkingSet() =>
        SetProcessWorkingSetSize(
            System.Diagnostics.Process.GetCurrentProcess().Handle,
            new IntPtr(-1), new IntPtr(-1));

    private static void MemPoint(Window window, string stage, int items)
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        proc.Refresh();
        int tiles = FindVisualChildren<ItemTile>(window).Count();
        int images = FindVisualChildren<Image>(window).Count();
        int effects = FindVisualChildren<TextBlock>(window).Count(t => t.Effect is not null);
        string json = "{" +
            $"\"stage\":\"{stage}\"," +
            $"\"items\":{items}," +
            $"\"tiles\":{tiles}," +
            $"\"images\":{images}," +
            $"\"effects\":{effects}," +
            $"\"ws_mb\":{proc.WorkingSet64 / 1048576.0:F1}," +
            $"\"private_mb\":{proc.PrivateMemorySize64 / 1048576.0:F1}," +
            $"\"managed_mb\":{GC.GetTotalMemory(false) / 1048576.0:F1}," +
            $"\"heap_mb\":{GC.GetGCMemoryInfo().HeapSizeBytes / 1048576.0:F1}" +
            "}";
        MemPoints.Add(json);
        Console.WriteLine(json);
    }

    /// <summary>
    /// 逐层拆除视图，记录每一步的内存。顺序：原始 → GC → 裁剪工作集 → 摘文字投影
    /// → 摘图标 → 卸载瓦片视觉树 → 最后再 GC+裁剪。
    /// 每一步都等一帧渲染，避免把"还没来得及释放"误读成"占用"。
    /// </summary>
    private static async Task MeasureMemoryAsync(Window window, MainViewModel viewModel,
                                                 string outPath, int items)
    {
        var biggest = viewModel.CategoryNav.Categories
            .OrderByDescending(c => c.ItemCount)
            .FirstOrDefault();
        if (biggest is not null)
        {
            viewModel.CategoryNav.SelectedCategory = biggest;
        }

        await window.Dispatcher.InvokeAsync(
            () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        window.UpdateLayout();
        await Task.Delay(800);

        MemPoint(window, "raw", items);

        ForceGc();
        await Task.Delay(300);
        MemPoint(window, "after_gc", items);

        TrimWorkingSet();
        await Task.Delay(300);
        MemPoint(window, "after_trim", items);

        int strippedEffects = 0;
        foreach (TextBlock tb in FindVisualChildren<TextBlock>(window))
        {
            if (tb.Effect is not null)
            {
                tb.Effect = null;
                strippedEffects++;
            }
        }
        window.UpdateLayout();
        ForceGc();
        TrimWorkingSet();
        await Task.Delay(300);
        MemPoint(window, "effects_off", items);

        int strippedIcons = 0;
        foreach (Image img in FindVisualChildren<Image>(window))
        {
            if (img.Source is not null)
            {
                img.Source = null;
                strippedIcons++;
            }
        }
        window.UpdateLayout();
        ForceGc();
        TrimWorkingSet();
        await Task.Delay(300);
        MemPoint(window, "icons_off", items);

        // 卸载所有 ListBox 的 ItemsSource：容器与 ItemTile 视觉树整体出栈
        foreach (ListBox box in FindVisualChildren<ListBox>(window))
        {
            box.ItemsSource = null;
        }
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(
            () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        ForceGc();
        TrimWorkingSet();
        await Task.Delay(300);
        MemPoint(window, "tiles_unloaded", items);

        string json = "{\"stripped_effects\":" + strippedEffects +
                      ",\"stripped_icons\":" + strippedIcons +
                      ",\"points\":[" + string.Join(",", MemPoints) + "]}";
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, json);
        Console.WriteLine("MEM_RESULT " + json);
    }

    /// <summary>
    /// 连续切换「分类导航 ↔ 自由分区」时的内存曲线：如果私有提交随切换次数线性涨，
    /// 就是切换时泄漏；如果第一次涨完就走平，那只是两套视图各自的常驻成本。
    /// </summary>
    private static async Task MeasureMemoryToggleAsync(Window window, MainViewModel viewModel,
                                                       string outPath, int items)
    {
        var biggest = viewModel.CategoryNav.Categories
            .OrderByDescending(c => c.ItemCount)
            .FirstOrDefault();
        if (biggest is not null)
        {
            viewModel.CategoryNav.SelectedCategory = biggest;
        }

        await window.Dispatcher.InvokeAsync(
            () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        window.UpdateLayout();
        await Task.Delay(800);

        MemPoint(window, "raw", items);
        await ToggleLayoutOnceAsync(window, viewModel);
        MemPoint(window, "toggle_00_warmup", items);

        for (int i = 1; i <= 12; i++)
        {
            await ToggleLayoutOnceAsync(window, viewModel);
            MemPoint(window, "toggle_" + i.ToString("00"), items);
        }

        ForceGc();
        TrimWorkingSet();
        await Task.Delay(500);
        MemPoint(window, "after_gc_trim", items);

        string json = "{\"points\":[" + string.Join(",", MemPoints) + "]}";
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, json);
        Console.WriteLine("MEM_RESULT " + json);
    }

    /// <summary>
    /// 200+ 项目的渲染性能：首屏、布局切换、滚动、缩放。
    /// 设 DESKLANES_NO_TEXT_SHADOW=1 可以在同一个构建上关掉文字投影做对照，
    /// 免得为了对比再去改代码重编译（那会把编译差异混进结论）。
    /// </summary>
    private static async Task MeasurePerf200Async(Window window, MainViewModel viewModel, string outPath)
    {
        // 先切到项目最多的那个分类：默认选中的分类只有十来项，
        // 在这样的视图上测 220 项的性能等于没测。
        var biggest = viewModel.CategoryNav.Categories
            .OrderByDescending(c => c.ItemCount)
            .FirstOrDefault();
        if (biggest is not null)
        {
            viewModel.CategoryNav.SelectedCategory = biggest;
        }

        // 等首屏：Items 到位 + ContextIdle 跑完 = 瓦片都进了视觉树并完成布局
        await window.Dispatcher.InvokeAsync(
            () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        window.UpdateLayout();
        double openMs = ProgramClock.Elapsed.TotalMilliseconds;
        int itemCount = viewModel.Items.Count;
        int visibleTiles = FindVisualChildren<ItemTile>(window).Count();

        bool stripShadow = Environment.GetEnvironmentVariable("DESKLANES_NO_TEXT_SHADOW") == "1";
        int stripped = 0;
        if (stripShadow)
        {
            foreach (TextBlock tb in FindVisualChildren<TextBlock>(window))
            {
                if (tb.Effect is not null)
                {
                    tb.Effect = null;
                    stripped++;
                }
            }

            window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(
                () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        // 布局切换：先热身一次（首次要实例化另一套视图），再取 5 次中位数
        await ToggleLayoutOnceAsync(window, viewModel);
        var toggles = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            toggles.Add(await ToggleLayoutOnceAsync(window, viewModel));
        }

        // 滚动：取内容区那个 ScrollViewer（窗口里还有 TextBox 模板自带的）
        // 必须挑"真的能滚"的那个：窗口里还有 TextBox 模板自带的 ScrollViewer 和
        // 分区画布，按高度挑会挑到 ScrollableHeight = 0 的，测出来永远是 0.0ms。
        ScrollViewer? content = FindVisualChildren<ScrollViewer>(window)
            .Where(s => s.ScrollableHeight > 0)
            .OrderByDescending(s => s.ScrollableHeight)
            .FirstOrDefault();
        var scrollTimes = new List<double>();
        double cpuBefore = System.Diagnostics.Process.GetCurrentProcess()
            .TotalProcessorTime.TotalMilliseconds;
        if (content is not null)
        {
            for (int i = 0; i < 10; i++)
            {
                double target = content.ScrollableHeight * (i / 9.0);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                content.ScrollToVerticalOffset(target);

                // 必须等"下一帧真的画出来"再停表：只调 UpdateLayout 的话
                // WPF 会把布局推迟到渲染阶段，量出来是 0.3ms 的假数字（踩过）。
                var frame = new TaskCompletionSource<bool>();
                EventHandler onRendering = null!;
                onRendering = (_, _) =>
                {
                    CompositionTarget.Rendering -= onRendering;
                    frame.TrySetResult(true);
                };
                CompositionTarget.Rendering += onRendering;
                Task done = await Task.WhenAny(frame.Task, Task.Delay(2000));
                if (done != frame.Task)
                {
                    CompositionTarget.Rendering -= onRendering;
                }

                sw.Stop();
                scrollTimes.Add(sw.Elapsed.TotalMilliseconds);
            }
        }

        double cpuAfter = System.Diagnostics.Process.GetCurrentProcess()
            .TotalProcessorTime.TotalMilliseconds;

        // 缩放：改窗口尺寸 + 等布局稳定
        var resizeTimes = new List<double>();
        foreach ((int w, int h) in new[] { (1200, 800), (1600, 1000), (1400, 900), (1800, 1100), (1400, 900) })
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            window.Width = w;
            window.Height = h;
            window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(
                () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            sw.Stop();
            resizeTimes.Add(sw.Elapsed.TotalMilliseconds);
        }

        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var (tMed, tMin, tMax) = Summarize(toggles);
        var (sMed, sMin, sMax) = Summarize(scrollTimes);
        var (rMed, rMin, rMax) = Summarize(resizeTimes);

        // WorkingSet64 由系统按内存压力管理，单次读数不等于"真实占用"。
        // 同时记私有提交（进程真正要到的内存）与裁剪后工作集，避免把高水位当泄漏。
        proc.Refresh();
        double wsMb = proc.WorkingSet64 / 1048576.0;
        double peakWsMb = proc.PeakWorkingSet64 / 1048576.0;
        double privateMb = proc.PrivateMemorySize64 / 1048576.0;
        TrimWorkingSet();
        await Task.Delay(300);
        proc.Refresh();
        double trimmedWsMb = proc.WorkingSet64 / 1048576.0;

        string json = "{" +
            $"\"shadow\":{(stripShadow ? "false" : "true")}," +
            $"\"stripped_textblocks\":{stripped}," +
            $"\"item_count\":{itemCount}," +
            $"\"visible_tiles\":{visibleTiles}," +
            $"\"scrollable_height\":{(content?.ScrollableHeight ?? 0):F0}," +
            $"\"open_ms\":{openMs:F1}," +
            $"\"toggle_ms\":{tMed:F1},\"toggle_min\":{tMin:F1},\"toggle_max\":{tMax:F1}," +
            $"\"scroll_ms\":{sMed:F1},\"scroll_min\":{sMin:F1},\"scroll_max\":{sMax:F1}," +
            $"\"resize_ms\":{rMed:F1},\"resize_min\":{rMin:F1},\"resize_max\":{rMax:F1}," +
            $"\"scroll_cpu_ms\":{cpuAfter - cpuBefore:F1}," +
            $"\"working_set_mb\":{wsMb:F1}," +
            $"\"peak_working_set_mb\":{peakWsMb:F1}," +
            $"\"trimmed_working_set_mb\":{trimmedWsMb:F1}," +
            $"\"private_mb\":{privateMb:F1}," +
            $"\"managed_mb\":{GC.GetTotalMemory(false) / 1024.0 / 1024.0:F1}" +
            "}";

        Console.WriteLine(json);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, json);
    }

    private static async Task MeasureLayoutToggleAsync(Window window, MainViewModel viewModel, bool demo)
    {
        // 第一次切换要构造另一套视图（DataTemplate 首次实例化），代价不该算进用户体感
        double warmup = await ToggleLayoutOnceAsync(window, viewModel);

        var times = new List<double>();
        var directions = new List<(string, double)>();
        for (int i = 0; i < 10; i++)
        {
            // 记录"切到哪一侧"：两套视图的渲染代价不同，混在一起取中位数会盖掉慢的一侧
            string direction = viewModel.IsFreePanels ? "-> category nav" : "-> free panels";
            double ms = await ToggleLayoutOnceAsync(window, viewModel);
            times.Add(ms);
            directions.Add((direction, ms));
        }

        times.Sort();
        double median = times[times.Count / 2];
        double max = times[^1];

        // Numbers only (ASCII): this output gets pasted into docs, and the Windows
        // console mangles UTF-8 Chinese when piped.
        Console.WriteLine(string.Format("layout toggle -- {0} items ({1})",
            viewModel.Items.Count, demo ? "demo" : "real desktop"));
        Console.WriteLine("  each: " + string.Join(", ", times.Select(t => t.ToString("F1"))) + " ms");
        Console.WriteLine(string.Format("  warmup {0:F1} ms, median {1:F1} ms, max {2:F1} ms",
            warmup, median, max));

        // 两套视图的渲染代价不一样，按方向分开报，避免中位数把慢的那一侧盖掉
        var perDirection = new Dictionary<string, List<double>>();
        foreach ((string dir, double ms) in directions)
        {
            if (!perDirection.TryGetValue(dir, out List<double>? bucket))
            {
                bucket = new List<double>();
                perDirection[dir] = bucket;
            }

            bucket.Add(ms);
        }

        foreach (KeyValuePair<string, List<double>> pair in perDirection)
        {
            pair.Value.Sort();
            Console.WriteLine(string.Format("  {0}: median {1:F1} ms, max {2:F1} ms (n={3})",
                pair.Key, pair.Value[pair.Value.Count / 2], pair.Value[^1], pair.Value.Count));
        }

        bool ok = max < 1000.0;
        Console.WriteLine((ok ? "PASS" : "FAIL")
            + "  layout toggle max < 1 s (B6) -- "
            + max.ToString("F1") + " ms");
        if (!ok)
        {
            Environment.ExitCode = 1;
        }
    }

    /// <summary>
    /// --demo 用的空扫描器：不碰真实桌面。
    /// 返回的 <see cref="ScanResult.ScopeDirectories"/> 为空，合并时失效判定不生效，
    /// 因此演示项目不会被误标成"失效"。
    /// </summary>
    private sealed class EmptyScanner : IDesktopScanner
    {
        public Task<ScanResult> ScanAsync(
            ScanOptions? options = null,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ScanResult
            {
                Items = Array.Empty<DesktopItem>(),
                Warnings = Array.Empty<string>(),
                ScopeDirectories = Array.Empty<string>(),
                ScannedAt = DateTime.Now,
            });
    }

    /// <summary>
    /// --demo 用的演示项目：在临时目录里造一批中性名称的文件与文件夹，
    /// 让对外发布的截图既能体现真实界面密度，又不会泄露真实桌面文件名。
    /// </summary>
    private static void SeedDemoItems(IWorkspaceService workspace, string tempDir, int extraCount = 0)
    {
        string root = Path.Combine(tempDir, "demo-items");
        Directory.CreateDirectory(root);

        string[] files =
        {
            "项目计划书.docx", "季度报表.xlsx", "需求评审.pptx", "会议纪要.docx",
            "客户名单.xlsx", "设计稿.png", "参考资料.pdf", "合同模板.docx",
            "预算表.xlsx", "截图.png", "说明.txt", "待办清单.txt",
        };

        foreach (string name in files)
        {
            string path = Path.Combine(root, name);
            File.WriteAllText(path, "demo");
            workspace.AddCustomItem(path);
        }

        foreach (string name in new[] { "归档 2026", "素材库", "临时下载" })
        {
            string dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            workspace.AddCustomItem(dir);
        }

        // 压力测试用的批量项目（perf 模式）
        for (int i = 0; i < extraCount; i++)
        {
            string path = Path.Combine(root, $"批量项目-{i:D3}.txt");
            File.WriteAllText(path, "bulk");
            workspace.AddCustomItem(path);
        }
    }

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
