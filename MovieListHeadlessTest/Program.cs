using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using EasyMovie.Client.Converters;
using EasyMovie.Client.Views;
using MaterialDesignColors;
using MaterialDesignThemes.Wpf;

namespace MovieListHeadlessTest;

/// <summary>
/// MovieListView 的 headless 冒烟测试宿主。
///
/// 背景：B2 重构把 MovieListView 的分页状态与视图模式抽到了 MovieFilterState / ViewMode，
/// 但 Client 层零测试覆盖，且「电影库」页面要点进去才加载——启动日志覆盖不到，
/// 此前只能靠人工双击验证（不稳定、不可重复）。
///
/// 本宿主在 STA 线程上真实实例化 MovieListView 并驱动其加载/翻页/切换视图路径，
/// 把异常显式收集出来，使重构可以自动化回归。
/// 依据：MovieListView 构造函数不依赖 DI（App.Services ?? DbHelper.CreateContext() 有手工回退），
/// 因此无需搭建完整 DI 容器即可 new。
/// </summary>
internal static class Program
{
    private static readonly List<string> Failures = new();
    private static readonly List<string> AsyncErrors = new();

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (s, e) =>
            {
                AsyncErrors.Add($"{e.Exception.GetType().Name}: {e.Exception.Message}");
                e.Handled = true; // 兜住 async void 异常，避免进程静默崩溃
            };
            Step("创建 WPF Application", () => { _ = Application.Current; });
            Step("装配 App.xaml 的资源字典(MDIX/皮肤/字符串/转换器)", () => InitResources(app));

            MovieListView? view = null;
            Step("实例化 MovieListView(null)", () => { view = new MovieListView(null); });
            if (view == null) return Finish("view 为空，终止");

            // 反射取私有 _filterState，便于断言分页/视图模式
            var fsField = typeof(MovieListView).GetField("_filterState", BindingFlags.NonPublic | BindingFlags.Instance);
            if (fsField == null) return Finish("找不到 _filterState 字段（重构后命名变了？）");
            object? State() => fsField.GetValue(view);
            T Read<T>(string prop) => (T)State()!.GetType().GetProperty(prop)!.GetValue(State())!;

            // ── 加载列表（这是我重构覆盖的主路径）──
            var mi = typeof(MovieListView).GetMethod("LoadMoviesAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            if (mi == null) return Finish("找不到 LoadMoviesAsync（重命名了？）");
            Step("调用 LoadMoviesAsync()", () =>
            {
                var t = (Task?)mi.Invoke(view, null);
                t?.GetAwaiter().GetResult();
            });
            Pump(3000);

            var total = Read<int>("TotalCount");
            var page = Read<int>("CurrentPage");
            var vm0 = Read<object>("ViewMode");
            Report($"加载后状态：TotalCount={total}, CurrentPage={page}, ViewMode={vm0}");
            if (total <= 0) Failures.Add($"TotalCount={total}（预期 >0，说明查询没取到数据）");
            if (page != 1) Failures.Add($"首次加载 CurrentPage={page}（预期 1）");

            // ── 翻页（B2 切片1 覆盖）──
            Step("翻页 NextPage", () =>
            {
                var st = State()!;
                var t = st.GetType();
                if ((bool)t.GetProperty("CanGoNext")!.GetValue(st)!)
                    t.GetMethod("GoToNext")!.Invoke(st, null);
            });
            var pageAfter = Read<int>("CurrentPage");
            Report($"GoToNext 后 CurrentPage={pageAfter}");
            if (total <= 20 && pageAfter != 1) Failures.Add("总数<=20 时不该翻页");

            // ── 视图切换（B2 切片2 覆盖）：CycleView 的同步部分会先置 ViewMode ──
            Step("CycleView() 切换视图", () => view.CycleView());
            Pump(4000);
            var vm1 = Read<object>("ViewMode");
            Report($"CycleView 后 ViewMode={vm1}（原 {vm0}）");
            if (Equals(vm0, vm1)) Failures.Add($"CycleView 后 ViewMode 未变化（仍为 {vm1}）");

            return Finish(null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n[FATAL] {ex}");
            return 2;
        }
    }

    /// <summary>复刻 App.xaml 的 Application.Resources——否则 MovieListView 的 XAML 解析时找不到 StaticResource。</summary>
    private static void InitResources(Application app)
    {
        app.Resources.MergedDictionaries.Add(new BundledTheme
        {
            BaseTheme = BaseTheme.Dark,
            PrimaryColor = PrimaryColor.DeepPurple,
            SecondaryColor = SecondaryColor.Lime,
        });
        Merge(app, "pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesign3.Defaults.xaml");
        Merge(app, "pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesignTheme.ObsoleteStyles.xaml");
        Merge(app, "pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesignTheme.ObsoleteBrushes.xaml");
        Merge(app, "pack://application:,,,/EasyMovie.Client;component/Strings/Strings.zh-CN.xaml");
        Merge(app, "pack://application:,,,/EasyMovie.Client;component/Controls/RangeSliderStyle.xaml");
        Merge(app, "pack://application:,,,/EasyMovie.Client;component/Themes/SkinBase.xaml");

        app.Resources["NullToVisibilityConverter"] = new NullToVisibilityConverter();
        app.Resources["NullToCollapsedConverter"] = new NullToCollapsedConverter();
        app.Resources["WatchStatusConverter"] = new WatchStatusConverter();
        app.Resources["WatchStatusColorConverter"] = new WatchStatusColorConverter();
        app.Resources["WatchStatusBadgeVisibilityConverter"] = new WatchStatusBadgeVisibilityConverter();
        app.Resources["BoolToStarConverter"] = new BoolToStarConverter();
        app.Resources["BoolToStarColorConverter"] = new BoolToStarColorConverter();
        app.Resources["FilePathIconConverter"] = new FilePathIconConverter();
        app.Resources["PosterImageConverter"] = new PosterImageConverter();
        app.Resources["StringToBrushConverter"] = new StringToBrushConverter();
        app.Resources["BoolToVisibilityConverter"] = new BoolToVisibilityConverter();
        app.Resources["InverseBoolConverter"] = new InverseBoolConverter();
        app.Resources["PlayButtonToolTipConverter"] = new PlayButtonToolTipConverter();
    }

    private static void Merge(Application app, string packUri)
        => app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(packUri, UriKind.Absolute) });

    private static void Step(string name, Action action)
    {
        Console.Write($"[..] {name} ... ");
        try
        {
            action();
            Console.WriteLine("OK");
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL");
            Console.WriteLine($"     {ex.GetType().Name}: {ex.Message}");
            var inner = ex.InnerException;
            while (inner != null) { Console.WriteLine($"     inner: {inner.GetType().Name}: {inner.Message}"); inner = inner.InnerException; }
            Failures.Add(name);
        }
    }

    private static void Report(string msg) => Console.WriteLine($"     -> {msg}");

    /// <summary>泵送 dispatcher，让 async void 的续体和绑定有机会执行（否则异常抓不到）。</summary>
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(ms), DispatcherPriority.Background,
            (s, e) => frame.Continue = false, Application.Current.Dispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    private static int Finish(string? abortReason)
    {
        Console.WriteLine("\n================ 结果 ================");
        if (abortReason != null) Console.WriteLine($"中止：{abortReason}");
        if (AsyncErrors.Count > 0)
        {
            Console.WriteLine($"async 异常 {AsyncErrors.Count} 条：");
            foreach (var e in AsyncErrors) Console.WriteLine($"  - {e}");
        }
        if (Failures.Count == 0 && AsyncErrors.Count == 0 && abortReason == null)
        {
            Console.WriteLine("全部通过：MovieListView 可构造、可加载、可翻页、可切换视图。");
            return 0;
        }
        Console.WriteLine($"失败 {Failures.Count + AsyncErrors.Count} 项：");
        foreach (var f in Failures) Console.WriteLine($"  - {f}");
        return 1;
    }
}
