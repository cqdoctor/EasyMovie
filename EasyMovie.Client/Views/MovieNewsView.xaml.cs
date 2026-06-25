using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EasyMovie.Core;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;
using EasyMovie.Core.Services;
using EasyMovie.Data;
using EasyMovie.Data.Repositories;
using EasyMovie.Tools.MovieApi;
using Microsoft.EntityFrameworkCore;

namespace EasyMovie.Client.Views;

public partial class MovieNewsView : UserControl
{
    private readonly MovieNewsService _newsService;
    private readonly Dictionary<string, List<MovieNewsItem>> _cache = new();
    private readonly Dictionary<string, bool> _loaded = new()
    {
        ["coming"] = false, ["nowplaying"] = false, ["top250"] = false, ["hot"] = false
    };
    private bool _isRefreshing;
    private int _top250Start; // Top250 分页偏移

    private static HttpClient? _newsImgClient;

    public MovieNewsView()
    {
        InitializeComponent();
        _newsService = new MovieNewsService();
        Loaded += async (_, _) => await InitializeAsync();
        IsVisibleChanged += async (_, e) =>
        {
            if (e.NewValue is true && _loaded.Any(kv => kv.Value))
                await LoadCurrentTabAsync();
        };
    }

    public async Task InitializeAsync()
    {
        if (_loaded["coming"]) return;
        await LoadCurrentTabAsync();
    }

    private async void NewsTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NewsTabControl == null) return;
        await LoadCurrentTabAsync();
    }

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        try
        {
            var category = GetCurrentCategory();
            _cache.Remove(category);
            _loaded[category] = false;
            if (category == "top250")
            {
                _top250Start = 0;
                Top250Panel.Children.Clear();
            }
            await LoadCurrentTabAsync();
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private string GetCurrentCategory()
    {
        return NewsTabControl?.SelectedIndex switch
        {
            0 => "coming",
            1 => "nowplaying",
            2 => "top250",
            3 => "hot",
            _ => "coming"
        };
    }

    private async Task LoadCurrentTabAsync()
    {
        var category = GetCurrentCategory();
        if (_loaded[category] && _cache.ContainsKey(category)) return;

        var (panel, loading, empty) = GetUiElements(category);
        if (panel == null) return;

        loading.Visibility = Visibility.Visible;
        panel.Visibility = Visibility.Collapsed;
        empty.Visibility = Visibility.Collapsed;
        if (Top250LoadMoreBtn != null) Top250LoadMoreBtn.Visibility = Visibility.Collapsed;

        try
        {
            MovieNewsResult result;
            if (category == "top250")
            {
                result = await _newsService.GetTop250Async(_top250Start);
            }
            else
            {
                result = category switch
                {
                    "coming" => await _newsService.GetComingSoonAsync(),
                    "nowplaying" => await _newsService.GetNowPlayingAsync(),
                    "hot" => await _newsService.GetMaoyanHotAsync(),
                    _ => MovieNewsResult.Fail("未知分类")
                };
            }

            loading.Visibility = Visibility.Collapsed;

            if (result.Success && result.Items.Count > 0)
            {
                if (category == "top250")
                {
                    // Top250 追加模式
                    if (!_cache.ContainsKey("top250")) _cache["top250"] = new List<MovieNewsItem>();
                    _cache["top250"].AddRange(result.Items);
                    _top250Start += result.Items.Count;
                    _loaded["top250"] = true;

                    foreach (var item in result.Items)
                    {
                        panel.Children.Insert(panel.Children.Count, CreateMovieCard(item));
                    }
                    // 显示/隐藏"加载更多"按钮
                    if (Top250LoadMoreBtn != null)
                        Top250LoadMoreBtn.Visibility = _top250Start >= 250 ? Visibility.Collapsed : Visibility.Visible;
                }
                else
                {
                    _cache[category] = result.Items;
                    _loaded[category] = true;

                    panel.Children.Clear();
                    foreach (var item in result.Items)
                    {
                        panel.Children.Add(CreateMovieCard(item));
                    }
                }
                panel.Visibility = Visibility.Visible;
                empty.Visibility = Visibility.Collapsed;
            }
            else
            {
                // 显示错误信息
                var errorMsg = result.Error ?? LanguageManager.GetString("News_NoData");
                if (category == "top250" && _cache.TryGetValue("top250", out var existing) && existing.Count > 0)
                {
                    // 已有数据但加载更多失败，只隐藏按钮
                    if (Top250LoadMoreBtn != null) Top250LoadMoreBtn.Visibility = Visibility.Collapsed;
                }
                else
                {
                    empty.Text = errorMsg;
                    empty.Visibility = Visibility.Visible;
                    panel.Visibility = Visibility.Collapsed;
                    _loaded[category] = false;
                }
            }
        }
        catch (Exception ex)
        {
            loading.Visibility = Visibility.Collapsed;
            empty.Text = $"加载失败: {ex.Message}";
            empty.Visibility = Visibility.Visible;
        }
    }

    private async void Top250LoadMoreBtn_Click(object sender, RoutedEventArgs e)
    {
        Top250LoadMoreBtn.IsEnabled = false;
        try
        {
            var result = await _newsService.GetTop250Async(_top250Start);
            if (result.Success && result.Items.Count > 0)
            {
                if (!_cache.ContainsKey("top250")) _cache["top250"] = new List<MovieNewsItem>();
                _cache["top250"].AddRange(result.Items);
                _top250Start += result.Items.Count;

                foreach (var item in result.Items)
                {
                    Top250Panel.Children.Insert(Top250Panel.Children.Count, CreateMovieCard(item));
                }
            }
            // 隐藏按钮如果已到250或加载失败
            Top250LoadMoreBtn.Visibility = _top250Start >= 250 ? Visibility.Collapsed : Visibility.Visible;
        }
        finally
        {
            Top250LoadMoreBtn.IsEnabled = true;
        }
    }

    private (WrapPanel? panel, StackPanel loading, TextBlock empty) GetUiElements(string category)
    {
        return category switch
        {
            "coming" => (ComingSoonPanel, ComingSoonLoading, ComingSoonEmpty),
            "nowplaying" => (NowPlayingPanel, NowPlayingLoading, NowPlayingEmpty),
            "top250" => (Top250Panel, Top250Loading, Top250Empty),
            "hot" => (MaoyanHotPanel, MaoyanHotLoading, MaoyanHotEmpty),
            _ => (null, ComingSoonLoading, ComingSoonEmpty)
        };
    }

    private Border CreateMovieCard(MovieNewsItem item)
    {
        var cardWidth = 160;

        var card = new Border
        {
            Width = cardWidth,
            Margin = new Thickness(6, 0, 6, 16),
            Background = SafeFindBrush("MaterialDesignCardBackground", Color.FromRgb(45, 45, 45)),
            CornerRadius = new CornerRadius(8),
            Cursor = Cursors.Hand,
            Tag = item,
            ToolTip = CreateToolTip(item)
        };

        var stack = new StackPanel();

        // 海报区域（用 Grid 叠加排名角标）
        var posterGrid = new Grid();

        var posterBorder = new Border
        {
            Height = 210,
            CornerRadius = new CornerRadius(8, 8, 0, 0),
            ClipToBounds = true,
            Background = SafeFindBrush("MaterialDesignDivider", Color.FromRgb(60, 60, 60))
        };
        var posterImg = new Image
        {
            Stretch = Stretch.UniformToFill,
            VerticalAlignment = VerticalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(posterImg, BitmapScalingMode.HighQuality);
        posterBorder.Child = posterImg;
        posterGrid.Children.Add(posterBorder);

        // 排名角标
        if (item.Rank.HasValue)
        {
            var rankBadge = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(255, 152, 0)),
                CornerRadius = new CornerRadius(0, 0, 8, 0),
                Padding = new Thickness(8, 2, 8, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            rankBadge.Child = new TextBlock
            {
                Text = $"#{item.Rank}",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White
            };
            posterGrid.Children.Add(rankBadge);
        }

        stack.Children.Add(posterGrid);

        // 信息区
        var infoPanel = new StackPanel { Margin = new Thickness(8, 6, 8, 8) };

        // 标题
        var titleBlock = new TextBlock
        {
            Text = item.Title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = SafeFindBrush("MaterialDesignBody", Colors.White),
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 36,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        infoPanel.Children.Add(titleBlock);

        // 评分行
        var ratingRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        if (item.Rating.HasValue)
        {
            ratingRow.Children.Add(new TextBlock
            {
                Text = "⭐",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            });
            ratingRow.Children.Add(new TextBlock
            {
                Text = item.Rating.Value.ToString("F1"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 215, 0)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0)
            });
        }
        if (!string.IsNullOrEmpty(item.ReleaseDate))
        {
            ratingRow.Children.Add(new TextBlock
            {
                Text = item.ReleaseDate,
                FontSize = 10,
                Foreground = SafeFindBrush("MaterialDesignHintForeground", Color.FromRgb(150, 150, 150)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }
        infoPanel.Children.Add(ratingRow);

        // 导演/国家
        if (!string.IsNullOrEmpty(item.Director))
        {
            infoPanel.Children.Add(new TextBlock
            {
                Text = "🎬 " + item.Director,
                FontSize = 10,
                Foreground = SafeFindBrush("MaterialDesignBodyLight", Color.FromRgb(180, 180, 180)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1, 0, 0)
            });
        }
        if (!string.IsNullOrEmpty(item.Country))
        {
            infoPanel.Children.Add(new TextBlock
            {
                Text = "🌍 " + item.Country,
                FontSize = 10,
                Foreground = SafeFindBrush("MaterialDesignBodyLight", Color.FromRgb(180, 180, 180)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1, 0, 0)
            });
        }

        stack.Children.Add(infoPanel);
        card.Child = stack;

        // 加载海报
        if (!string.IsNullOrEmpty(item.PosterUrl))
        {
            _ = LoadPosterAsync(posterImg, item.PosterUrl);
        }

        // 点击事件
        card.MouseLeftButtonUp += (s, e) => OnCardClick(item);

        return card;
    }

    private static string CreateToolTip(MovieNewsItem item)
    {
        var parts = new List<string> { item.Title };
        if (item.Year > 0) parts.Add(item.Year + LanguageManager.GetString("Msg_YearSuffix"));
        if (!string.IsNullOrEmpty(item.Director)) parts.Add("🎬 " + item.Director);
        if (!string.IsNullOrEmpty(item.Cast)) parts.Add("🎭 " + item.Cast);
        if (!string.IsNullOrEmpty(item.Country)) parts.Add("🌍 " + item.Country);
        if (item.Rating.HasValue) parts.Add("⭐ " + item.Rating.Value.ToString("F1"));
        if (!string.IsNullOrEmpty(item.Synopsis)) parts.Add(item.Synopsis);
        return string.Join("\n", parts);
    }

    private async void OnCardClick(MovieNewsItem item)
    {
        // 检查本地是否已有此电影
        if (!string.IsNullOrEmpty(item.ExternalId) && item.Source == "douban")
        {
            try
            {
                using var ctx = DbHelper.CreateContext();
                var existing = await ctx.Movies.FirstOrDefaultAsync(m => m.DoubanId == item.ExternalId);
                if (existing != null)
                {
                    if (Window.GetWindow(this) is MainWindow mw)
                        mw.ShowMovieDetail(existing);
                    return;
                }
            }
            catch { }
        }

        // 不在库中，弹出添加确认
        var msg = string.Format(LanguageManager.GetString("News_AddConfirm"), item.Title);
        if (!AppMessageBox.Confirm(msg, LanguageManager.GetString("Msg_Confirm"))) return;

        try
        {
            // 获取详细信息
            MovieSearchResult? searchResult = null;
            if (item.Source == "douban" && !string.IsNullOrEmpty(item.ExternalId))
            {
                var doubanClient = new DoubanApiClient();
                searchResult = await doubanClient.GetDetailAsync(item.ExternalId);
            }
            else if (item.Source == "maoyan" && !string.IsNullOrEmpty(item.ExternalId))
            {
                var maoyanClient = new MaoyanApiClient();
                searchResult = await maoyanClient.GetDetailAsync(item.ExternalId);
            }

            Movie movie;
            if (searchResult != null)
            {
                movie = await MovieApiService.MapToMovieAsync(searchResult, new CategoryService(new CategoryRepository(DbHelper.CreateContext())));
            }
            else
            {
                movie = new Movie
                {
                    Title = item.Title,
                    OriginalTitle = item.OriginalTitle,
                    Year = item.Year,
                    Director = item.Director,
                    Cast = item.Cast,
                    Country = item.Country,
                    PosterUrl = item.PosterUrl,
                    Runtime = item.Runtime,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                if (item.Source == "douban") movie.DoubanId = item.ExternalId;
            }

            using var ctx2 = DbHelper.CreateContext();
            ctx2.Movies.Add(movie);
            await ctx2.SaveChangesAsync();

            AppMessageBox.ShowInfo(string.Format(LanguageManager.GetString("OnlineSearch_Added"), item.Title));
        }
        catch (Exception ex)
        {
            AppMessageBox.ShowError(LanguageManager.GetString("Msg_LoadFailed") + ex.Message);
        }
    }

    private static async Task LoadPosterAsync(Image img, string url)
    {
        try
        {
            if (_newsImgClient == null)
            {
                _newsImgClient = new HttpClient(new HttpClientHandler
                {
                    AutomaticDecompression = System.Net.DecompressionMethods.All,
                    AllowAutoRedirect = true
                })
                {
                    Timeout = TimeSpan.FromSeconds(10)
                };
                _newsImgClient.DefaultRequestHeaders.Add("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
                _newsImgClient.DefaultRequestHeaders.Add("Referer", "https://movie.douban.com/");
                var cookie = AppSettings.DoubanCookie;
                if (!string.IsNullOrEmpty(cookie))
                    _newsImgClient.DefaultRequestHeaders.Add("Cookie", cookie);
            }

            // 豆瓣图片CDN短链接需要特殊处理
            var fetchUrl = url;
            if (url.Contains("aka.doubaocdn.com") || url.Contains("img.doubanio.com"))
            {
                // 这些短链接会302重定向到真实图片地址，HttpClient会自动跟随
                fetchUrl = url;
            }

            var bytes = await _newsImgClient.GetByteArrayAsync(fetchUrl);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                img.Source = bmp;
            });
        }
        catch { }
    }

    private static Brush SafeFindBrush(string resourceKey, Color fallback)
    {
        var brush = Application.Current.TryFindResource(resourceKey) as Brush;
        if (brush != null) return brush;
        var solid = new SolidColorBrush(fallback);
        solid.Freeze();
        return solid;
    }
}
