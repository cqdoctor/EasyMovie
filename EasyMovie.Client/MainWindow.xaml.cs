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
                await ctx.SaveChangesAsync();
            }
        }
        catch { }
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
        InitializeComponent();
        LoadInputBindings();
        Loaded += OnLoaded;
        StateChanged += OnStateChanged;
        BackupService.EnsureAutoBackup();
        NavigateTo("Dashboard");
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        MaximizeBtn.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        RegisterNavButtons();
        Dispatcher.BeginInvoke(new Action(PreWarmViews), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void PreWarmViews()
    {
        // 创建页面实例、加入可视树、预加载数据
        // 首次加入可视树时 WPF 完成布局，后续切换只改 Visibility
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            var pages = new (string key, Func<UserControl> create)[]
            {
                ("Dashboard", () => new DashboardView()),
                ("Movies", () => new MovieListView(this)),
                ("Categories", () => new CategoryTagManageView()),
                ("Statistics", () => new StatisticsView()),
                ("Settings", () => new SettingsView()),
            };

            foreach (var (key, create) in pages)
            {
                if (_pageCache.ContainsKey(key)) continue;
                var view = create();
                _pageCache[key] = view;
                view.Visibility = Visibility.Collapsed;
                ContentArea.Children.Add(view);

                // 预加载数据
                if (view is DashboardView dashView)
                    await dashView.InitializeAsync();
                else if (view is CategoryTagManageView catView)
                    await catView.InitializeAsync();
                else if (view is StatisticsView statsView)
                    await statsView.InitializeAsync();
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
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

    private readonly Dictionary<string, Button> _navButtons = new();

    private void RegisterNavButtons()
    {
        // 遍历 NavPanel 的逻辑树中所有带 Tag 的 Button（包括折叠的 Expander 子项）
        foreach (var child in FindLogicalButtons(NavPanel))
        {
            if (child.Tag is string tag)
                _navButtons[tag] = child;
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

    private void GroupToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb) return;

        // 获取箭头的旋转变换
        var packIcon = FindPackIconInHeader(tb);
        if (packIcon?.RenderTransform is RotateTransform rot)
        {
            var targetAngle = tb.IsChecked == true ? 90.0 : 0.0;
            var anim = new DoubleAnimation(targetAngle, TimeSpan.FromMilliseconds(150));
            rot.BeginAnimation(RotateTransform.AngleProperty, anim);
        }

        if (tb == AnalysisToggle)
        {
            AnalysisSubPanel.Visibility = tb.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            // 同时折叠另一个分组
            if (tb.IsChecked == true)
            {
                DiscoverToggle.IsChecked = false;
                DiscoverSubPanel.Visibility = Visibility.Collapsed;
                // 更新另一个箭头角度
                if (FindPackIconInHeader(DiscoverToggle)?.RenderTransform is RotateTransform rot2)
                    rot2.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
            }
        }
        else if (tb == DiscoverToggle)
        {
            DiscoverSubPanel.Visibility = tb.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            if (tb.IsChecked == true)
            {
                AnalysisToggle.IsChecked = false;
                AnalysisSubPanel.Visibility = Visibility.Collapsed;
                if (FindPackIconInHeader(AnalysisToggle)?.RenderTransform is RotateTransform rot1)
                    rot1.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
            }
        }
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

    public void NavigateTo(string page)
    {
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

        // 用 Visibility 切换，避免重复布局
        foreach (UIElement child in ContentArea.Children)
            child.Visibility = child == view ? Visibility.Visible : Visibility.Collapsed;

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
            catch { }
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
            catch { }
        }
        else if (!posterLoaded && !string.IsNullOrEmpty(movie.CoverImagePath) && File.Exists(movie.CoverImagePath))
        {
            try
            {
                var bmp = new BitmapImage(new Uri(movie.CoverImagePath));
                bmp.Freeze();
                DetailPoster.Source = bmp;
            }
            catch { }
        }

        await LoadWatchLogsAsync(movie.Id);
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
        catch { }
    }

    private static Brush TryCreateBrush(string? color)
    {
        if (!string.IsNullOrEmpty(color))
        {
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); }
            catch { }
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
        catch { }
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
    private void Nav3_Executed(object sender, ExecutedRoutedEventArgs e) { NavigateTo("Statistics"); }
    private void Nav4_Executed(object sender, ExecutedRoutedEventArgs e) { NavigateTo("Settings"); }

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
