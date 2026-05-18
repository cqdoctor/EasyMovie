using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MaterialDesignThemes.Wpf;
using MovieManager.Core.Enums;
using MovieManager.Core.Interfaces;
using MovieManager.Core.Models;
using MovieManager.Core.Services;
using MovieManager.Data;
using MovieManager.Data.Repositories;
using MovieManager.Tools.ImportExport;
using MovieManager.Tools.MovieApi;

namespace MovieManager.Client.Views;

public partial class MovieListView : UserControl
{
    private readonly MovieDbContext _context;
    private readonly IMovieService _movieService;
    private readonly ICategoryService _categoryService;
    private readonly ITagService _tagService;
    private int _currentPage = 1;
    private const int PageSize = 20;
    private int _totalCount;
    private bool _isCardView;

    public MovieListView()
    {
        InitializeComponent();
        _context = DbHelper.CreateContext();
        var movieRepo = new MovieRepository(_context);
        var categoryRepo = new CategoryRepository(_context);
        var tagRepo = new TagRepository(_context);
        _movieService = new MovieService(movieRepo, tagRepo);
        _categoryService = new CategoryService(categoryRepo);
        _tagService = new TagService(tagRepo);
        Loaded += async (s, e) => await LoadDataAsync();
        Unloaded += (s, e) => _context.Dispose();
    }

    private async Task LoadDataAsync()
    {
        try { await LoadCategoriesAsync(); await LoadYearsAsync(); await LoadMoviesAsync(); }
        catch (Exception ex) { MessageBox.Show("加载失败: " + ex.Message); }
    }

    private async Task LoadCategoriesAsync()
    {
        var categories = await _categoryService.GetAllAsync();
        CategoryFilter.Items.Clear();
        CategoryFilter.Items.Add(new ComboBoxItem { Content = "全部分类" });
        foreach (var cat in categories) CategoryFilter.Items.Add(new ComboBoxItem { Content = cat.Name, Tag = cat.Id });
        CategoryFilter.SelectedIndex = 0;
    }

    private async Task LoadYearsAsync()
    {
        YearFilter.Items.Clear();
        YearFilter.Items.Add(new ComboBoxItem { Content = "全部年份" });
        for (var year = DateTime.Now.Year; year >= 1888; year--) YearFilter.Items.Add(new ComboBoxItem { Content = year.ToString(), Tag = year });
        YearFilter.SelectedIndex = 0;
    }

    private async Task LoadMoviesAsync()
    {
        var (keyword, categoryId, status) = GetFilterValues();
        var sortInfo = GetSortInfo();
        var year = GetYearFilter();
        var (movies, total) = await _movieService.SearchAsync(keyword, categoryId, null, year, null, null, status, sortInfo.sortBy, sortInfo.sortDesc, _currentPage, PageSize);
        _totalCount = total;
        if (_isCardView) RenderCardView(movies); else MovieDataGrid.ItemsSource = movies;
        var totalPages = (int)Math.Ceiling((double)total / PageSize);
        PageInfo.Text = "共 " + total + " 部 · 第 " + _currentPage + "/" + Math.Max(1, totalPages) + " 页";
        PrevPageBtn.IsEnabled = _currentPage > 1;
        NextPageBtn.IsEnabled = _currentPage < totalPages;
        var hasMovies = movies.Any();
        MovieDataGrid.Visibility = !_isCardView && hasMovies ? Visibility.Visible : Visibility.Collapsed;
        CardScrollViewer.Visibility = _isCardView && hasMovies ? Visibility.Visible : Visibility.Collapsed;
        EmptyLabel.Visibility = hasMovies ? Visibility.Collapsed : Visibility.Visible;
    }

    private (string? keyword, int? categoryId, WatchStatus? status) GetFilterValues()
    {
        string? keyword = string.IsNullOrWhiteSpace(SearchBox.Text) ? null : SearchBox.Text.Trim();
        int? categoryId = null;
        if (CategoryFilter.SelectedItem is ComboBoxItem ci && ci.Tag is int cid) categoryId = cid;
        WatchStatus? status = null;
        if (StatusFilter.SelectedItem is ComboBoxItem si && si.Tag is string st) status = st switch { "WantToWatch" => WatchStatus.WantToWatch, "Watching" => WatchStatus.Watching, "Watched" => WatchStatus.Watched, _ => null };
        return (keyword, categoryId, status);
    }

    private int? GetYearFilter()
    {
        if (YearFilter.SelectedItem is ComboBoxItem yi && yi.Tag is int y) return y;
        return null;
    }

    private (string? sortBy, bool sortDesc) GetSortInfo()
    {
        if (SortFilter.SelectedItem is ComboBoxItem si && si.Tag is string st) { var p = st.Split('_'); if (p.Length == 2) return (p[0], p[1] == "desc"); }
        return ("createdat", true);
    }

    private void RenderCardView(List<Movie> movies)
    {
        CardPanel.Children.Clear();
        foreach (var movie in movies)
        {
            var card = new Card { Width = 200, Height = 320, Margin = new Thickness(8), Cursor = System.Windows.Input.Cursors.Hand };
            var stack = new StackPanel();
            stack.Children.Add(new Border { Height = 220, Background = System.Windows.Media.Brushes.Gray });
            stack.Children.Add(new TextBlock { Text = movie.Title, FontWeight = FontWeights.Bold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 8, 8, 2) });
            stack.Children.Add(new TextBlock { Text = movie.Year + " · " + (movie.Rating.HasValue ? "⭐" + movie.Rating : "未评分"), FontSize = 12, Margin = new Thickness(8, 0, 8, 0) });
            var st = movie.WatchStatus switch { WatchStatus.WantToWatch => "想看", WatchStatus.Watching => "在看", WatchStatus.Watched => "已看", _ => "" };
            stack.Children.Add(new TextBlock { Text = st, FontSize = 11, Margin = new Thickness(8, 4, 8, 8) });
            card.Content = stack;
            card.MouseLeftButtonUp += (s, e) => OpenDetailView(movie.Id);
            card.Tag = movie.Id;
            CardPanel.Children.Add(card);
        }
    }

    private void OpenDetailView(int movieId)
    {
        var detailView = new MovieDetailView(movieId, _movieService, _categoryService, _tagService);
        detailView.MovieSaved += async (s, e) => await LoadMoviesAsync();
        detailView.MovieDeleted += async (s, e) => await LoadMoviesAsync();
        new Window { Title = "电影详情", Content = detailView, Width = 700, Height = 780, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(this), ResizeMode = ResizeMode.CanResize }.ShowDialog();
    }

    private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { _currentPage = 1; await LoadMoviesAsync(); }
    private async void Filter_Changed(object sender, SelectionChangedEventArgs e) { _currentPage = 1; await LoadMoviesAsync(); }
    private async void TableViewBtn_Click(object sender, RoutedEventArgs e) { _isCardView = false; await LoadMoviesAsync(); }
    private async void CardViewBtn_Click(object sender, RoutedEventArgs e) { _isCardView = true; await LoadMoviesAsync(); }
    private void AddMovie_Click(object sender, RoutedEventArgs e) => OpenDetailView(0);

    private void OnlineSearch_Click(object sender, RoutedEventArgs e)
    {
        var sv = new OnlineSearchView();
        sv.MovieAdded += async (s, ev) => await LoadMoviesAsync();
        new Window { Title = "在线搜索", Content = sv, Width = 800, Height = 650, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private void MovieDataGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) { if (MovieDataGrid.SelectedItem is Movie m) OpenDetailView(m.Id); }
    private void EditMovie_Click(object sender, RoutedEventArgs e) { if (sender is Button b && b.Tag is int id) OpenDetailView(id); }
    private async void DeleteMovie_Click(object sender, RoutedEventArgs e) { if (sender is Button b && b.Tag is int id && MessageBox.Show("确定删除？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) { await _movieService.DeleteAsync(id); await LoadMoviesAsync(); } }
    private async void PlayMovie_Click(object sender, RoutedEventArgs e) { if (sender is Button b && b.Tag is int id) { var m = await _movieService.GetByIdAsync(id); if (m?.FilePath != null && File.Exists(m.FilePath)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = m.FilePath, UseShellExecute = true }); else MessageBox.Show("该电影没有关联视频文件。"); } }

    private async void FetchInfo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is int id)
        {
            b.IsEnabled = false;
            ProgressBar.Visibility = Visibility.Visible;
            try
            {
                var m = await _movieService.GetByIdAsync(id);
                if (m == null || string.IsNullOrWhiteSpace(m.Title)) { ProgressText.Text = "电影不存在"; return; }

                var cookie = MovieManager.Core.AppSettings.DoubanCookie;
                if (string.IsNullOrEmpty(cookie)) { ProgressText.Text = "❌ 未配置豆瓣Cookie，去设置页填入"; await Task.Delay(3000); return; }

                var keyword = MovieManager.Tools.MovieApi.DoubanApiClient.ExtractChineseKeyword(m.Title);
                ProgressText.Text = "豆瓣搜索: " + keyword + "...";
                var douban = new MovieManager.Tools.MovieApi.DoubanApiClient();
                var sr = await douban.SearchAsync(new MovieManager.Core.Interfaces.MovieSearchRequest { Keyword = m.Title, Page = 1, PageSize = 5 });
                if (sr.Results.Count == 0) { ProgressText.Text = "未找到: " + keyword; await Task.Delay(3000); return; }

                // 用文件名英文名验证匹配
                var engHint = MovieManager.Tools.MovieApi.DoubanApiClient.ExtractEnglishHint(m.Title);
                MovieManager.Core.Interfaces.MovieSearchResult? best = null;
                if (!string.IsNullOrEmpty(engHint))
                    foreach (var r in sr.Results)
                        if (!string.IsNullOrEmpty(r.OriginalTitle) && r.OriginalTitle.Contains(engHint, StringComparison.OrdinalIgnoreCase)) { best = r; break; }
                if (best == null) best = sr.Results[0];

                var info = await douban.GetDetailAsync(best.ExternalId ?? "") ?? best;
                var forceUpdate = best != sr.Results[0]; var updated = false;
                if (!string.IsNullOrEmpty(info.Director) && (string.IsNullOrEmpty(m.Director) || forceUpdate)) { m.Director = info.Director; updated = true; }
                if (!string.IsNullOrEmpty(info.Cast) && (string.IsNullOrEmpty(m.Cast) || forceUpdate)) { m.Cast = info.Cast; updated = true; }
                if (!string.IsNullOrEmpty(info.Country) && (string.IsNullOrEmpty(m.Country) || forceUpdate)) { m.Country = info.Country; updated = true; }
                if (!string.IsNullOrEmpty(info.Synopsis) && (string.IsNullOrEmpty(m.Synopsis) || forceUpdate)) { m.Synopsis = info.Synopsis; updated = true; }
                if (!string.IsNullOrEmpty(info.PosterUrl) && (string.IsNullOrEmpty(m.PosterUrl) || forceUpdate)) { m.PosterUrl = info.PosterUrl; updated = true; }
                if (info.Runtime.HasValue && (!m.Runtime.HasValue || forceUpdate)) { m.Runtime = info.Runtime; updated = true; }
                if (info.Year > 0 && (m.Year == 0 || forceUpdate)) { m.Year = info.Year; updated = true; }
                if (string.IsNullOrEmpty(m.TmdbId) || forceUpdate) { m.DoubanId = info.ExternalId; updated = true; }

                if (updated) { await _movieService.UpdateAsync(m); await LoadMoviesAsync(); ProgressText.Text = "✅ 已更新: " + m.Title; }
                else ProgressText.Text = "ℹ️ 无需更新: " + m.Title;
            }
            catch (Exception ex) { ProgressText.Text = "❌ 获取失败: " + ex.Message; }
            finally
            {
                b.IsEnabled = true;
                await Task.Delay(2000);
                ProgressBar.Visibility = Visibility.Collapsed;
            }
        }
    }

    private async void FetchAll_Click(object sender, RoutedEventArgs e)
    {
        var cookie = MovieManager.Core.AppSettings.DoubanCookie;
        if (string.IsNullOrEmpty(cookie)) { MessageBox.Show("请先去设置页填入豆瓣Cookie"); return; }

        var (all, _) = await _movieService.SearchAsync(null, null, null, null, null, null, null, "createdat", false, 1, 1000);
        var needFetch = all.Where(m => string.IsNullOrEmpty(m.Director)).ToList();
        if (needFetch.Count == 0) { MessageBox.Show("所有电影已有信息"); return; }

        ProgressBar.Visibility = Visibility.Visible;
        var douban = new MovieManager.Tools.MovieApi.DoubanApiClient();
        var done = 0;
        foreach (var m in needFetch)
        {
            try
            {
                var kw = MovieManager.Tools.MovieApi.DoubanApiClient.ExtractChineseKeyword(m.Title);
                ProgressText.Text = $"({++done}/{needFetch.Count}) {kw}...";
                var sr = await douban.SearchAsync(new MovieManager.Core.Interfaces.MovieSearchRequest { Keyword = m.Title, Page = 1, PageSize = 3 });
                if (sr.Results.Count == 0) continue;

                var engHint = MovieManager.Tools.MovieApi.DoubanApiClient.ExtractEnglishHint(m.Title);
                MovieManager.Core.Interfaces.MovieSearchResult? best = null;
                if (!string.IsNullOrEmpty(engHint))
                    foreach (var r in sr.Results)
                        if (!string.IsNullOrEmpty(r.OriginalTitle) && r.OriginalTitle.Contains(engHint, StringComparison.OrdinalIgnoreCase)) { best = r; break; }
                if (best == null) best = sr.Results[0];

                var info = await douban.GetDetailAsync(best.ExternalId ?? "") ?? best;
                if (!string.IsNullOrEmpty(info.Director)) m.Director = info.Director;
                if (!string.IsNullOrEmpty(info.Cast)) m.Cast = info.Cast;
                if (!string.IsNullOrEmpty(info.Country)) m.Country = info.Country;
                if (!string.IsNullOrEmpty(info.Synopsis)) m.Synopsis = info.Synopsis;
                if (!string.IsNullOrEmpty(info.PosterUrl)) m.PosterUrl = info.PosterUrl;
                if (info.Runtime.HasValue) m.Runtime = info.Runtime;
                if (info.Year > 0 && m.Year == 0) m.Year = info.Year;
                if (string.IsNullOrEmpty(m.DoubanId)) m.DoubanId = info.ExternalId;
                await _movieService.UpdateAsync(m);
                await LoadMoviesAsync();
            }
            catch { }
            await Task.Delay(1500);
        }
        ProgressBar.Visibility = Visibility.Collapsed;
        await LoadMoviesAsync();
    }
    private async void PrevPage_Click(object sender, RoutedEventArgs e) { if (_currentPage > 1) { _currentPage--; await LoadMoviesAsync(); } }
    private async void NextPage_Click(object sender, RoutedEventArgs e) { var tp = (int)Math.Ceiling((double)_totalCount / PageSize); if (_currentPage < tp) { _currentPage++; await LoadMoviesAsync(); } }

    private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase) { ".mp4",".mkv",".avi",".mov",".wmv",".flv",".webm",".m4v",".mpg",".mpeg",".ts",".rmvb" };

    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择包含视频文件的文件夹" };
            string? path = null;
            try { if (dlg.ShowDialog() == true) path = dlg.FolderName; } catch { }
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) { MessageBox.Show("请选择有效文件夹"); return; }

            ProgressBar.Visibility = Visibility.Visible;
            var files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories).Where(f => VideoExts.Contains(Path.GetExtension(f))).ToList();
            var addedIds = new List<int>();

            // 阶段1: 快速导入所有文件 (跳过已存在的)
            var existingPaths = new HashSet<string>((await _movieService.GetAllAsync()).Where(m => m.FilePath != null).Select(m => m.FilePath!));
            for (int i = 0; i < files.Count; i++)
            {
                if (existingPaths.Contains(files[i])) { ProgressText.Text = "(" + (i + 1) + "/" + files.Count + ") 跳过重复: " + Path.GetFileName(files[i]); continue; }
                ProgressText.Text = "(" + (i + 1) + "/" + files.Count + ") " + Path.GetFileName(files[i]);
                try
                {
                    var (title, year) = new FolderImportService().ParseFileName(files[i]);
                    var m = await _movieService.AddAsync(new Movie { Title = title, Year = year ?? 0, FilePath = files[i], CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
                    addedIds.Add(m.Id);
                }
                catch { }
            }

            // 刷新列表让用户看到
            await LoadMoviesAsync();

            // 阶段2: 逐个获取豆瓣信息
            if (addedIds.Count > 0)
            {
                var douban = new DoubanApiClient();
                var maoyan = new MovieManager.Tools.MovieApi.MaoyanApiClient();
                var tmdbKey = AppSettings.TmdbApiKey;
                // WARNING: 此处使用了公共测试 API Key 作为 fallback，仅用于开发/测试环境
                // 生产环境应在设置页配置自己的 TMDB API Key: https://www.themoviedb.org/settings/api
                var tmdb = new TmdbApiClient(!string.IsNullOrEmpty(tmdbKey) ? tmdbKey : "1f54bd990f1cdfb230adb312546d765d");
                // 猫眼优先 -> 豆瓣 -> TMDB
                var api = new MovieApiService(maoyan, tmdb);
                var done = 0;
                foreach (var id in addedIds)
                {
                    var m = await _movieService.GetByIdAsync(id);
                    if (m == null || string.IsNullOrWhiteSpace(m.Title)) { done++; continue; }
                    ProgressText.Text = "获取信息 (" + (++done) + "/" + addedIds.Count + "): " + m.Title;
                    try
                    {
                        var sr = await api.SearchAsync(m.Title, 1, 1);
                        if (sr.Results.Count > 0)
                        {
                            var info = await api.GetDetailAsync(sr.Results[0].ExternalId ?? "", sr.Results[0].Source) ?? sr.Results[0];
                            if (!string.IsNullOrEmpty(info.Director)) m.Director = info.Director;
                            if (!string.IsNullOrEmpty(info.Cast)) m.Cast = info.Cast;
                            if (!string.IsNullOrEmpty(info.Country)) m.Country = info.Country;
                            if (!string.IsNullOrEmpty(info.Synopsis)) m.Synopsis = info.Synopsis;
                            if (!string.IsNullOrEmpty(info.PosterUrl)) m.PosterUrl = info.PosterUrl;
                            if (info.Runtime.HasValue) m.Runtime = info.Runtime;
                            if (info.Year > 0 && m.Year == 0) m.Year = info.Year;
                            if (info.Source == "douban") m.DoubanId = info.ExternalId;
                            else if (info.Source == "tmdb") m.DoubanId = info.ExternalId;
                            await _movieService.UpdateAsync(m);
                            await LoadMoviesAsync(); // 每部更新后立即刷新列表
                        }
                    }
                    catch { }
                    await Task.Delay(600);
                }
            }

            ProgressBar.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { ProgressBar.Visibility = Visibility.Collapsed; MessageBox.Show("导入失败: " + ex.Message); }
    }
}
