using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace DeskLanes.App.Services;

/// <summary>文字层级：与 GlassTheme 的 primary / secondary / tertiary 对应。</summary>
public enum DynamicTextTone
{
    None,
    Primary,
    Secondary,
    Tertiary,
}

/// <summary>玻璃上文字的"局部亮度自适应"。</summary>
/// <remarks>
/// 用法（XAML）：
///   xmlns:dt="clr-namespace:DeskLanes.App.Services"
///   &lt;Border dt:DynamicText.Scrim="True" ...&gt;   ← 名字牌：底与字一起切
///   &lt;TextBlock dt:DynamicText.Tone="Primary" /&gt; ← 没有局部底的文字：只切字色
///
/// 只在**窗口显示前抓一次纯桌面**（见 DesktopLuminance），之后按控件当前的屏幕
/// 坐标查那张亮度网格。滚动 / 拖拽 / 窗口缩放时只是重新查表，不重新抓屏、不逐帧
/// 重算颜色，所以没有"滚一下字就闪一下"的问题。颜色切换另有 0.8 的滞回。
///
/// 失败兜底：抓屏失败、窗口未激活、或被 DESKLANES_BACKDROP=none 强制关掉材质时，
/// 一律回到原来的浅色字 + 深色局部底，界面不会因此变样或崩掉。
/// </remarks>
public static class DynamicText
{
    public static readonly DependencyProperty ScrimProperty = DependencyProperty.RegisterAttached(
        "Scrim", typeof(bool), typeof(DynamicText),
        new PropertyMetadata(false, OnRegistrationChanged));

    public static readonly DependencyProperty ToneProperty = DependencyProperty.RegisterAttached(
        "Tone", typeof(DynamicTextTone), typeof(DynamicText),
        new PropertyMetadata(DynamicTextTone.None, OnRegistrationChanged));

    public static void SetScrim(DependencyObject element, bool value) => element.SetValue(ScrimProperty, value);
    public static bool GetScrim(DependencyObject element) => (bool)element.GetValue(ScrimProperty);

    public static void SetTone(DependencyObject element, DynamicTextTone value) => element.SetValue(ToneProperty, value);
    public static DynamicTextTone GetTone(DependencyObject element) => (DynamicTextTone)element.GetValue(ToneProperty);

    private static void OnRegistrationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.Loaded -= OnLoaded;
        element.Unloaded -= OnUnloaded;
        element.Loaded += OnLoaded;
        element.Unloaded += OnUnloaded;
        if (element.IsLoaded)
        {
            Register(element);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs _) => Register((FrameworkElement)sender);
    private static void OnUnloaded(object sender, RoutedEventArgs e) => DynamicTextService.Instance.Unregister((FrameworkElement)sender);

    private static void Register(FrameworkElement element)
    {
        bool scrim = GetScrim(element);
        DynamicTextTone tone = GetTone(element);
        if (!scrim && tone == DynamicTextTone.None)
        {
            return;
        }

        // 名字牌里的 TextBlock 由外层 Border 统一管理，避免两个决策互相覆盖。
        if (!scrim && HasScrimAncestor(element))
        {
            return;
        }

        DynamicTextService.Instance.Register(element, scrim, tone);
    }

    private static bool HasScrimAncestor(FrameworkElement element)
    {
        DependencyObject? parent = VisualTreeHelper.GetParent(element);
        while (parent is not null)
        {
            if (parent is FrameworkElement fe && GetScrim(fe))
            {
                return true;
            }
            parent = VisualTreeHelper.GetParent(parent);
        }
        return false;
    }
}

/// <summary>持有亮度网格、颜色决策与控件注册表。进程内单例。</summary>
public sealed class DynamicTextService
{
    /// <summary>切换颜色需要的额外收益，避免临界亮度来回翻。</summary>
    private const double SwitchMargin = 0.8;

    private static readonly SolidColorBrush LightPrimary = Frozen(0xF1, 0xF5, 0xFA);
    private static readonly SolidColorBrush LightSecondary = Frozen(0xB7, 0xC6, 0xD8);
    private static readonly SolidColorBrush LightTertiary = Frozen(0x8C, 0xA0, 0xB8);
    private static readonly SolidColorBrush DarkPrimary = Frozen(0x08, 0x11, 0x1C);
    private static readonly SolidColorBrush DarkSecondary = Frozen(0x1B, 0x2B, 0x3D);
    private static readonly SolidColorBrush DarkTertiary = Frozen(0x2E, 0x42, 0x58);
    private static readonly SolidColorBrush ScrimDark = Frozen(0x4D, 0x00, 0x00, 0x00);
    // 45% 白：30% 时亮壁纸下浅底实测对比度最低只有 4.21，差 4.5 一点点。
    // 只加深"深色字那一侧"的局部牌，浅色字那一侧仍是原来的 30% 黑底。
    private static readonly SolidColorBrush ScrimLight = Frozen(0x73, 0xFF, 0xFF, 0xFF);

    // 29% 基底 tint（#4A20334A），色值亮度约 47.9。由阶段 9 的 18%/26%/29% A/B 标定：
    // effective = (1-0.29) * 桌面亮度 + 0.29 * 47.9，暗/亮壁纸两次实测都吻合。
    private const double BaseTintAlpha = 0.29;
    private const double TintLuma = 47.9;

    private readonly DesktopLuminance _luminance = new();
    private readonly List<WeakReference<FrameworkElement>> _controls = new();
    private readonly ConditionalWeakTable<FrameworkElement, State> _states = new();
    private readonly HashSet<Window> _hookedWindows = new();
    private bool _refreshQueued;
    private bool _diagnostics;
    private int _totalFlips;
    private int _lastGoodCount;
    private bool _enabled;
    private string _luminanceSource = "none";
    private string? _lastSignature;
    private int _desktopRefreshes;
    private DispatcherTimer? _pendingRefresh;

    private sealed class State
    {
        public bool? SchemeIsDark;
        public int Flips;
    }

    public static DynamicTextService Instance { get; } = new();

    /// <summary>在第一个窗口 Show 之前调用。失败不影响启动，只是退回静态文字色。</summary>
    public void CaptureDesktop()
    {
        _diagnostics = Environment.GetEnvironmentVariable("DESKLANES_DIAG_TEXT") == "1";
        // 诊断/回退开关：跳过桌面抓帧与自适应，界面退回阶段 9 的静态浅色字。
        // 用来把"启动时那一次抓屏"的成本与其它因素分开量。
        if (Environment.GetEnvironmentVariable("DESKLANES_NO_DYNAMIC_TEXT") == "1")
        {
            _luminanceSource = "disabled";
            return;
        }
        // 原生材质被诊断开关强制关掉时，窗口是实心底色，不需要也不应该动态变色。
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DESKLANES_BACKDROP")))
        {
            _luminanceSource = "disabled";
            return;
        }
        _enabled = true;
        if (_luminance.Capture())
        {
            _luminanceSource = "screen_capture";
        }
        _lastSignature = EnvironmentSignature();
        RequestRefresh();
    }

    /// <summary>壁纸 / 显示器拓扑变化：延迟 1.5 秒合并连续消息后重建亮度网格。</summary>
    public void OnEnvironmentChanged()
    {
        if (!_enabled)
        {
            return;
        }
        if (_pendingRefresh is null)
        {
            _pendingRefresh = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(1.5),
            };
            _pendingRefresh.Tick += (_, _) =>
            {
                _pendingRefresh.Stop();
                RefreshFromWallpaper();
            };
        }
        _pendingRefresh.Stop();
        _pendingRefresh.Start();
    }

    private string EnvironmentSignature()
    {
        // 网格数会随显示器拓扑变化，把它一起算进签名，换分辨率但壁纸没变也能重算
        return (DesktopLuminance.CurrentWallpaperPath() ?? "") + "|" + _luminance.CellCount;
    }

    private void RefreshFromWallpaper()
    {
        string? path = DesktopLuminance.CurrentWallpaperPath();
        if (path is null)
        {
            return;
        }
        string signature = EnvironmentSignature();
        if (signature == _lastSignature)
        {
            return; // 不是壁纸/拓扑变化，别白解码一次
        }
        if (!_luminance.SetFromWallpaper(path))
        {
            return; // 解码失败：保留启动时的网格，不假装更新过
        }
        _lastSignature = signature;
        _luminanceSource = "wallpaper_bitmap";
        _desktopRefreshes++;
        RequestRefresh();
    }

    public void Register(FrameworkElement element, bool scrim, DynamicTextTone tone)
    {
        foreach (var weak in _controls)
        {
            if (weak.TryGetTarget(out var existing) && ReferenceEquals(existing, element))
            {
                return;
            }
        }
        _controls.Add(new WeakReference<FrameworkElement>(element));

        if (Window.GetWindow(element) is Window window && _hookedWindows.Add(window))
        {
            window.SizeChanged += (_, _) => RequestRefresh();
            window.LocationChanged += (_, _) => RequestRefresh(); // 拖到别的屏 / 测试脚本摆位
            window.IsVisibleChanged += (_, _) => RequestRefresh(); // 隐藏窗口恢复显示
            window.Activated += (_, _) => RequestRefresh();
            window.Deactivated += (_, _) => RequestRefresh();
            window.LayoutUpdated += (_, _) => RequestRefresh();
            window.AddHandler(
                ScrollViewer.ScrollChangedEvent,
                new ScrollChangedEventHandler((_, _) => RequestRefresh()));
        }

        RequestRefresh();
    }

    public void Unregister(FrameworkElement element)
    {
        _controls.RemoveAll(w => !w.TryGetTarget(out var target) || ReferenceEquals(target, element));
    }

    private void RequestRefresh()
    {
        if (_refreshQueued)
        {
            return;
        }
        _refreshQueued = true;
        Application.Current?.Dispatcher.BeginInvoke(
            () =>
            {
                _refreshQueued = false;
                RefreshAll();
            },
            DispatcherPriority.Loaded);
    }

    private void RefreshAll()
    {
        var decisions = _diagnostics ? new List<object>() : null;
        int darkSchemeCount = 0;
        int unusable = 0;

        _controls.RemoveAll(w => !w.TryGetTarget(out _));
        foreach (var weak in _controls.ToArray())
        {
            if (!weak.TryGetTarget(out var element) || !element.IsLoaded)
            {
                continue;
            }

            Window? window = Window.GetWindow(element);
            // 三种模式的窗口会同时被构造；隐藏窗口里的控件 PointToScreen 会给出
            // 屏幕外的假坐标（实测出现过 y=2554），必须排除。
            if (window is null || !window.IsVisible)
            {
                continue;
            }

            bool scrim = DynamicText.GetScrim(element);
            var tone = DynamicText.GetTone(element);
            var state = _states.GetValue(element, _ => new State());

            Point? screen = ScreenCenter(element);
            if (screen is null)
            {
                continue;
            }
            double desktop = _luminance.LumaAt(screen.Value.X, screen.Value.Y);
            if (desktop < 0)
            {
                // 滚出可视区 / 还没定位的控件：不参与决策，也不写进诊断
                continue;
            }
            // 还有一种"在屏幕内、但在窗口可视区外"的控件：ScrollViewer 内容可以
            // 延伸到窗口下方，PointToScreen 仍在虚拟屏范围内。再按窗口矩形裁一次。
            // 两个角都用 PointToScreen 取，避免拿 DIP 的 ActualWidth 配物理坐标。
            Point? origin = ScreenTopLeft(window);
            Point? corner = ScreenBottomRight(window);
            if (origin is not null && corner is not null &&
                (screen.Value.X < origin.Value.X - 8 ||
                 screen.Value.X > corner.Value.X + 8 ||
                 screen.Value.Y < origin.Value.Y - 8 ||
                 screen.Value.Y > corner.Value.Y + 8))
            {
                continue;
            }
            bool active = window.IsActive;
            bool usable = _luminance.IsAvailable && active;

            bool dark;
            double contrast;
            if (!usable)
            {
                dark = false; // 兜底：原来的浅色字 + 深色局部底
                contrast = 0;
                unusable++;
            }
            else
            {
                double effective = (1 - BaseTintAlpha) * desktop + BaseTintAlpha * TintLuma;
                // 有局部底时，两种方案各自带自己的底；没有底时就是玻璃本身。
                double bgLight = scrim ? effective * 0.70 : effective;                 // 30% 黑底
                double bgDark = scrim ? effective * 0.55 + 255 * 0.45 : effective;      // 45% 白底
                double lightContrast = Contrast(LightPrimary.Color, bgLight);
                double darkContrast = Contrast(DarkPrimary.Color, bgDark);

                dark = darkContrast > lightContrast;
                if (state.SchemeIsDark is bool previous && previous != dark)
                {
                    // 滞回：新方案必须明显更好才允许翻转
                    double current = previous ? darkContrast : lightContrast;
                    double candidate = previous ? lightContrast : darkContrast;
                    if (candidate < current + SwitchMargin)
                    {
                        dark = previous;
                    }
                }
                contrast = dark ? darkContrast : lightContrast;

                if (state.SchemeIsDark is bool old && old != dark)
                {
                    state.Flips++;
                    _totalFlips++;
                }
                state.SchemeIsDark = dark;

                if (!usable)
                {
                    // 兜底状态下不记入对比度统计，避免把"没参与自适应"的控件算成通过
                }
                else if (dark)
                {
                    darkSchemeCount++;
                }

                if (decisions is not null && screen is not null && usable)
                {
                    Point topLeft = ScreenTopLeft(element) ?? screen.Value;
                    decisions.Add(new
                    {
                        x = Math.Round(topLeft.X, 1),
                        y = Math.Round(topLeft.Y, 1),
                        w = Math.Round(element.ActualWidth, 1),
                        h = Math.Round(element.ActualHeight, 1),
                        desktop_luma = Math.Round(desktop, 2),
                        effective_luma = Math.Round(effective, 2),
                        scrim,
                        tone = tone.ToString(),
                        scheme = dark ? "dark_text" : "light_text",
                        contrast = Math.Round(contrast, 2),
                        flips = state.Flips,
                    });
                }
            }

            Apply(element, scrim, tone, dark);
        }

        if (decisions is not null && (decisions.Count > 0 || unusable > 0)
            && decisions.Count >= _lastGoodCount - 2)
        {
            // 失焦/布局中间态会触发一次"全部不可用"的刷新。别让那次空快照覆盖掉
            // 已经拍好的完整证据；只保留控件数最多的那次。
            _lastGoodCount = Math.Max(_lastGoodCount, decisions.Count);
            WriteDiagnostics(decisions, darkSchemeCount, unusable);
        }
    }

    private static void Apply(FrameworkElement element, bool scrim, DynamicTextTone tone, bool dark)
    {
        Brush textBrush = (dark, tone) switch
        {
            (false, DynamicTextTone.Secondary) => LightSecondary,
            (false, DynamicTextTone.Tertiary) => LightTertiary,
            (false, _) => LightPrimary,
            (true, DynamicTextTone.Secondary) => DarkSecondary,
            (true, DynamicTextTone.Tertiary) => DarkTertiary,
            (true, _) => DarkPrimary,
        };

        if (scrim && element is Border border)
        {
            border.Background = dark ? ScrimLight : ScrimDark;
            if (FirstTextBlock(border) is TextBlock child)
            {
                // 名字牌里的字一律按 primary 判：决策就是按 primary 算的
                child.Foreground = dark ? DarkPrimary : LightPrimary;
            }
        }
        else if (element is TextBlock textBlock)
        {
            textBlock.Foreground = textBrush;
        }
    }

    private static FrameworkElement? FirstTextBlock(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock text)
            {
                return text;
            }
            if (FirstTextBlock(child) is FrameworkElement found)
            {
                return found;
            }
        }
        return null;
    }

    private static Point? ScreenCenter(FrameworkElement element)
    {
        try
        {
            if (PresentationSource.FromVisual(element) is null ||
                element.ActualWidth <= 0 || element.ActualHeight <= 0)
            {
                return null;
            }
            return element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        }
        catch
        {
            return null;
        }
    }

    private static Point? ScreenTopLeft(FrameworkElement element)
    {
        try
        {
            return element.PointToScreen(new Point(0, 0));
        }
        catch
        {
            return null;
        }
    }

    private static Point? ScreenBottomRight(FrameworkElement element)
    {
        try
        {
            return element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        }
        catch
        {
            return null;
        }
    }

    private void WriteDiagnostics(List<object> decisions, int darkSchemeCount, int unusable)
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeskLanes", "diagnostics");
            Directory.CreateDirectory(directory);
            var payload = new
            {
                captured_at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                grid_cells = _luminance.CellCount,
                luminance_source = _luminanceSource,
                desktop_refreshes = _desktopRefreshes,
                controls = decisions.Count,
                unusable_controls = unusable,
                total_flips = _totalFlips,
                dark_scheme = darkSchemeCount,
                decisions,
            };
            string json = System.Text.Json.JsonSerializer.Serialize(payload,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(directory, "dynamic-text.json"), json);
        }
        catch
        {
            // 诊断输出不能影响界面
        }
    }

    private static SolidColorBrush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b) => Frozen(0xFF, r, g, b);

    /// <summary>WCAG 2.x 对比度，背景用 0~255 的灰阶还原成相对亮度。</summary>
    private static double Contrast(Color foreground, double gray)
    {
        double l1 = RelativeLuminance(foreground);
        double l2 = RelativeLuminance(Color.FromRgb(Gray(gray), Gray(gray), Gray(gray)));
        if (l1 < l2)
        {
            (l1, l2) = (l2, l1);
        }
        return (l1 + 0.05) / (l2 + 0.05);
    }

    private static byte Gray(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);

    private static double RelativeLuminance(Color c)
        => 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);

    private static double Linear(byte channel)
    {
        double v = channel / 255.0;
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }
}
