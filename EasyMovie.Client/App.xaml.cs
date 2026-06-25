using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;
using EasyMovie.Core.Services;
using EasyMovie.Tools.ImportExport;
using EasyMovie.Tools.MovieApi;
using MaterialDesignColors;
using Serilog;

namespace EasyMovie.Client;

public partial class App : Application
{
    public static bool IsDarkTheme => AppSettings.IsDarkTheme;
    public static readonly FolderWatcherService FolderWatcher = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 初始化语言
        LanguageManager.Initialize();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Debug()
            .WriteTo.File("logs/EasyMovie-.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level}] {Message}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("EasyMovie 启动");

        try
        {
            using var context = DbHelper.CreateContext();
            Log.Information("数据库就绪: {Path}", DbHelper.ConnectionString);
        }
        catch (Exception ex) { Log.Error(ex, "数据库初始化失败"); }

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            Log.Fatal(ex, "未处理的异常");
            AppMessageBox.ShowError($"严重错误: {ex?.Message}", "错误");
        };

        DispatcherUnhandledException += (s, args) =>
        {
            Log.Error(args.Exception, "UI线程异常");
            AppMessageBox.ShowWarning(args.Exception.Message, "错误");
            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            Log.Error(args.Exception, "未观察到的任务异常");
            args.SetObserved();
        };

        ApplyTheme(IsDarkTheme);

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
                foreach (var p in AppSettings.DeletedFilePaths)
                    dbPaths.Add(p);

                return dbPaths;
            }
            catch { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
        };

        if (AppSettings.FolderMonitorEnabled && AppSettings.MonitoredFolders.Count > 0)
        {
            FolderWatcher.NewFileDetected += OnNewFileDetected;
            FolderWatcher.Start(AppSettings.MonitoredFolders);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        FolderWatcher.Stop();
        Log.Information("EasyMovie 退出");
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    public static void SetTheme(AppThemeMode mode)
    {
        AppSettings.Theme = mode;
        ApplyTheme(IsDarkTheme);
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
                if (AppSettings.DeletedFilePaths.Contains(filePath))
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
                            catch { }
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
}
