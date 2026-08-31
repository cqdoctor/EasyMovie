using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Threading;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;
using EasyMovie.Core.Services;
using EasyMovie.Tools.AIChat;
using EasyMovie.Tools.ImportExport;
using EasyMovie.Tools.MovieApi;
using EasyMovie.Client.ViewModels;
using EasyMovie.Client.Views;
using EasyMovie.Data;
using EasyMovie.Data.Repositories;
using MaterialDesignColors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace EasyMovie.Client;

public partial class App : Application
{
    public static bool IsDarkTheme => AppSettings.IsDarkTheme;
    public static FolderWatcherService FolderWatcher { get; private set; } = new();

    /// <summary>DI 容器根（由 OnStartup 构建）。供 View/Service 增量迁移时使用。</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>单实例互斥体句柄。持有期间本进程为唯一运行实例；退出时释放。
    /// 用于防止多个 exe 副本同时运行并争抢同一 AppData（DB / settings.json / 标志文件），
    /// 该争抢会造成 "Access to the path '...settings.json' is denied" 与严重卡顿（已在日志中实证）。</summary>
    private static Mutex? _singleInstanceMutex;

    /// <summary>手动控制的启动闪屏实例。改用 <see cref="ShowSplash"/>/<see cref="CloseSplash"/> 后，
    /// 闪屏由我们精确控制关闭时机：在所有导航页构造完成、主窗口已就绪后平滑淡出，避免“闪屏未隐藏、主窗口已显示”的重叠衔接问题。</summary>
    private static SplashScreen? _splashScreen;

    /// <summary>显示启动闪屏（不自动关闭）。应尽早调用，覆盖 CLR 加载、DI/主题/窗口创建与页面预热阶段。</summary>
    public static void ShowSplash()
    {
        try
        {
            _splashScreen = new SplashScreen("app.png");
            _splashScreen.Show(false);
            LogStartup("启动闪屏已显示(手动控制)");
        }
        catch (Exception ex)
        {
            LogStartup("启动闪屏显示失败: " + ex.Message);
            _splashScreen = null;
        }
        // 兜底：极端情况下若预热流程异常未关闭闪屏，8 秒后强制关闭，绝不卡死在启动画面。
        _ = Task.Delay(8000).ContinueWith(_ => CloseSplash());
    }

    /// <summary>平滑关闭启动闪屏（幂等，可重复调用）。内部自动封送到 UI 线程，因此可从任意线程（含兜底超时的线程池线程）安全调用。</summary>
    public static void CloseSplash()
    {
        try
        {
            if (_splashScreen == null) return;
            var splash = _splashScreen;
            var dispatcher = Application.Current?.Dispatcher;
            // 注意：必须用 TimeSpan.Zero（同步立即关闭），不能用淡出时间——淡出依赖 DispatcherTimer，
            // 会被 PreWarmViews 等更高优先级任务饿死（实测：闪屏关闭逻辑执行了但窗口一直挂着）。
            if (dispatcher?.CheckAccess() == true)
                splash.Close(TimeSpan.Zero);
            else
                dispatcher?.BeginInvoke(new Action(() => splash.Close(TimeSpan.Zero)));
        }
        catch (Exception ex)
        {
            // 闪屏关不掉会挡住主窗口，属启动可视性故障，必须留痕
            LogStartup("启动闪屏关闭失败: " + ex.Message);
        }
        _splashScreen = null;
        LogStartup("启动闪屏已关闭");
    }

    /// <summary>启动期里程碑日志：写入 exe 同级 logs/startup.log，带毫秒时间戳，用于跨会话定位卡顿阶段。</summary>
    public static void LogStartup(string msg)
    {
        try
        {
            var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "startup.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}\n");
        }
        catch { /* 故意留空：这里再调用日志会无限递归（logs 不可写时唯一可靠的处置就是放弃） */ }
    }

    /// <summary>模块初始化器：CLR 加载本程序集后、任何类型静态构造之前立即执行，用于探测"OnStartup 之前"的极早期耗时。</summary>
    [ModuleInitializer]
    public static void ModuleInit() => LogStartup($"CLR模块已加载(极早期, PID={Environment.ProcessId})");

    protected override void OnStartup(StartupEventArgs e)
    {
        // ===== 单实例保护 =====
        // 多个 exe 副本会共享同一 AppData（DB / settings.json / 标志文件），互相争抢会导致
        // "Access to the path '...settings.json' is denied" 与严重卡顿（已在日志中实证）。
        // 使用全局命名互斥体：若已有实例持有，则当前实例直接退出，避免重复争抢。
        _singleInstanceMutex = new Mutex(true, @"Global\EasyMovie_SingleInstance", out var createdNew);
        if (!createdNew)
        {
            LogStartup($"已有实例运行，本实例退出 (PID={Environment.ProcessId})");
            // 即将 Shutdown，Dispose 只是尽力清理；失败不影响本实例退出
            try { _singleInstanceMutex.Dispose(); _singleInstanceMutex = null; } catch { }
            Shutdown();
            return;
        }

        // 启动里程碑：写到 logs/startup.log，便于跨会话验证窗口是否真正建出（不依赖 Serilog 初始化时机）
        LogStartup($"OnStartup 开始 (PID={Environment.ProcessId})");

        // 启动期并发后台任务多（DB 预热、海报迁移、首页预载、备份、文件夹监控等），
        // .NET 线程池默认懒建线程（约 500ms/个），会把并发的 Task.Run 排队饿死 → 提前扩容最小线程数。
        // 最小线程数设置失败 = 后续并发 Task.Run 会被懒建线程拖慢，直接影响启动体感，需留痕
        try { ThreadPool.SetMinThreads(16, 16); }
        catch (Exception ex) { LogStartup($"线程池最小线程数设置失败: {ex.Message}"); }

        // 后台预热数据库（首次 EnsureCreated 约数十秒重活）。尽早启动，让首次迁移在后台线程跑，
        // 避免被后续 UI 线程创建 DbContext 抢先触发、阻塞首帧渲染导致启动闪屏长时间不消失。
        _ = DbHelper.WarmupAsync();

        base.OnStartup(e);
        LogStartup("base.OnStartup 完成(主窗口即将创建)");

        // 手动显示启动闪屏：覆盖 DI/主题/窗口创建与页面预热阶段，关闭时机由 MainWindow 预热完成后调用 CloseSplash 精确控制。
        ShowSplash();

        // 构建 DI 容器；失败不影响启动，FolderWatcher 回退为默认实例
        try
        {
            Services = ConfigureServices();
            FolderWatcher = Services.GetRequiredService<FolderWatcherService>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DI 容器初始化失败，使用默认 FolderWatcher");
        }
        LogStartup("DI容器就绪");

        // 初始化语言
        LanguageManager.Initialize();
        LogStartup("语言初始化完成");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Debug()
            .WriteTo.File("logs/EasyMovie-.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level}] {Message}{NewLine}{Exception}")
            .CreateLogger();
        LogStartup("Serilog日志器就绪");

        Log.Information("EasyMovie 启动");

        // 数据库初始化（schema 迁移 / 数据清洗 / 种子标签）较重，已在 OnStartup 早期
        // 通过 DbHelper.WarmupAsync() 派发到后台线程（见上方），避免阻塞首帧渲染。
        // 此处复用同一预热任务并打印 DB 路径。
        _ = DbHelper.WarmupAsync();
        LogStartup($"数据库预热已派发后台线程: {DbHelper.ConnectionString}");

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            DumpCrash("AppDomain.UnhandledException", ex);
            Log.Fatal(ex, "未处理的异常");
            // 异常本体已由 DumpCrash 落盘 crash.log；弹窗只是给用户看的提示，失败不必再记
            try { AppMessageBox.ShowError($"严重错误: {ex?.Message}", "错误"); } catch { }
        };

        DispatcherUnhandledException += (s, args) =>
        {
            DumpCrash("DispatcherUnhandledException", args.Exception);
            Log.Error(args.Exception, "UI线程异常");
            // 同上：异常已进日志，弹窗失败无需再记
            try { AppMessageBox.ShowWarning(args.Exception.Message, "错误"); } catch { }
            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            Log.Error(args.Exception, "未观察到的任务异常");
            args.SetObserved();
        };

        // 系统深浅色实时跟随：注入 Windows 系统主题检测，并在 System 模式下监听系统主题变化
        AppSettings.SetSystemThemeDetector(DetectSystemDark);
        if (AppSettings.Theme == AppThemeMode.System)
            RegisterSystemThemeWatcher();

        ApplyTheme(IsDarkTheme);
        LogStartup("主题/皮肤就绪");

        // 启动文件夹监控
        FolderWatcher.GetExistingPaths = () =>
        {
            try
            {
                using var ctx = DbHelper.CreateContext();
                var dbPaths = ctx.Movies
                    .Where(m => m.FilePath != null && m.FilePath != "")
                    .Select(m => m.FilePath!)
                    .AsEnumerable()
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                // 也加入已删除的文件路径，防止重新导入
                foreach (var p in AppSettings.GetDeletedFilePathsSnapshot())
                    dbPaths.Add(p);

                return dbPaths;
            }
            catch (Exception ex) { Log.Error(ex, "获取已入库路径失败"); return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
        };

        if (AppSettings.FolderMonitorEnabled && AppSettings.MonitoredFolders.Count > 0)
        {
            FolderWatcher.NewFileDetected += OnNewFileDetected;
            FolderWatcher.Start(AppSettings.MonitoredFolders);
        }
        // 后台把历史海报写盘（磁盘缓存层），不阻塞启动；任何失败均被 PosterCache 内部吞掉
        _ = System.Threading.Tasks.Task.Run(EasyMovie.Client.Helpers.PosterCache.MigrateFromDb);

        // 启动空闲期预载首页数据与海报：在闪屏/空闲阶段就把首页要显示的内容准备好，
        // 用户进入首页即秒显，避免“进首页后再等几秒”（详见 DashboardView.EnsureDataLoadedAsync）
        _ = DashboardView.EnsureDataLoadedAsync();

        // 若启用“定时同步在线信息”，启动周期计时器（首轮在间隔后触发，不阻塞启动）
        StartMetadataAutoSync();

        LogStartup("OnStartup 结束");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        FolderWatcher.Stop();
        _metadataSyncTimer?.Stop();
        _metadataSyncTimer?.Dispose();
        _metadataSyncTimer = null;
        // 释放单实例互斥体，允许下次启动
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
        }
        // 进程即将退出，未释放的具名互斥体会由 OS 回收；此处失败不影响退出
        catch { }
        Log.Information("EasyMovie 退出");
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // 每次解析得到独立的 DbContext 实例，天然隔离并发写（配合 MovieDbContext 内的写串行化锁）
        services.AddTransient(sp => new MovieDbContext(DbHelper.CreateOptions()));

        services.AddTransient<IMovieRepository, MovieRepository>();
        services.AddTransient<ICategoryRepository, CategoryRepository>();
        services.AddTransient<ITagRepository, TagRepository>();
        services.AddTransient<IMovieService, MovieService>();
        services.AddTransient<ICategoryService, CategoryService>();
        services.AddTransient<ITagService, TagService>();
        services.AddTransient<IStatisticsService, StatisticsService>();
        services.AddTransient<IImportExportService, ImportExportService>();
        services.AddTransient<CollectionService>();
        services.AddTransient<WatchLogService>();
        // FolderImportService 的 IMovieApiClient 参数有多个实现，按现状以 null 注入
        services.AddTransient<IFolderImportService>(sp => new FolderImportService());
        services.AddSingleton<FolderWatcherService>();
        services.AddTransient<CategoryManageViewModel>();
        services.AddTransient<TagManageViewModel>();
        services.AddTransient<CategoryTagManageViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<StatisticsViewModel>();
        services.AddTransient<OnlineSearchViewModel>();
        services.AddTransient<WatchCalendarViewModel>();
        services.AddTransient<ImportExportViewModel>();
        services.AddTransient<MovieRelationViewModel>();
        // 复杂 View 的 ViewModel（持有服务/上下文，由 DI 解析，视图内兜底手工 new）
        services.AddTransient<WatchDiaryViewModel>();
        services.AddTransient<WatchHeatmapViewModel>();
        services.AddTransient<AIChatService>();
        services.AddTransient<AIRecommendationViewModel>();
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<MovieNewsService>();
        services.AddTransient<MovieNewsViewModel>();

        return services.BuildServiceProvider();
    }

    public static void SetTheme(AppThemeMode mode)
    {
        AppSettings.Theme = mode;
        ApplyTheme(IsDarkTheme);
        if (mode == AppThemeMode.System)
            RegisterSystemThemeWatcher();
        else
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        Log.Information("主题切换: {Theme} (实际: {Actual})", mode, IsDarkTheme ? "Dark" : "Light");
    }

    private static void ApplyTheme(bool dark)
    {
        // 1. 切换 MaterialDesign 主题
        if (Current.Resources.MergedDictionaries.Count > 0 &&
            Current.Resources.MergedDictionaries[0] is MaterialDesignThemes.Wpf.BundledTheme theme)
        {
            theme.BaseTheme = dark
                ? MaterialDesignThemes.Wpf.BaseTheme.Dark
                : MaterialDesignThemes.Wpf.BaseTheme.Light;
        }

        // 2. 切换皮肤颜色刷子
        var skinName = AppSettings.SkinName;
        if (string.IsNullOrEmpty(skinName))
            skinName = dark ? "Dark" : "Light";

        LoadSkin(skinName);
    }

    /// <summary>读取 Windows 系统“应用”深浅色（注册表 AppsUseLightTheme，0=深色 1=浅色）。非 Windows 或读取失败回退夜间时间判定。</summary>
    private static bool DetectSystemDark()
    {
        if (!OperatingSystem.IsWindows())
            return AppSettings.IsNightTime();
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v)
                return v == 0; // 0 表示系统使用深色
        }
        catch (Exception ex)
        {
            // 读不到注册表会静默回退到"夜间时间判定"，表现为系统主题跟随失灵——留痕好查
            Log.Warning(ex, "读取系统深浅色注册表失败，回退夜间时间判定");
        }
        return AppSettings.IsNightTime();
    }

    /// <summary>订阅 Windows 系统主题变化并实时切换界面主题（幂等，可重复调用）。</summary>
    private static void RegisterSystemThemeWatcher()
    {
        _lastSystemDark = AppSettings.IsDarkTheme;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
    }

    // 记录上次应用的系统深浅色，避免系统其它偏好变化（鼠标/键盘/语言等）触发事件时反复切换主题
    private static bool _lastSystemDark = AppSettings.IsDarkTheme;

    private static void OnSystemPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (AppSettings.Theme != AppThemeMode.System) return;
        try
        {
            var nowDark = AppSettings.IsDarkTheme;
            if (nowDark == _lastSystemDark) return; // 系统深浅色未变，跳过
            _lastSystemDark = nowDark;
            Current.Dispatcher.Invoke(() => ApplyTheme(nowDark));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "系统主题变化后应用主题失败");
        }
    }

    /// <summary>加载指定皮肤，写入刷子 + 切换主色调</summary>
    public static void LoadSkin(string skinName)
    {
        try
        {
            var skinUri = new Uri($"Themes/Skin.{skinName}.xaml", UriKind.Relative);
            var skinDict = new ResourceDictionary { Source = skinUri };

            foreach (var key in skinDict.Keys)
            {
                Current.Resources[key] = skinDict[key];
            }

            // 切换 MaterialDesign 主色调
            if (Current.Resources.MergedDictionaries.Count > 0 &&
                Current.Resources.MergedDictionaries[0] is MaterialDesignThemes.Wpf.BundledTheme theme)
            {
                var (acc, sec) = skinName switch
                {
                    "Ocean" => (PrimaryColor.LightBlue, SecondaryColor.Cyan),
                    "Forest" => (PrimaryColor.Green, SecondaryColor.LightGreen),
                    "Sunset" => (PrimaryColor.Orange, SecondaryColor.Amber),
                    _ => (PrimaryColor.DeepPurple, SecondaryColor.Purple),
                };
                theme.PrimaryColor = acc;
                theme.SecondaryColor = sec;
            }

            Log.Information("皮肤切换: {Skin}", skinName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "皮肤加载失败: {Skin}", skinName);
        }
    }

    /// <summary>检测到新视频文件，自动导入到电影库</summary>
    private static void OnNewFileDetected(string filePath)
    {
        var fileName = System.IO.Path.GetFileName(filePath);
        Log.Information("[FolderWatcher] 检测到新文件: {File}", fileName);

        _ = Task.Run(async () =>
        {
            try
            {
                // 检查是否已被用户删除（防止重新导入）
                if (AppSettings.IsFileDeleted(filePath))
                {
                    Log.Information("[FolderWatcher] 文件已被用户删除，跳过: {File}", fileName);
                    return;
                }

                // 双重检查：确保文件未被导入
                using var ctx = DbHelper.CreateContext();
                var existing = ctx.Movies
                    .Where(m => m.FilePath == filePath)
                    .Select(m => m.Id)
                    .FirstOrDefault();
                if (existing > 0)
                {
                    Log.Information("[FolderWatcher] 文件已存在数据库中，跳过: {File}", fileName);
                    return;
                }

                var importService = new FolderImportService();
                var (title, year) = importService.ParseFileName(filePath);

                var movie = new Movie
                {
                    Title = title,
                    Year = year ?? 0,
                    FilePath = filePath,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                // 尝试从豆瓣获取元数据
                try
                {
                    var douban = new DoubanApiClient();
                    var searchResponse = await douban.SearchAsync(
                        new MovieSearchRequest { Keyword = title, Page = 1, PageSize = 1 });

                    if (searchResponse.Results.Count > 0)
                    {
                        var apiResult = searchResponse.Results[0];
                        if (year == null || apiResult.Year == 0 ||
                            Math.Abs(apiResult.Year - (year ?? 0)) <= 1)
                        {
                            movie.Title = apiResult.Title;
                            movie.OriginalTitle = apiResult.OriginalTitle;
                            movie.Year = apiResult.Year > 0 ? apiResult.Year : (year ?? 0);
                            movie.Director = apiResult.Director;
                            movie.Cast = apiResult.Cast;
                            movie.Country = apiResult.Country;
                            movie.Synopsis = apiResult.Synopsis;
                            movie.PosterUrl = apiResult.PosterUrl;
                            movie.Runtime = apiResult.Runtime;
                            movie.DoubanId = apiResult.ExternalId;

                            // 获取详情
                            try
                            {
                                var detail = await douban.GetDetailAsync(apiResult.ExternalId ?? "");
                                if (detail != null)
                                {
                                    movie.Synopsis ??= detail.Synopsis;
                                    movie.Runtime ??= detail.Runtime;
                                    movie.Director ??= detail.Director;
                                    movie.Cast ??= detail.Cast;
                                    movie.Country ??= detail.Country;
                                }
                            }
                            catch (Exception ex)
                            {
                                Log.Error(ex, "处理新文件自动入库失败");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[FolderWatcher] 获取元数据失败: {File}", fileName);
                }

                ctx.Movies.Add(movie);
                await ctx.SaveChangesAsync();

                Log.Information("[FolderWatcher] 已导入: {Title} ({Year})", movie.Title, movie.Year);

                Current.Dispatcher.BeginInvoke(() =>
                {
                    if (Current.MainWindow is MainWindow mw)
                        mw.ShowFolderNotification(movie.Title, filePath);
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[FolderWatcher] 导入文件失败: {File}", fileName);
            }
        });
    }

    /// <summary>重启文件夹监控（设置变更后调用）</summary>
    public static void RestartFolderWatcher()
    {
        FolderWatcher.Stop();
        // 确保事件只订阅一次
        FolderWatcher.NewFileDetected -= OnNewFileDetected;
        FolderWatcher.NewFileDetected += OnNewFileDetected;
        if (AppSettings.FolderMonitorEnabled && AppSettings.MonitoredFolders.Count > 0)
        {
            FolderWatcher.Start(AppSettings.MonitoredFolders);
        }
    }

    // ── 定时同步在线信息 ──
    private static System.Timers.Timer? _metadataSyncTimer;
    private static int _metadataSyncRunning = 0;

    /// <summary>若启用自动同步，启动周期计时器（首轮在间隔之后触发，不阻塞启动）。</summary>
    private static void StartMetadataAutoSync()
    {
        if (!AppSettings.MetadataAutoSyncEnabled) return;
        var hours = Math.Max(1, AppSettings.MetadataAutoSyncIntervalHours);
        _metadataSyncTimer = new System.Timers.Timer(hours * 3600.0 * 1000.0) { AutoReset = true };
        _metadataSyncTimer.Elapsed += (_, _) => _ = RunMetadataSyncNow();
        _metadataSyncTimer.Start();
        Log.Information("已启用定时同步在线信息，间隔 {Hours} 小时", hours);
    }

    /// <summary>立即同步所有已绑定外部 ID 的电影（手动按钮 / 定时触发共用）。重叠调用会被忽略。</summary>
    public static async Task RunMetadataSyncNow(IProgress<string>? progress = null)
    {
        if (Interlocked.Exchange(ref _metadataSyncRunning, 1) == 1) return; // 已在运行
        try
        {
            await EasyMovie.Client.Services.MetadataSyncService.SyncAllAsync(progress);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "同步在线信息失败");
        }
        finally
        {
            Interlocked.Exchange(ref _metadataSyncRunning, 0);
        }
    }

    /// <summary>设置变更后重新应用自动同步（停止旧计时器并按当前设置重启或关闭）。</summary>
    public static void ApplyMetadataAutoSyncSetting()
    {
        _metadataSyncTimer?.Stop();
        _metadataSyncTimer?.Dispose();
        _metadataSyncTimer = null;
        StartMetadataAutoSync();
    }

    private static readonly object _crashLock = new();
    /// <summary>把任何未处理异常（含原生崩溃尽可能）同步落盘到 logs/crash.log，便于无界面环境定位闪退根因</summary>
    private static void DumpCrash(string source, Exception? ex)
    {
        try
        {
            var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {source}");
            sb.AppendLine($"ExceptionType: {ex?.GetType().FullName}");
            sb.AppendLine($"Message: {ex?.Message}");
            sb.AppendLine("StackTrace:");
            sb.AppendLine(ex?.StackTrace);
            var inner = ex?.InnerException;
            while (inner != null)
            {
                sb.AppendLine($"--- Inner: {inner.GetType().FullName}: {inner.Message}");
                sb.AppendLine(inner.StackTrace);
                inner = inner.InnerException;
            }
            sb.AppendLine(new string('=', 60));
            lock (_crashLock)
            {
                File.AppendAllText(Path.Combine(dir, "crash.log"), sb.ToString());
            }
        }
        catch { /* 尽力而为 */ }
    }
}
