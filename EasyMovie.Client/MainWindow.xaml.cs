using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using EasyMovie.Client.Views;
using EasyMovie.Core.Enums;
using EasyMovie.Core.Models;
using EasyMovie.Data;
using MaterialDesignThemes.Wpf;
using Microsoft.EntityFrameworkCore;

using Serilog;

namespace EasyMovie.Client;

public partial class MainWindow : Window
{
    public static RoutedCommand SearchCommand { get; } = new();
    public static RoutedCommand AddNewCommand { get; } = new();
    public static RoutedCommand DeleteCommand { get; } = new();
    public static RoutedCommand DetailCommand { get; } = new();
    public static RoutedCommand EscapeCommand { get; } = new();
    public static RoutedCommand RefreshCommand { get; } = new();
    public static RoutedCommand SelectAllCommand { get; } = new();
    public static RoutedCommand Nav1Command { get; } = new();
    public static RoutedCommand Nav2Command { get; } = new();
    public static RoutedCommand Nav3Command { get; } = new();
    public static RoutedCommand Nav4Command { get; } = new();
    public static RoutedCommand CycleViewCommand { get; } = new();
    public static RoutedCommand ShortcutsHelpCommand { get; } = new();
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private const uint WM_SETICON = 0x0080;
    private const uint WM_GETICON = 0x007F;

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconFromResourceEx(byte[] pbIconBits, uint cbIconBits,
        bool fIcon, int dwVersion, int cxDesired, int cyDesired, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr LoadImage(IntPtr hInst, IntPtr name, uint type,
        int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(IntPtr lpModuleName);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_MAINWINDOW = 2;
    private const int DWMSBT_TRANSIENTWINDOW = 3;

    private const uint IMAGE_ICON = 1;
    private const uint LR_DEFAULTSIZE = 0x00000040;
    private const uint LR_SHARED = 0x00008000;
    private const int SM_CXICON = 11;
    private const int SM_CYICON = 12;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_DLGMODALFRAME = 0x00000001;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private static readonly IntPtr ICON_SMALL = IntPtr.Zero;
    private static readonly IntPtr ICON_BIG = new(1);

    private static HttpClient? _imgClient;
    private static HttpClient? _tmdbImgClient;
    private static HttpClient? _generalImgClient;

    private static async Task SavePosterToDb(Movie movie, byte[] bytes)
    {
        try
        {
            using var ctx = DbHelper.CreateContext();
            var dbMovie = await ctx.Movies.FindAsync(movie.Id);
            if (dbMovie != null)
            {
                dbMovie.PosterData = bytes;
                EasyMovie.Client.Helpers.PosterCache.Save(movie.Id, bytes);
                await ctx.SaveChangesAsync();
            }
        }
        catch (Exception ex) { Log.Error(ex, "MainWindow 操作异常"); }
    }

    private static HttpClient GetImageClient(string? url = null)
    {
        if (url != null && (url.Contains("themoviedb.org") || url.Contains("tmdb.org")))
        {
            if (_tmdbImgClient != null) return _tmdbImgClient;
            _tmdbImgClient = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(8) };
            _tmdbImgClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
            _tmdbImgClient.DefaultRequestHeaders.Add("Referer", "https://www.themoviedb.org/");
            return _tmdbImgClient;
        }

        if (url != null && !url.Contains("doubanio.com") && !url.Contains("douban.com"))
        {
            if (_generalImgClient != null) return _generalImgClient;
            _generalImgClient = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(8) };
            _generalImgClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
            return _generalImgClient;
        }

        if (_imgClient != null) return _imgClient;
        var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
        _imgClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        _imgClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
        _imgClient.DefaultRequestHeaders.Add("Referer", "https://movie.douban.com/");
        var cookie = Core.AppSettings.DoubanCookie;
        if (!string.IsNullOrEmpty(cookie)) _imgClient.DefaultRequestHeaders.Add("Cookie", cookie);
        return _imgClient;
    }

    public MainWindow()
    {
        App.LogStartup("MainWindow 构造开始(InitializeComponent 前)");
        InitializeComponent();
        App.LogStartup("MainWindow.InitializeComponent 完成");
        LoadInputBindings();
        SourceInitialized += (_, _) => VideoPlayerHelper.RestrictMaximizeToWorkArea(this);
        Loaded += OnLoaded;
        StateChanged += OnStateChanged;
        PlayerHost.Closed += PlayerHost_Closed;
        // 自动备份（同步拷贝整个数据库，可能数十 MB）不再于构造期派发——它会与启动期
        // Dashboard 预载查询争抢 SQLite 文件锁，实测把预载查询拖慢 ~2s。改由
        // MainWindow_ContentRendered（闪屏关闭后）再触发，避免拖慢首屏（见该处）。
        // 不在构造里同步创建 Dashboard（其 XAML 解析+首屏数据会拖长启动画面），
        // 改为窗口 Loaded 后再导航，让启动画面尽早关闭。
        App.LogStartup("MainWindow 构造完成(延迟 Dashboard 至 Loaded)");
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        MaximizeBtn.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                SendMessage(hwnd, WM_SETICON, ICON_SMALL, IntPtr.Zero);
                SendMessage(hwnd, WM_SETICON, ICON_BIG, IntPtr.Zero);
            }
            // 最大化不覆盖任务栏的 WM_GETMINMAXINFO 处理已在 SourceInitialized 注册
        }
        catch (Exception ex) { Log.Error(ex, "MainWindow 操作异常"); }

        RegisterNavButtons();
        // 先创建首屏 Dashboard（同步，让主窗口立即有内容）
        NavigateTo("Dashboard");
        // 关闭闪屏采用“主路径 + ContentRendered 兜底”双保险：
        // 主路径：等 Dashboard 首帧渲染完成（Render 优先级之后）经 ContextIdle 空闲优先级立即关闭。
        // 兜底：ContentRendered 事件——它不可单独依赖：主窗口可能在 OnLoaded 之前已完成首帧渲染，
        // 若该事件在注册前已触发则永远不会再触发（这正是“主界面都出来了启动画面还在”的根因：
        // 上一轮把 PreWarmViews 改为空闲延迟后首帧渲染提前完成，触发了这个回归）。
        ContentRendered += MainWindow_ContentRendered;   // 兜底（CloseSplash 幂等，重复调用无副作用）
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle,
            new Action(App.CloseSplash));               // 主路径
        // 其余 9 个导航页延迟到闪屏关闭后（ContextIdle 空闲优先级，排在 CloseSplash 之后）再构造加入可视树，
        // 保证首页数据绑定与闪屏关闭不被其同步构造阻塞。
        // 注意：PreWarmViews 是 async void，不能写成 `_ = PreWarmViews()`（void 不能赋给弃元）。
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle,
            new Action(PreWarmViews));

        // 启动里程碑：窗口已加载（OnLoaded 触发说明 WPF 已成功创建并显示主窗口）
        try
        {
            var dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "startup.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] MainWindow.OnLoaded 触发（主窗口已创建并显示）\n");
        }
        catch { /* 故意留空：里程碑日志写不进去只能放弃，此处再记仍会失败 */ }

        // 自测开关：--selftest <path> [fs] 自动加载影片并（全屏）播放，便于自动化截图验证 UI
        try
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--selftest" && i + 1 < args.Length)
                {
                    var path = args[i + 1];
                    bool fs = i + 2 < args.Length && args[i + 2] == "fs";
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        var m = new Movie { FilePath = path, Title = System.IO.Path.GetFileNameWithoutExtension(path) };
                        ShowMoviePlayer(m);
                        if (fs) Dispatcher.BeginInvoke(new Action(() => PlayerHost.EnterFullscreenForTest()), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    }), System.Windows.Threading.DispatcherPriority.Background);
                    break;
                }
            }
        }
        catch (Exception ex) { Log.Error(ex, "selftest 初始化失败"); }
    }

    /// <summary>主窗口首帧渲染完成后触发：此时所有导航页已在 PreWarmViews 中构造完毕、Dashboard 已显示，
    /// 可平滑关闭启动闪屏，实现“闪屏→主窗口”的无缝交接，避免两者重叠或空白闪烁。</summary>
    private void MainWindow_ContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= MainWindow_ContentRendered;
        App.LogStartup("ContentRendered 触发(关闭闪屏)");
        App.CloseSplash();
        // 自动备份（同步拷贝整个数据库，可能数十 MB）延后 15 秒执行：彻底错开启动期
        // Dashboard 预载查询与首页渲染，避免争抢 SQLite 文件锁 / 磁盘 IO。
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(15)); BackupService.EnsureAutoBackup(); }
            catch (Exception ex) { Log.Warning(ex, "启动后自动备份失败"); }
        });
    }

    private async void PreWarmViews()
    {
        // 创建页面实例、加入可视树、预加载数据
        // 首次加入可视树时 WPF 完成布局，后续切换只改 Visibility
        var pages = new (string key, Func<UserControl> create)[]
        {
            ("Dashboard", () => new DashboardView()),
            ("Movies", () => new MovieListView(this)),
            ("Categories", () => new CategoryTagManageView()),
            ("Statistics", () => new StatisticsView()),
            ("Settings", () => new SettingsView()),
            ("Calendar", () => new WatchCalendarView()),
            ("Relation", () => new MovieRelationView(this)),
            ("News", () => new MovieNewsView()),
            ("AI", () => new AIRecommendationView()),
            ("Heatmap", () => new WatchHeatmapView()),
        };

        // 阶段一：在 UI 线程一次性构造全部导航页（闪屏此时仍盖着主窗口，用户无感）。
        // 构造完成前绝不关闭闪屏，因此点击任何导航项都不会再触发同步 new 控件的卡顿。
        try
        {
            foreach (var (key, create) in pages)
            {
                // 每构造一个页面就让出 UI 线程：使主窗口首帧渲染与闪屏关闭不被“一次性构造 10 个页面”整体阻塞，
                // 闪屏在首页渲染后立即关闭，其余页面在后台静默补充（NavigateTo 已支持按需懒构造，不会重复添加）。
                await Task.Yield();
                if (_pageCache.ContainsKey(key)) continue;
                var view = create();
                _pageCache[key] = view;
                view.Visibility = Visibility.Collapsed;
                ContentArea.Children.Add(view);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PreWarmViews 页面构造失败");
        }
        // 注意：此处不再关闭闪屏。闪屏由 MainWindow.ContentRendered 在“主窗口首帧真正渲染完成”后
        // 平滑关闭（见 OnLoaded 注册的 MainWindow_ContentRendered），从根本上避免“闪屏未隐藏、主窗口已显示”的重叠。

        // 阶段二：后台预加载数据。
        // ⚠️ 关键：Dashboard 的 _context/_vm 是在其自身 Loaded 事件（Task.Run 建库）中才创建的，
        // 此处抢先调用其 InitializeAsync 会因 _context 为 null 失败，并错误地把 _isInitialized 置 true，
        // 导致 Loaded 后不再加载、首页数据全空。故 Dashboard 必须交由自身 Loaded 初始化，这里跳过。
        // StatisticsView / CategoryTagManageView 的 _context 在构造函数即已创建，可安全预加载。
        try
        {
            foreach (var (key, _) in pages)
            {
                if (!_pageCache.TryGetValue(key, out var view)) continue;
                if (view is DashboardView) continue;   // 交给 Dashboard 自身 Loaded 初始化
                if (view is CategoryTagManageView catView)
                    await catView.InitializeAsync();
                else if (view is StatisticsView statsView)
                    await statsView.InitializeAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PreWarmViews 数据预加载失败");
        }
    }

    public void SetStatus(string text, bool isWorking = false)
    {
        StatusBarText.Text = text;
        StatusBarProgress.Visibility = isWorking ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ClearStatus()
    {
        StatusBarText.Text = LanguageManager.GetString("Status_Ready");
        StatusBarProgress.Visibility = Visibility.Collapsed;
    }

    /// <summary>显示文件夹监控检测到的新文件通知</summary>
    public void ShowFolderNotification(string title, string filePath)
    {
        SetStatus($"已导入: {title}", false);

        // 5 秒后恢复状态
        _ = Task.Delay(5000).ContinueWith(_ =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (StatusBarText.Text.Contains("已导入"))
                    ClearStatus();
            }));
        });
    }

    private readonly Dictionary<string, Button> _navButtons = new();

    private void RegisterNavButtons()
    {
        // 遍历 NavPanel 的逻辑树中所有带 Tag 的 Button（包括折叠的 Expander 子项）
        foreach (var child in FindLogicalButtons(NavPanel))
        {
            if (child.Tag is string tag)
                _navButtons[tag] = child;
            // 关闭 MDIX 涟漪动画，避免每次点击导航都触发额外渲染开销
            MaterialDesignThemes.Wpf.RippleAssist.SetIsDisabled(child, true);
        }
    }

    private static IEnumerable<Button> FindLogicalButtons(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is Button btn)
                yield return btn;
            if (child is DependencyObject depChild)
            {
                foreach (var grandchild in FindLogicalButtons(depChild))
                    yield return grandchild;
            }
        }
    }

    private void NavBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            // 点击一级导航项（非分组子项）时，折叠所有分组面板
            if (tag is "Dashboard" or "Movies" or "Settings")
                CollapseAllGroups();

            NavigateTo(tag);
        }
    }

    private void ToggleGroup(ToggleButton toggle, StackPanel panel, ToggleButton otherToggle, StackPanel otherPanel)
    {
        panel.Visibility = toggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (toggle.IsChecked == true)
        {
            otherToggle.IsChecked = false;
            otherPanel.Visibility = Visibility.Collapsed;
            if (FindPackIconInHeader(otherToggle)?.RenderTransform is RotateTransform rot)
                rot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
        }
    }

    private void GroupToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb) return;

        var packIcon = FindPackIconInHeader(tb);
        if (packIcon?.RenderTransform is RotateTransform rot)
        {
            var targetAngle = tb.IsChecked == true ? 90.0 : 0.0;
            var anim = new DoubleAnimation(targetAngle, TimeSpan.FromMilliseconds(150));
            rot.BeginAnimation(RotateTransform.AngleProperty, anim);
        }

        if (tb == AnalysisToggle)
            ToggleGroup(tb, AnalysisSubPanel, DiscoverToggle, DiscoverSubPanel);
        else if (tb == DiscoverToggle)
            ToggleGroup(tb, DiscoverSubPanel, AnalysisToggle, AnalysisSubPanel);
    }

    private PackIcon? FindPackIconInHeader(ToggleButton tb)
    {
        if (tb.Content is Grid grid)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(grid).OfType<PackIcon>())
                return child;
        }
        return null;
    }

    private void CollapseAllGroups()
    {
        AnalysisToggle.IsChecked = false;
        AnalysisSubPanel.Visibility = Visibility.Collapsed;
        DiscoverToggle.IsChecked = false;
        DiscoverSubPanel.Visibility = Visibility.Collapsed;

        // 动画旋转箭头回原位
        if (FindPackIconInHeader(AnalysisToggle)?.RenderTransform is RotateTransform rot1)
            rot1.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
        if (FindPackIconInHeader(DiscoverToggle)?.RenderTransform is RotateTransform rot2)
            rot2.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
    }

    private void HighlightNavButton(string page)
    {
        foreach (var (tag, btn) in _navButtons)
        {
            btn.FontWeight = tag == page ? FontWeights.Bold : FontWeights.Normal;
        }
    }

    private readonly Dictionary<string, UserControl> _pageCache = new();
    private string _currentPage = "";
    private static readonly Duration PageAnimDuration = new(TimeSpan.FromMilliseconds(150));

    public void NavigateTo(string page)
    {
        if (_currentPage == page && _pageCache.ContainsKey(page))
            return;

        if (!_pageCache.TryGetValue(page, out var view))
        {
            view = page switch
            {
                "Dashboard" => new DashboardView(),
                "Movies" => new MovieListView(this),
                "Statistics" => new StatisticsView(),
                "Calendar" => new WatchCalendarView(),
                "Relation" => new MovieRelationView(this),
                "News" => new MovieNewsView(),
                "AI" => new AIRecommendationView(),
                "Heatmap" => new WatchHeatmapView(),
                "Settings" => new SettingsView(),
                _ => new MovieListView(this)
            };
            _pageCache[page] = view;
            ContentArea.Children.Add(view);
        }

        // 找到当前可见的页面
        UIElement? oldView = null;
        foreach (UIElement child in ContentArea.Children)
        {
            if (child.Visibility == Visibility.Visible && child != view)
                oldView = child;
        }

        var newView = view;

        if (oldView == null)
        {
            // 首次加载：直接显示新页面（不带动画）
            newView.Opacity = 1;
            newView.Visibility = Visibility.Visible;
        }
        else
        {
            // 过渡动画：旧页面淡出 + 新页面淡入 同时进行（交叉淡入），
            // 避免原先“先淡出完成再淡入”串行造成的空白停顿与拖尾感。
            newView.Opacity = 0;
            newView.Visibility = Visibility.Visible;

            var fadeIn = new DoubleAnimation(0.0, 1.0, PageAnimDuration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            var fadeOut = new DoubleAnimation(1.0, 0.0, PageAnimDuration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            fadeOut.Completed += (s, e) => { oldView.Visibility = Visibility.Collapsed; };
            oldView.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            newView.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        }

        _currentPage = page;

        // 高亮当前导航按钮
        HighlightNavButton(page);

        // 非电影页面时隐藏电影详情面板
        MovieDetailPanel.Visibility = page == "Movies" && _lastSelectedMovie != null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private Movie? _lastSelectedMovie;

    public async void ShowMovieDetail(Movie? movie)
    {
        if (movie == null)
        {
            _lastSelectedMovie = null;
            MovieDetailPanel.Visibility = Visibility.Collapsed;
            return;
        }

        _lastSelectedMovie = movie;
        MovieDetailPanel.Visibility = Visibility.Visible;
        DetailTitle.Text = movie.Title;
        DetailOriginalTitle.Text = movie.OriginalTitle ?? "";
        var yearSuffix = LanguageManager.GetString("Msg_YearSuffix");
        var minSuffix = LanguageManager.GetString("Msg_MinuteSuffix");
        DetailYear.Text = movie.Year > 0 ? movie.Year + yearSuffix : "";
        DetailRuntime.Text = movie.Runtime.HasValue ? movie.Runtime + minSuffix : "";
        DetailRating.Text = movie.Rating.HasValue ? "⭐" + movie.Rating : "";
        DetailDirector.Text = string.IsNullOrEmpty(movie.Director) ? "" : "🎬 " + movie.Director;
        DetailCountry.Text = string.IsNullOrEmpty(movie.Country) ? "" : "🌍 " + movie.Country;
        DetailCast.Text = string.IsNullOrEmpty(movie.Cast) ? "" : "🎭 " + movie.Cast;
        DetailSynopsis.Text = movie.Synopsis ?? "";
        DetailStatus.Text = movie.WatchStatus switch
        {
            WatchStatus.WantToWatch => LanguageManager.GetString("WatchStatus_WantToWatch"),
            WatchStatus.Watched => LanguageManager.GetString("WatchStatus_Watched"),
            _ => ""
        };

        // 加载标签
        await LoadDetailTagsAsync(movie.Id);

        DetailPoster.Source = null;

        var posterLoaded = false;
        // 优先磁盘缓存（离线/去重层）；失败或缺失则回退下方 PosterData / PosterUrl
        var cachedSrc = EasyMovie.Client.Helpers.PosterCache.LoadImageSource(movie.Id);
        if (cachedSrc != null)
        {
            DetailPoster.Source = cachedSrc;
            posterLoaded = true;
        }

        if (movie.PosterData != null && movie.PosterData.Length > 0)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(movie.PosterData);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                DetailPoster.Source = bmp;
                posterLoaded = true;
            }
            catch (Exception ex) { Log.Error(ex, "MainWindow 操作异常"); }
        }

        if (!posterLoaded && !string.IsNullOrEmpty(movie.PosterUrl))
        {
            try
            {
                var bytes = await GetImageClient(movie.PosterUrl).GetByteArrayAsync(movie.PosterUrl);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                DetailPoster.Source = bmp;

                _ = SavePosterToDb(movie, bytes);
            }
            catch (Exception ex) { Log.Error(ex, "MainWindow 操作异常"); }
        }
        else if (!posterLoaded && !string.IsNullOrEmpty(movie.CoverImagePath) && File.Exists(movie.CoverImagePath))
        {
            try
            {
                var bmp = new BitmapImage(new Uri(movie.CoverImagePath));
                bmp.Freeze();
                DetailPoster.Source = bmp;
            }
            catch (Exception ex) { Log.Error(ex, "MainWindow 操作异常"); }
        }

        await LoadWatchLogsAsync(movie.Id);

        // 通知电影列表选中该电影
        Dispatcher.BeginInvoke(new Action(() =>
        {
            GetCurrentMovieView()?.SelectMovieById(movie.Id);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    public void ShowMoviePlayer(Movie movie)
    {
        // 在电影详情面板中展示基础信息，右侧 ContentArea 进入播放器
        _lastSelectedMovie = movie;
        // 非全屏播放时保留主界面状态栏：PlayerHost 已限定在内容行 Row1，不会覆盖它；
        // 视频底部控制栏位于播放区内部，与状态栏互不重叠。
        // 先让播放器可见并完成布局，再 LoadMovie（否则 EnsureOverlay 时 ActualWidth/Height 还是 0，
        // 覆盖窗口位置和尺寸会错，导致返回栏偏移或顶部露出灰条）。
        PlayerHost.Visibility = Visibility.Visible;
        PlayerHost.UpdateLayout();
        PlayerHost.LoadMovie(movie);
        PlayerHost.Focus();
    }

    private void PlayerHost_Closed(object? sender, EventArgs e)
    {
        PlayerHost.Visibility = Visibility.Collapsed;
        StatusBar.Visibility = Visibility.Visible;
        // 刷新电影列表以更新观影状态/进度
        _ = Dispatcher.BeginInvoke(new Action(async () =>
        {
            var view = GetCurrentMovieView();
            if (view != null) await view.RefreshCurrentPageAsync();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private async Task LoadDetailTagsAsync(int movieId)
    {
        DetailTags.Children.Clear();
        try
        {
            using var ctx = DbHelper.CreateContext();
            var tagIds = await ctx.Set<MovieTag>().Where(mt => mt.MovieId == movieId).Select(mt => mt.TagId).ToListAsync();
            if (tagIds.Count == 0) return;
            var tags = await ctx.Tags.Where(t => tagIds.Contains(t.Id)).ToListAsync();
            foreach (var tag in tags)
            {
                var border = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 2, 8, 2),
                    Margin = new Thickness(0, 0, 4, 2),
                    Background = TryCreateBrush(tag.Color)
                };
                var tb = new TextBlock
                {
                    Text = tag.Name,
                    FontSize = 11,
                    Foreground = Brushes.White
                };
                border.Child = tb;
                DetailTags.Children.Add(border);
            }
        }
        catch (Exception ex) { Log.Error(ex, "MainWindow 操作异常"); }
    }

    private static Brush TryCreateBrush(string? color)
    {
        if (!string.IsNullOrEmpty(color))
        {
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); }
            catch (Exception ex) { Log.Error(ex, "MainWindow 操作异常"); }
        }
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#5C6BC0"));
    }

    private async Task LoadWatchLogsAsync(int movieId)
    {
        WatchLogList.Children.Clear();
        try
        {
            using var ctx = DbHelper.CreateContext();
            var svc = new WatchLogService(ctx);
            var logs = await svc.GetByMovieIdAsync(movieId);

            if (logs.Count == 0)
            {
                WatchLogList.Children.Add(new TextBlock
                {
                    Text = LanguageManager.GetString("WatchLog_Empty"),
                    FontSize = 11,
                    Foreground = SafeFindBrush("MaterialDesignHintForeground", Color.FromRgb(117, 117, 117)),
                    Margin = new Thickness(0, 2, 0, 0)
                });
                return;
            }

            foreach (var log in logs)
            {
                var border = new Border
                {
                    Background = SafeFindBrush("MaterialDesignCardBackground", Color.FromRgb(45, 45, 45)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 6, 8, 6),
                    Margin = new Thickness(0, 0, 0, 4)
                };
                var stack = new StackPanel();

                var header = new Grid();
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var dateRating = new StackPanel { Orientation = Orientation.Horizontal };
                dateRating.Children.Add(new TextBlock { Text = log.WatchDate.ToString("yyyy-MM-dd"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = SafeFindBrush("MaterialDesignBody", Colors.White) });
                if (log.Rating.HasValue) dateRating.Children.Add(new TextBlock { Text = $"  ⭐{log.Rating}", FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(255, 215, 0)) });
                Grid.SetColumn(dateRating, 0);
                header.Children.Add(dateRating);

                var btnPanel = new StackPanel { Orientation = Orientation.Horizontal };
                var editBtn = new Button { Style = (Style)FindResource("MaterialDesignIconForegroundButton"), Width = 22, Height = 22, Tag = log.Id, ToolTip = LanguageManager.GetString("WatchLog_Edit") };
                editBtn.Content = new MaterialDesignThemes.Wpf.PackIcon { Kind = MaterialDesignThemes.Wpf.PackIconKind.Pencil, Width = 12, Height = 12 };
                editBtn.Click += EditWatchLog_Click;
                btnPanel.Children.Add(editBtn);

                var delBtn = new Button { Style = (Style)FindResource("MaterialDesignIconForegroundButton"), Width = 22, Height = 22, Tag = log.Id, ToolTip = LanguageManager.GetString("WatchLog_Delete"), Margin = new Thickness(2, 0, 0, 0) };
                delBtn.Content = new MaterialDesignThemes.Wpf.PackIcon { Kind = MaterialDesignThemes.Wpf.PackIconKind.Delete, Width = 12, Height = 12 };
                delBtn.Click += DeleteWatchLog_Click;
                btnPanel.Children.Add(delBtn);

                Grid.SetColumn(btnPanel, 1);
                header.Children.Add(btnPanel);

                stack.Children.Add(header);

                if (!string.IsNullOrEmpty(log.Location))
                    stack.Children.Add(new TextBlock { Text = "📍 " + log.Location, FontSize = 11, Foreground = SafeFindBrush("MaterialDesignBody", Colors.White), Margin = new Thickness(0, 2, 0, 0) });
                if (!string.IsNullOrEmpty(log.Companion))
                    stack.Children.Add(new TextBlock { Text = "👥 " + log.Companion, FontSize = 11, Foreground = SafeFindBrush("MaterialDesignBody", Colors.White), Margin = new Thickness(0, 2, 0, 0) });
                if (!string.IsNullOrEmpty(log.Notes))
                    stack.Children.Add(new TextBlock { Text = log.Notes, FontSize = 11, Foreground = SafeFindBrush("MaterialDesignBodyLight", Color.FromRgb(180, 180, 180)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });

                border.Child = stack;
                WatchLogList.Children.Add(border);
            }
        }
        catch (Exception ex) { Log.Error(ex, "MainWindow 操作异常"); }
    }

    private async void AddWatchLog_Click(object sender, RoutedEventArgs e)
    {
        if (_lastSelectedMovie == null) return;
        var dlg = new WatchLogDialog(_lastSelectedMovie.Id, _lastSelectedMovie.Title) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            using var ctx = DbHelper.CreateContext();
            var svc = new WatchLogService(ctx);
            await svc.AddAsync(new WatchLog
            {
                MovieId = dlg.MovieId,
                WatchDate = dlg.WatchDate,
                Rating = dlg.Rating,
                Location = dlg.LogLocation,
                Companion = dlg.LogCompanion,
                Notes = dlg.LogNotes
            });
            await LoadWatchLogsAsync(_lastSelectedMovie.Id);
        }
    }

    private async void EditWatchLog_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not int logId || _lastSelectedMovie == null) return;
        using var ctx = DbHelper.CreateContext();
        var svc = new WatchLogService(ctx);
        var log = await svc.GetByIdAsync(logId);
        if (log == null) return;
        var dlg = new WatchLogDialog(log.MovieId, _lastSelectedMovie.Title, log) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            log.WatchDate = dlg.WatchDate;
            log.Rating = dlg.Rating;
            log.Location = dlg.LogLocation;
            log.Companion = dlg.LogCompanion;
            log.Notes = dlg.LogNotes;
            await svc.UpdateAsync(log);
            await LoadWatchLogsAsync(_lastSelectedMovie.Id);
        }
    }

    private async void DeleteWatchLog_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not int logId || _lastSelectedMovie == null) return;
        if (!AppMessageBox.Confirm(LanguageManager.GetString("WatchLog_ConfirmDelete"), LanguageManager.GetString("Msg_Confirm"))) return;
        using var ctx = DbHelper.CreateContext();
        var svc = new WatchLogService(ctx);
        await svc.DeleteAsync(logId);
        await LoadWatchLogsAsync(_lastSelectedMovie.Id);
    }

    private static Brush SafeFindBrush(string resourceKey, Color fallback)
    {
        var brush = Application.Current.TryFindResource(resourceKey) as Brush;
        if (brush != null) return brush;
        var solid = new SolidColorBrush(fallback);
        solid.Freeze();
        return solid;
    }

    private MovieListView? GetCurrentMovieView()
    {
        if (_currentPage == "Movies" && _pageCache.TryGetValue("Movies", out var view) && view is MovieListView mlv)
            return mlv;
        return null;
    }

    private void LoadInputBindings()
    {
        InputBindings.Clear();
        var configs = ShortcutConfig.LoadAll();
        var commandMap = new Dictionary<string, RoutedCommand>
        {
            ["Search"] = SearchCommand,
            ["AddNew"] = AddNewCommand,
            ["Delete"] = DeleteCommand,
            ["Detail"] = DetailCommand,
            ["Escape"] = EscapeCommand,
            ["Refresh"] = RefreshCommand,
            ["SelectAll"] = SelectAllCommand,
            ["CycleView"] = CycleViewCommand,
            ["Nav1"] = Nav1Command,
            ["Nav2"] = Nav2Command,
            ["Nav3"] = Nav3Command,
            ["Nav4"] = Nav4Command,
            ["ShortcutsHelp"] = ShortcutsHelpCommand,
        };

        foreach (var cfg in configs)
        {
            if (!commandMap.TryGetValue(cfg.Action, out var cmd)) continue;
            var gesture = ShortcutConfig.ParseGesture(cfg.KeyGesture);
            if (gesture != null)
                InputBindings.Add(new KeyBinding(cmd, gesture));
        }
    }

    public void ApplyShortcuts()
    {
        LoadInputBindings();
    }

    private void Search_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (GetCurrentMovieView() is { } mv) mv.FocusSearchBox();
        else { NavigateTo("Movies"); Dispatcher.BeginInvoke(new Action(() => GetCurrentMovieView()?.FocusSearchBox()), System.Windows.Threading.DispatcherPriority.Background); }
    }

    private void AddNew_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (GetCurrentMovieView() is { } mv) mv.AddNewMovie();
    }

    private void Delete_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (GetCurrentMovieView() is { } mv) mv.DeleteSelectedMovie();
    }

    private void Detail_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (GetCurrentMovieView() is { } mv) mv.OpenSelectedMovieDetail();
    }

    private void Escape_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (_lastSelectedMovie != null) ShowMovieDetail(null);
        else if (GetCurrentMovieView() is { } mv) mv.DeselectAll();
    }

    private void Refresh_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (GetCurrentMovieView() is { } mv) mv.RefreshData();
    }

    private void SelectAll_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (GetCurrentMovieView() is { } mv) mv.SelectAllMovies();
    }

    private void Nav1_Executed(object sender, ExecutedRoutedEventArgs e) { NavigateTo("Dashboard"); }
    private void Nav2_Executed(object sender, ExecutedRoutedEventArgs e) { NavigateTo("Movies"); }
    private void Nav3_Executed(object sender, ExecutedRoutedEventArgs e) { NavigateTo("Statistics"); ExpandGroup("Statistics"); }
    private void Nav4_Executed(object sender, ExecutedRoutedEventArgs e) { NavigateTo("Settings"); }

    /// <summary>根据页面 tag 自动展开对应分组</summary>
    private void ExpandGroup(string page)
    {
        if (page is "Statistics" or "Heatmap" or "Calendar")
        {
            AnalysisToggle.IsChecked = true;
            AnalysisSubPanel.Visibility = Visibility.Visible;
            DiscoverToggle.IsChecked = false;
            DiscoverSubPanel.Visibility = Visibility.Collapsed;
        }
        else if (page is "Relation" or "News" or "AI")
        {
            DiscoverToggle.IsChecked = true;
            DiscoverSubPanel.Visibility = Visibility.Visible;
            AnalysisToggle.IsChecked = false;
            AnalysisSubPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void CycleView_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (GetCurrentMovieView() is { } mv) mv.CycleView();
    }

    private void ShortcutsHelp_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        var dlg = new Window
        {
            Title = LanguageManager.GetString("Shortcuts_Title"),
            Width = 420,
            Height = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = (Brush)FindResource("MaterialDesignPaper")
        };

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var panel = new StackPanel { Margin = new Thickness(20) };

        panel.Children.Add(new TextBlock
        {
            Text = "⌨️ " + LanguageManager.GetString("Shortcuts_Title"),
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 16),
            Foreground = (Brush)FindResource("MaterialDesignBody")
        });

        var shortcuts = new (string key, string desc)[]
        {
            ("Ctrl+F", LanguageManager.GetString("Shortcuts_Search")),
            ("Ctrl+N", LanguageManager.GetString("Shortcuts_AddNew")),
            ("Delete", LanguageManager.GetString("Shortcuts_Delete")),
            ("Enter", LanguageManager.GetString("Shortcuts_Detail")),
            ("Esc", LanguageManager.GetString("Shortcuts_Escape")),
            ("F5", LanguageManager.GetString("Shortcuts_Refresh")),
            ("Ctrl+A", LanguageManager.GetString("Shortcuts_SelectAll")),
            ("F3", LanguageManager.GetString("Shortcuts_CycleView")),
            ("Ctrl+1", LanguageManager.GetString("Shortcuts_Nav1")),
            ("Ctrl+2", LanguageManager.GetString("Shortcuts_Nav2")),
            ("Ctrl+3", LanguageManager.GetString("Shortcuts_Nav3")),
            ("Ctrl+4", LanguageManager.GetString("Shortcuts_Nav4")),
            ("Ctrl+/", LanguageManager.GetString("Shortcuts_Help")),
        };

        foreach (var (key, desc) in shortcuts)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var keyBorder = new Border
            {
                Background = (Brush)FindResource("MaterialDesignCardBackground"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 4, 8, 4),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            keyBorder.Child = new TextBlock
            {
                Text = key,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Consolas"),
                Foreground = (Brush)FindResource("MaterialDesignBody")
            };
            Grid.SetColumn(keyBorder, 0);
            row.Children.Add(keyBorder);

            var descText = new TextBlock
            {
                Text = desc,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                Foreground = (Brush)FindResource("MaterialDesignBody")
            };
            Grid.SetColumn(descText, 1);
            row.Children.Add(descText);

            panel.Children.Add(row);
        }

        var closeBtn = new Button
        {
            Content = LanguageManager.GetString("Msg_Cancel"),
            Style = (Style)FindResource("MaterialDesignRaisedButton"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        closeBtn.Click += (s, ev) => dlg.Close();
        panel.Children.Add(closeBtn);

        scroll.Content = panel;
        dlg.Content = scroll;
        dlg.ShowDialog();
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeWindow_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
        }
        else
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
        else
        {
            DragMove();
        }
    }
}
