using System;
using System.Windows;
using System.Windows.Threading;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using Serilog;
using SkiaSharp;

namespace EasyMovie.Client;

public partial class App : Application
{
    public static bool IsDarkTheme => AppSettings.IsDarkTheme;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 注册 LiveCharts SkiaSharp 渲染器 + 中文字体
        LiveCharts.Configure(config => config
            .AddSkiaSharp()
            .AddDefaultMappers()
            .AddDefaultTheme()
            .HasTextSettings(new LiveChartsCore.SkiaSharpView.TextSettings
            {
                DefaultTypeface = SKFontManager.Default.MatchCharacter('汉')
            }));

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
            MessageBox.Show($"严重错误: {ex?.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        DispatcherUnhandledException += (s, args) =>
        {
            Log.Error(args.Exception, "UI线程异常");
            MessageBox.Show(args.Exception.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            Log.Error(args.Exception, "未观察到的任务异常");
            args.SetObserved();
        };

        ApplyTheme(IsDarkTheme);
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
        if (Current.Resources.MergedDictionaries.Count > 0 &&
            Current.Resources.MergedDictionaries[0] is MaterialDesignThemes.Wpf.BundledTheme theme)
        {
            theme.BaseTheme = dark
                ? MaterialDesignThemes.Wpf.BaseTheme.Dark
                : MaterialDesignThemes.Wpf.BaseTheme.Light;
        }
    }
}
