using System.IO;
using System.Windows;
using System.Windows.Threading;
using DeskLanes.App.Services;
using DeskLanes.App.ViewModels;
using DeskLanes.Core.Models;
using DeskLanes.Core.Services;

namespace DeskLanes.App;

public partial class App : Application
{
    private AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 全局异常兜底：记录日志 + 友好提示，避免应用静默崩溃
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // 局部亮度自适应文字色：必须在任何窗口 Show 之前抓这一帧纯桌面，
        // 否则采到的是窗口自己的文字/图标。失败时服务自动退回静态文字色。
        DynamicTextService.Instance.CaptureDesktop();

        // 手动组装依赖（阶段规模小，暂不引入 DI 容器）
        IShortcutResolver shortcutResolver = new ShortcutResolver();
        IDesktopScanner scanner = new DesktopScanner(shortcutResolver);
        IIconService iconService = new IconService();
        ILaunchService launchService = new LaunchService();
        var dialogService = new WpfDialogService();

        string baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeskLanes");

        // 旧版（DeskSpace）配置一次性迁移：旧目录原样保留，复制而非移动，失败也不影响启动。
        ConfigDirectoryMigrator.TryMigrate(baseDir, "DeskSpace");

        // 应用控制器：三种窗口模式 + 全局快捷键 + 系统托盘 + 单实例
        _controller = new AppController(scanner, iconService, launchService, dialogService, baseDir);

        if (!_controller.Startup())
        {
            // 已有实例在运行：唤起消息已发给第一实例（其窗口将自动前置），本进程直接退出。
            // 注意：这里不能弹模态对话框——它会阻塞 Shutdown()，导致第二个进程长时间残留。
            Shutdown();
            return;
        }

        MainWindow = _controller.MainWindow;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 退出时释放：全局快捷键、文件监听器、托盘图标、单实例互斥体
        _controller?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException(e.Exception);
        MessageBox.Show(
            $"发生了未预期的错误：\n{e.Exception.Message}\n\n详细信息已记录到 %LocalAppData%\\DeskLanes\\logs\\errors.log。",
            "DeskLanes", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogException(ex);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogException(e.Exception);
        e.SetObserved();
    }

    private static void LogException(Exception ex)
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeskLanes", "logs");

            // errors.log 之前只追加不整理：崩溃循环里会一直长。
            // 走与 sync.log 同一套轮转（单份 256 KB，留 2 份归档），
            // 这样排障时还能拿到"出事之前"的那一段。
            var writer = new LogFileWriter(Path.Combine(directory, "errors.log"), 256 * 1024, 2);
            writer.Append($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}");
        }
        catch
        {
            // 日志写入失败不能影响应用
        }
    }
}
