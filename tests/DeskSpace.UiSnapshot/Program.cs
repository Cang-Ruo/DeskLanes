// ============================================================================
// DeskSpace 界面快照工具（阶段 2 视觉验证用）
// ----------------------------------------------------------------------------
// 用法：DeskSpace.UiSnapshot.exe <category|panels> <输出.png>
//
// 行为：
//   1. 把真实配置目录（%LocalAppData%\DeskSpace）复制到临时目录，绝不写回真实配置；
//   2. 组装与真实应用完全相同的服务与 MainViewModel，启动 MainWindow；
//   3. 按模式预置演示数据（分类导航：2 个分类 + 归属；自由分区：2 个分区 + 归属）；
//   4. 渲染完成后用 RenderTargetBitmap 截取窗口，保存 PNG 后退出。
// 退出码：0 = 成功；1 = 失败（消息打到 stderr）。
//
// 注意：本工具只用于开发期视觉验证，不参与应用运行时逻辑。
// ============================================================================

using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskSpace.App;
using DeskSpace.App.Services;
using DeskSpace.App.ViewModels;
using DeskSpace.Core.Models;
using DeskSpace.Core.Services;

namespace DeskSpace.UiSnapshot;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 2 || (args[0] != "category" && args[0] != "panels"))
        {
            Console.Error.WriteLine("用法：DeskSpace.UiSnapshot.exe <category|panels> <输出.png>");
            return 1;
        }

        string mode = args[0];
        string output = Path.GetFullPath(args[1]);

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
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskSpace");
        string tempDir = Path.Combine(Path.GetTempPath(), "DeskSpaceSnap_" + Guid.NewGuid().ToString("N")[..8]);
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
            var viewModel = new MainViewModel(workspace, scanner, iconService, launchService, dialogService, shellState);

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

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var window = new MainWindow(viewModel, controller)
            {
                DataContext = viewModel,
                Width = 1400,
                Height = 900,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowActivated = false,
            };
            dialogService.SetOwner(window);

            // 窗口渲染后：先自行触发首次扫描（快照工具不走完整应用启动流程），
            // 再等待初始化与图标加载完成后截图。
            window.ContentRendered += async (_, _) =>
            {
                try
                {
                    await viewModel.InitializeAsync();

                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    await Task.Delay(300);
                    while ((viewModel.IsLoading || viewModel.Items.Count == 0) &&
                           stopwatch.ElapsedMilliseconds < 30000)
                    {
                        await Task.Delay(200);
                    }

                    await Task.Delay(800); // 布局与图标稳定
                    window.UpdateLayout();

                    int width = (int)window.ActualWidth;
                    int height = (int)window.ActualHeight;
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window);

                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(output);
                    encoder.Save(stream);

                    Console.WriteLine($"快照已保存：{output}（{width}x{height}，模式 {mode}）");
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
