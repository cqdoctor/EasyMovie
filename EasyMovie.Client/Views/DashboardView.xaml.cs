using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using EasyMovie.Client.Services;
using EasyMovie.Client.ViewModels;
using EasyMovie.Core.Models;
using EasyMovie.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using EasyMovie.Client.Helpers;

namespace EasyMovie.Client.Views;

public partial class DashboardView : UserControl
{
    private DashboardViewModel? _vm;
    private MovieDbContext? _context;
    private bool _isInitialized;
    // 启动期空闲预载：把首页数据与海报在后台准备好，进入首页即秒显（详见 EnsureDataLoadedAsync）。
    private static readonly object _preloadLock = new();
    private static Task<DashboardSnapshot>? _preloadTask;

    /// <summary>首页数据的不可变快照，由后台预载生成、UI 线程绑定。</summary>
    internal sealed record DashboardSnapshot(
        List<Movie> RecentAdded,
        List<Movie> RecentWatched,
        List<Movie> ContinueWatching,
        int TotalMovies,
        int MonthWatched,
        double TotalMinutes,
        double? AvgRating,
        int Favorites,
        IList<GenreDatum> Genres,
        int Uncategorized,
        List<ReminderService.UpcomingReminder>? Reminders);

    private static readonly Color[] BarColors = new[]
    {
        Color.FromRgb(124, 77, 255),   // purple
        Color.FromRgb(76, 175, 80),    // green
        Color.FromRgb(255, 152, 0),    // orange
        Color.FromRgb(233, 30, 99),    // pink
        Color.FromRgb(0, 188, 212),    // cyan
        Color.FromRgb(156, 39, 176),   // deep purple
        Color.FromRgb(255, 193, 7),    // amber
        Color.FromRgb(63, 81, 181),    // indigo
        Color.FromRgb(244, 67, 54),    // red
        Color.FromRgb(0, 150, 136),    // teal
    };

    public DashboardView()
    {
        App.LogStartup("Dashboard 构造开始(InitializeComponent 前)");
        InitializeComponent();
        App.LogStartup("Dashboard.InitializeComponent 完成");
        SetGreeting();
        Loaded += async (s, e) =>
        {
            try
            {
                // 确保数据库已在后台完成初始化（schema 迁移等），避免首次查询表不存在
                await DbHelper.WarmupAsync();
                if (_vm == null)
                {
                    var ctx = DbHelper.CreateContext();
                    _vm = App.Services?.GetService<DashboardViewModel>()
                          ?? new DashboardViewModel(ctx);
                    _context = _vm.Context;
                    App.LogStartup("Dashboard 已建 DbContext/ViewModel(后台预热后)");
                }
                await InitializeAsync();
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Dashboard init error: {ex}"); }
        };
        // 每次页面变为可见（用户导航回首页）都重新查询并绑定，保证"最近观看/最近添加/继续观看"
        // 在看完新电影后即时更新。关键：PreWarmViews 在启动期已把本页加入可视树并初始化，
        // 若只靠首次 Loaded，之后导航回来时数据永远是启动时的旧快照。
        IsVisibleChanged += (s, e) =>
        {
            if (IsVisible && _isInitialized) _ = RefreshDataAsync();
        };
    }

    private async Task RefreshDataAsync()
    {
        try
        {
            var snap = await LoadSnapshotAsync();
            ApplySnapshot(snap);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Dashboard refresh error: {ex}"); }
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized) return;
        _isInitialized = true;
        try
        {
            // 复用启动期空闲预载的任务（若尚未完成则等待同一任务，不重复查询）
            var snap = await DashboardView.EnsureDataLoadedAsync();
            ApplySnapshot(snap);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Dashboard init error: {ex}");
        }
    }

    private void SetGreeting()
    {
        var hour = DateTime.Now.Hour;
        string greeting;
        string emoji;
        if (hour < 12)
        {
            greeting = LanguageManager.GetString("Dashboard_GreetingMorning");
            emoji = "☀️";
        }
        else if (hour < 18)
        {
            greeting = LanguageManager.GetString("Dashboard_GreetingAfternoon");
            emoji = "🌤️";
        }
        else
        {
            greeting = LanguageManager.GetString("Dashboard_GreetingEvening");
            emoji = "🌙";
        }
        GreetingText.Text = greeting;
        GreetingEmoji.Text = emoji;
    }

    private void SetupCardHoverEffects()
    {
        var cards = new[] { StatCard0, StatCard1, StatCard2, StatCard3, StatCard4 };

        foreach (var card in cards)
        {
            // 添加 ScaleTransform 用于悬停放大
            var scale = new ScaleTransform(1.0, 1.0);
            card.RenderTransformOrigin = new Point(0.5, 0.5);
            card.RenderTransform = scale;
            card.SizeChanged += (s, e) =>
            {
                scale.CenterX = card.ActualWidth / 2;
                scale.CenterY = card.ActualHeight / 2;
            };

            card.MouseEnter += (s, e) =>
            {
                var sb = new Storyboard();
                var animX = new DoubleAnimation(1.0, 1.04, TimeSpan.FromMilliseconds(150))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(animX, scale);
                Storyboard.SetTargetProperty(animX, new PropertyPath(ScaleTransform.ScaleXProperty));
                sb.Children.Add(animX);

                var animY = new DoubleAnimation(1.0, 1.04, TimeSpan.FromMilliseconds(150))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(animY, scale);
                Storyboard.SetTargetProperty(animY, new PropertyPath(ScaleTransform.ScaleYProperty));
                sb.Children.Add(animY);
                sb.Begin();
            };

            card.MouseLeave += (s, e) =>
            {
                var sb = new Storyboard();
                var animX = new DoubleAnimation(1.04, 1.0, TimeSpan.FromMilliseconds(150))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(animX, scale);
                Storyboard.SetTargetProperty(animX, new PropertyPath(ScaleTransform.ScaleXProperty));
                sb.Children.Add(animX);

                var animY = new DoubleAnimation(1.04, 1.0, TimeSpan.FromMilliseconds(150))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(animY, scale);
                Storyboard.SetTargetProperty(animY, new PropertyPath(ScaleTransform.ScaleYProperty));
                sb.Children.Add(animY);
                sb.Begin();
            };
        }
    }

    /// <summary>
    /// 启动期空闲预载入口（幂等）：立即返回已在进行/已完成的预载任务。
    /// App.OnStartup 在空闲期调用它，使首页数据与海报在用户进入首页前就准备好。
    /// </summary>
    internal static Task<DashboardSnapshot> EnsureDataLoadedAsync()
    {
        if (_preloadTask != null) return _preloadTask;
        lock (_preloadLock)
        {
            _preloadTask ??= LoadSnapshotAsync();
        }
        return _preloadTask;
    }

    /// <summary>
    /// 加载首页所需的全部数据，并把首页涉及的约 30 部电影海报在后台解码进内存缩略图缓存
    /// （PosterCache._thumbCache），使首页绑定后 PosterImageBehavior 直接命中缓存、瞬间显示，
    /// 不再逐张后台解码导致“进首页后还要等几秒图才出来”。
    /// 各区块独立 DbContext + 后台线程并行；近期观看用服务端分组去重，仅取 10 部（避免物化全部观看记录）。
    /// </summary>
    private static async Task<DashboardSnapshot> LoadSnapshotAsync()
    {
        App.LogStartup("Dashboard 预载开始");
        try
        {
            // 与数据库初始化串行：EnsureInitialized（首次建库/schema）会持 SQLite 写锁，
            // 查询若撞上会被 busy_timeout 拖慢数秒。非首次启动有 flag 快速路径，此 await 几乎不耗时。
            await DbHelper.WarmupAsync();

            // 性能关键：5 个查询合并到【单连接顺序执行】而非 5 个并发 Task.Run+连接——
            // 实测各查询均为毫秒级（WatchLogs 仅 12 行、索引齐全），并发的固定开销反而大：
            // 启动期线程池懒建线程（~500ms/个）+ 每连接打开都执行 PRAGMA journal_mode=WAL（抢锁）。
            // 顺序执行总耗时 <100ms，且只占 1 个线程、1 个连接。
            var coreTask = Task.Run(async () =>
            {
                using var c = DbHelper.CreateContext();

                // 最近添加
                var recentAdded = await c.Movies
                    .OrderByDescending(m => m.CreatedAt).Take(10).ToListAsync();

                // 最近观看：服务端按 MovieId 分组取最近 10 个不同影片，再仅取这 10 部实体
                var top = await c.WatchLogs
                    .GroupBy(w => w.MovieId)
                    .Select(g => new { MovieId = g.Key, Last = g.Max(x => (DateTime?)x.WatchDate) })
                    .OrderByDescending(x => x.Last)
                    .Take(10)
                    .ToListAsync();
                List<Movie> recentWatched;
                if (top.Count == 0) recentWatched = new List<Movie>();
                else
                {
                    var ids = top.Select(t => t.MovieId).ToList();
                    var movies = await c.Movies.Where(m => ids.Contains(m.Id)).ToListAsync();
                    recentWatched = top.Select(t => movies.FirstOrDefault(m => m.Id == t.MovieId))
                                       .OfType<Movie>()
                                       .ToList();
                }

                // 继续观看
                var continueWatching = await c.Movies
                    .Where(m => m.PlaybackPosition > 0)
                    .Select(m => new { Movie = m, Last = m.WatchLogs.Max(w => (DateTime?)w.WatchDate) })
                    .OrderByDescending(x => x.Last)
                    .Take(10)
                    .Select(x => x.Movie)
                    .ToListAsync();

                // 统计卡片
                var now = DateTime.Now;
                var monthStart = new DateTime(now.Year, now.Month, 1);
                var total = await c.Movies.CountAsync();
                var month = await c.WatchLogs.CountAsync(w => w.WatchDate >= monthStart);
                var minutes = await c.Movies.Where(m => m.WatchLogs.Any()).SumAsync(m => (int?)m.Runtime ?? 0);
                var avg = await c.Movies.Where(m => m.Rating.HasValue).AverageAsync(m => (double?)m.Rating);
                var fav = await c.Movies.CountAsync(m => m.IsFavorite);

                // 类型分布
                var raw = await c.Categories
                    .Where(cat => cat.Movies.Any() && cat.ParentId == null)
                    .Select(cat => new { cat.Name, Count = cat.Movies.Count })
                    .OrderByDescending(x => x.Count)
                    .Take(10)
                    .ToListAsync();
                var uncategorized = await c.Movies.CountAsync(m => m.CategoryId == null);

                return (recentAdded, recentWatched, continueWatching,
                        (total, month, minutes, avg, fav),
                        raw.Select(r => new GenreDatum(r.Name, r.Count)).ToList(), uncategorized);
            });

            // 上映提醒（联网；失败静默；不修改数据）
            var reminderTask = Task.Run(() => AppSettings.ReleaseReminderEnabled
                ? ReminderService.GetUpcomingWatchlistAsync()
                : null);

            var core = await coreTask;
            App.LogStartup("Dashboard 预载数据已取回(单连接顺序查询)");
            var reminders = await reminderTask;

            // 海报预热：把首页 3 个列表涉及的电影海报在后台解码进内存缩略图缓存，
            // 使首页绑定后 PosterImageBehavior 命中缓存、瞬间显示。
            // 注意：预热不阻塞快照返回（fire-and-forget）——首页先绑定文字数据立即呈现；
            // 预热完成前 PosterImageBehavior 命中缓存则直接显示，未命中则异步补齐，均不卡 UI。
            var warmMovies = core.Item1.Concat(core.Item2).Concat(core.Item3)
                .DistinctBy(m => m.Id).ToList();
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.WhenAll(warmMovies.Select(m =>
                        PosterCache.GetThumbnailAsync(m.Id, m.PosterData, 160, 240)));
                }
                catch { /* 海报预热失败不影响主流程 */ }
            });

            App.LogStartup("Dashboard 预载完成(数据就绪)");
            return new DashboardSnapshot(
                core.Item1, core.Item2, core.Item3,
                core.Item4.total, core.Item4.month, core.Item4.minutes, core.Item4.avg, core.Item4.fav,
                core.Item5, core.Item6, reminders);
        }
        catch (Exception ex)
        {

            System.Diagnostics.Debug.WriteLine($"Dashboard preload error: {ex}");
            return new DashboardSnapshot(new List<Movie>(), new List<Movie>(), new List<Movie>(),
                0, 0, 0, null, 0, new List<GenreDatum>(), 0, null);
        }
    }

    /// <summary>把预载好的快照绑定到首页各控件（UI 线程）。</summary>
    private void ApplySnapshot(DashboardSnapshot snap)
    {
        RecentAddedList.ItemsSource = snap.RecentAdded;
        RecentWatchedList.ItemsSource = snap.RecentWatched;
        ContinueWatchingList.ItemsSource = snap.ContinueWatching;
        ContinueWatchingPanel.Visibility = snap.ContinueWatching.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;

        TotalMoviesText.Text = snap.TotalMovies.ToString();
        MonthWatchedText.Text = snap.MonthWatched.ToString();
        TotalHoursText.Text = (snap.TotalMinutes / 60.0).ToString("F1") + "h";
        AvgRatingText.Text = snap.AvgRating.HasValue ? snap.AvgRating.Value.ToString("F1") : "-";
        FavoritesText.Text = snap.Favorites.ToString();

        RenderGenreChart(snap.Genres, snap.Uncategorized);

        if (snap.Reminders != null)
        {
            UpcomingReminderList.ItemsSource = snap.Reminders;
            UpcomingReminderPanel.Visibility = snap.Reminders.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            UpcomingReminderPanel.Visibility = Visibility.Collapsed;
        }

        Dispatcher.BeginInvoke(new Action(SetupCardHoverEffects),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    internal sealed record GenreDatum(string Name, int Count);

    private void RenderGenreChart(IList<GenreDatum> genreData, int uncategorized)
    {
        GenreChartPanel.Children.Clear();

        try
        {
            if (genreData.Count == 0 && uncategorized == 0)
            {
                GenreChartPanel.Children.Add(new TextBlock
                {
                    Text = LanguageManager.GetString("Heatmap_NoRecord"),
                    FontSize = 13,
                    Foreground = TryFindBrush("MaterialDesignHintForeground", Color.FromRgb(117, 117, 117))
                });
                return;
            }

            var max = Math.Max(genreData.Max(x => x.Count), uncategorized);

            int idx = 0;
            foreach (var item in genreData)
            {
                var bar = CreateGenreBar(item.Name, item.Count, max, BarColors[idx % BarColors.Length]);
                GenreChartPanel.Children.Add(bar);
                idx++;
            }

            // 未分类
            if (uncategorized > 0)
            {
                var bar = CreateGenreBar(
                    LanguageManager.GetString("MovieLib_Uncategorized"),
                    uncategorized, max, Color.FromRgb(158, 158, 158));
                GenreChartPanel.Children.Add(bar);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Genre chart error: {ex}");
        }
    }

    private static FrameworkElement CreateGenreBar(string name, int count, int max, Color color)
    {
        // 行高尽量紧凑：首页"类型分布"与"最近观看"同排，行高过大会让整页超出视口出现滚动条
        var row = new Grid { Margin = new Thickness(0, 1, 0, 1), Height = 14 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(35) });

        // 类型名称
        var nameTb = new TextBlock
        {
            Text = name,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Application.Current.TryFindResource("MaterialDesignBody") as Brush ?? Brushes.White
        };
        Grid.SetColumn(nameTb, 0);
        row.Children.Add(nameTb);

        // 比例进度条：用 Grid 列宽实现真正的百分比填充
        var pct = max > 0 ? (double)count / max : 0;
        var pctClamped = Math.Max(pct, 0.02); // 最小 2%，保证可见
        var barContainer = new Border
        {
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(8, 0, 4, 0),
            Background = Application.Current.TryFindResource("MaterialDesignDivider") as Brush
                ?? new SolidColorBrush(Color.FromRgb(80, 80, 80)),
            ClipToBounds = true
        };
        var barGrid = new Grid();
        barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(pctClamped, GridUnitType.Star) });
        barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - pctClamped, GridUnitType.Star) });
        var filledBar = new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(color),
            MinWidth = 4
        };
        Grid.SetColumn(filledBar, 0);
        barGrid.Children.Add(filledBar);
        barContainer.Child = barGrid;
        Grid.SetColumn(barContainer, 1);
        row.Children.Add(barContainer);

        // 数量
        var countTb = new TextBlock
        {
            Text = count.ToString(),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(4, 0, 0, 0),
            Foreground = Application.Current.TryFindResource("MaterialDesignBody") as Brush ?? Brushes.White
        };
        Grid.SetColumn(countTb, 2);
        row.Children.Add(countTb);

        return row;
    }

    private static Brush TryFindBrush(string key, Color fallback)
    {
        var brush = Application.Current.TryFindResource(key) as Brush;
        if (brush != null) return brush;
        var solid = new SolidColorBrush(fallback);
        solid.Freeze();
        return solid;
    }

    private void MovieCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is Movie movie)
        {
            var mainWindow = Application.Current.MainWindow as MainWindow;
            if (mainWindow != null)
            {
                mainWindow.NavigateTo("Movies");
                mainWindow.ShowMovieDetail(movie);
            }
        }
    }

    /// <summary>继续观看卡片：直接播放，播放器会按 PlaybackPosition 自动续播到记录处。</summary>
    private void ContinueWatchingCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is Movie movie)
        {
            var mainWindow = Application.Current.MainWindow as MainWindow;
            mainWindow?.ShowMoviePlayer(movie);
        }
    }

    private void UpcomingReminderCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is ReminderService.UpcomingReminder reminder)
        {
            var mainWindow = Application.Current.MainWindow as MainWindow;
            if (mainWindow != null)
            {
                mainWindow.NavigateTo("Movies");
                mainWindow.ShowMovieDetail(reminder.Movie);
            }
        }
    }

    private async void RandomPickBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var count = await _context.Movies.CountAsync();
            if (count == 0)
            {
                AppMessageBox.ShowInfo(
                    LanguageManager.GetString("Msg_NoRecommendData"),
                    LanguageManager.GetString("Msg_Hint"));
                return;
            }

            var rand = new Random();
            var skip = rand.Next(count);
            var movie = await _context.Movies
                .OrderBy(m => m.Id)
                .Skip(skip)
                .FirstOrDefaultAsync();

            if (movie != null)
            {
                var mainWindow = Application.Current.MainWindow as MainWindow;
                mainWindow?.NavigateTo("Movies");
                mainWindow?.ShowMovieDetail(movie);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Random pick error: {ex.Message}");
        }
    }
}
