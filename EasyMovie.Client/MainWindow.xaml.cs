using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EasyMovie.Client.Views;
using EasyMovie.Core.Enums;
using EasyMovie.Core.Models;

namespace EasyMovie.Client;

public partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private const uint WM_SETICON = 0x0080;
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
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SendMessage(hwnd, WM_SETICON, ICON_SMALL, IntPtr.Zero);
            SendMessage(hwnd, WM_SETICON, ICON_BIG, IntPtr.Zero);
            var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_DLGMODALFRAME);
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_FRAMECHANGED);
        };
        NavListBox.SelectedIndex = 0;
        NavigateTo("Movies");
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
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
                if (view is CategoryTagManageView catView)
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

    private void NavListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavListBox.SelectedItem is ListBoxItem item && item.Tag is string tag)
        {
            NavigateTo(tag);
        }
    }

    private readonly Dictionary<string, UserControl> _pageCache = new();
    private string _currentPage = "";

    private void NavigateTo(string page)
    {
        if (!_pageCache.TryGetValue(page, out var view))
        {
            view = page switch
            {
                "Movies" => new MovieListView(this),
                "Categories" => new CategoryTagManageView(),
                "Statistics" => new StatisticsView(),
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
            WatchStatus.Watching => LanguageManager.GetString("WatchStatus_Watching"),
            WatchStatus.Watched => LanguageManager.GetString("WatchStatus_Watched"),
            _ => ""
        };

        DetailPoster.Source = null;

        // 优先从数据库加载海报
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
                return;
            }
            catch { }
        }

        // 数据库无海报，从远程下载
        if (!string.IsNullOrEmpty(movie.PosterUrl))
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

                // 保存到数据库
                _ = SavePosterToDb(movie, bytes);
            }
            catch { }
        }
        else if (!string.IsNullOrEmpty(movie.CoverImagePath) && File.Exists(movie.CoverImagePath))
        {
            try
            {
                var bmp = new BitmapImage(new Uri(movie.CoverImagePath));
                bmp.Freeze();
                DetailPoster.Source = bmp;
            }
            catch { }
        }
    }
}
