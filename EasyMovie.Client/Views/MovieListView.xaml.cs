using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;
using EasyMovie.Core.Enums;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;
using EasyMovie.Core.Services;
using EasyMovie.Data;
using Microsoft.EntityFrameworkCore;
using System.Windows.Media.Imaging;
using EasyMovie.Data.Repositories;
using EasyMovie.Tools.ImportExport;
using EasyMovie.Tools.MovieApi;
using EasyMovie.Client.Controls;

namespace EasyMovie.Client.Views;

public partial class MovieListView : UserControl
{
    private readonly MovieDbContext _context;
    private readonly IMovieService _movieService;
    private readonly ICategoryService _categoryService;
    private readonly ITagService _tagService;
    private readonly IRecommendationService _recommendationService;
    private readonly CollectionService _collectionService;
    private readonly MainWindow? _mainWindow;
    private int _currentPage = 1;
    private const int PageSize = 20;
    private int _totalCount;
    private bool _isCardView;
    private bool _isPosterView;
    private bool _isCollectionView;

    private bool _isFirstLoad = true;
    private bool _quickFilterFavorites;
    private bool _quickFilterWatchlist;

    public MovieListView(MainWindow? mainWindow = null)
    {
        InitializeComponent();
        _mainWindow = mainWindow;
        _context = DbHelper.CreateContext();
        var movieRepo = new MovieRepository(_context);
        var categoryRepo = new CategoryRepository(_context);
        var tagRepo = new TagRepository(_context);
        _movieService = new MovieService(movieRepo, tagRepo);
        _categoryService = new CategoryService(categoryRepo);
        _tagService = new TagService(tagRepo);
        _recommendationService = new RecommendationService(movieRepo);
        _collectionService = new CollectionService(_context);
        Loaded += async (s, e) =>
        {
            if (_isFirstLoad)
            {
                _isFirstLoad = false;
                UpdateViewButtons();
                await LoadDataAsync();
                PreMeasureExpander();
            }
            else
            {
                await LoadMoviesAsync();
            }
        };
    }

    private void PreMeasureExpander()
    {
        AdvancedFilterPanel.IsExpanded = true;
        AdvancedFilterPanel.UpdateLayout();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            AdvancedFilterPanel.IsExpanded = false;
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private async Task LoadDataAsync()
    {
        try
        {
            // 只查一次数据库，后续所有方法复用这份数据
            var allMovies = await _movieService.GetAllAsync();
            var allCats = await _categoryService.GetAllAsync();

            await RebuildSearchIndexBatchAsync(allMovies);
            await AutoAssignCountryCategoriesBatchAsync(allMovies, allCats);

            // 数据可能已变更，重新加载
            allCats = await _categoryService.GetAllAsync();

            PopulateCategoryFilter(allCats);
            PopulateBatchCategoryCombo(allCats);
            PopulateYearFilter(allMovies);
            PopulateAdvancedFilterOptions(allMovies);
            await LoadMoviesAsync();
        }
        catch (Exception ex) { AppMessageBox.ShowError(LanguageManager.GetString("Msg_LoadFailed") + ex.Message); }
    }

    /// <summary>批量重建搜索索引（一次 SaveChanges）</summary>
    private async Task RebuildSearchIndexBatchAsync(List<Movie> movies)
    {
        var needUpdate = movies.Where(m => string.IsNullOrEmpty(m.SearchIndex)).ToList();
        if (needUpdate.Count == 0) return;
        foreach (var m in needUpdate)
            m.SearchIndex = PinyinIndexHelper.BuildSearchIndex(m.Title, m.OriginalTitle, m.Director, m.Cast);
        await _context.SaveChangesAsync();
    }

    private static readonly HashSet<string> JunkCategoryNames = new(StringComparer.Ordinal)
    {
        "人收藏", "人评论", "人看", "人想看", "人看过", "人评价", "人关注", "人推荐"
    };

    private static bool IsValidCategoryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (int.TryParse(name, out _)) return false;
        if (JunkCategoryNames.Any(j => name.Contains(j))) return false;
        return true;
    }

    /// <summary>批量清理无效分类并自动分配国家分类</summary>
    private async Task AutoAssignCountryCategoriesBatchAsync(List<Movie> movies, List<Category> allCats)
    {
        // 1. 清理无效分类
        var invalidCats = allCats.Where(c => !IsValidCategoryName(c.Name)).ToList();
        foreach (var cat in invalidCats)
        {
            foreach (var m in movies.Where(m => m.CategoryId == cat.Id))
                m.CategoryId = null;
            _context.Categories.Remove(cat);
        }
        if (invalidCats.Count > 0) await _context.SaveChangesAsync();

        // 2. 为有国家信息但无分类的电影自动分配分类
        var uncatMovies = movies.Where(m => !m.CategoryId.HasValue && !string.IsNullOrWhiteSpace(m.Country)).ToList();
        if (uncatMovies.Count == 0) return;

        // 重新加载分类（可能已删除无效分类）
        var validCats = await _categoryService.GetAllAsync();
        foreach (var movie in uncatMovies)
        {
            var firstCountry = movie.Country!.Split('/', ' ', '·')
                .FirstOrDefault(c => IsValidCategoryName(c.Trim()))?.Trim();
            if (string.IsNullOrEmpty(firstCountry) || !IsValidCategoryName(firstCountry)) continue;
            var existing = validCats.FirstOrDefault(c => c.Name == firstCountry);
            if (existing != null)
            {
                movie.CategoryId = existing.Id;
            }
            else
            {
                var newCat = new Category { Name = firstCountry, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
                _context.Categories.Add(newCat);
                await _context.SaveChangesAsync();
                validCats.Add(newCat);
                movie.CategoryId = newCat.Id;
            }
        }
        await _context.SaveChangesAsync();
    }

    private void PopulateCategoryFilter(List<Category> categories)
    {
        CategoryFilter.Items.Clear();
        CategoryFilter.Items.Add(new ComboBoxItem { Content = LanguageManager.GetString("MovieLib_AllCategories") });
        CategoryFilter.Items.Add(new ComboBoxItem { Content = LanguageManager.GetString("MovieLib_Uncategorized"), Tag = -1 });
        foreach (var cat in categories) CategoryFilter.Items.Add(new ComboBoxItem { Content = cat.Name, Tag = cat.Id });
        CategoryFilter.SelectedIndex = 0;
    }

    private void PopulateYearFilter(List<Movie> allMovies)
    {
        YearFilter.Items.Clear();
        YearFilter.Items.Add(new ComboBoxItem { Content = LanguageManager.GetString("MovieLib_AllYears") });
        var years = allMovies.Where(m => m.Year > 0).Select(m => m.Year).Distinct().OrderByDescending(y => y).ToList();
        foreach (var year in years) YearFilter.Items.Add(new ComboBoxItem { Content = year.ToString(), Tag = year });
        YearFilter.SelectedIndex = 0;
    }

    private async Task LoadMoviesAsync()
    {
        var (keyword, categoryId, status) = GetFilterValues();
        var effectiveStatus = _quickFilterWatchlist ? WatchStatus.WantToWatch : status;
        var sortInfo = GetSortInfo();
        var year = GetYearFilter();
        var adv = GetAdvancedFilterValues();
        var (movies, total) = await _movieService.SearchAsync(
            keyword, categoryId, null,
            adv.yearFrom ?? year, adv.yearTo ?? year,
            adv.ratingMin, adv.ratingMax, effectiveStatus,
            adv.countries, adv.languages, adv.runtimeMin, adv.runtimeMax, adv.directors,
            sortInfo.sortBy, sortInfo.sortDesc, _currentPage, PageSize,
            _quickFilterFavorites ? true : null);
        _totalCount = total;
        if (_isCardView) RenderCardView(movies); else if (_isPosterView) PosterWall.ItemsSource = movies; else MovieDataGrid.ItemsSource = movies;
        var totalPages = (int)Math.Ceiling((double)total / PageSize);
        PageInfo.Text = string.Format(LanguageManager.GetString("Msg_PageInfo"), total, _currentPage, Math.Max(1, totalPages));
        PrevPageBtn.IsEnabled = _currentPage > 1;
        NextPageBtn.IsEnabled = _currentPage < totalPages;
        var hasMovies = movies.Any();
        MovieDataGrid.Visibility = !_isCardView && !_isPosterView && !_isCollectionView && hasMovies ? Visibility.Visible : Visibility.Collapsed;
        CardScrollViewer.Visibility = _isCardView && hasMovies ? Visibility.Visible : Visibility.Collapsed;
        PosterWall.Visibility = _isPosterView && hasMovies ? Visibility.Visible : Visibility.Collapsed;
        EmptyLabel.Visibility = hasMovies || _isCollectionView ? Visibility.Collapsed : Visibility.Visible;
        CollectionScrollViewer.Visibility = _isCollectionView ? Visibility.Visible : Visibility.Collapsed;

        if (_isPosterView) PosterWall.ScrollIntoView(PosterWall.Items[0]);
        else if (_isCardView) CardScrollViewer.ScrollToTop();
        else if (MovieDataGrid.Items.Count > 0) MovieDataGrid.ScrollIntoView(MovieDataGrid.Items[0]);

        if (_isFirstLoad && hasMovies)
        {
            _isFirstLoad = false;
            if (!_isCardView && !_isPosterView && MovieDataGrid.Items.Count > 0)
            {
                MovieDataGrid.SelectedIndex = 0;
                if (MovieDataGrid.Items[0] is Movie firstMovie)
                    _mainWindow?.ShowMovieDetail(firstMovie);
            }
            else if (movies.Count > 0)
                _mainWindow?.ShowMovieDetail(movies[0]);
        }
    }

    private (string? keyword, int? categoryId, WatchStatus? status) GetFilterValues()
    {
        string? keyword = string.IsNullOrWhiteSpace(SearchBox.Text) ? null : SearchBox.Text.Trim();
        int? categoryId = null;
        if (CategoryFilter.SelectedItem is ComboBoxItem ci && ci.Tag is int cid) categoryId = cid;
        WatchStatus? status = null;
        if (StatusFilter.SelectedItem is ComboBoxItem si && si.Tag is string st) status = st switch { "NotWatched" => WatchStatus.NotWatched, "WantToWatch" => WatchStatus.WantToWatch, "Watched" => WatchStatus.Watched, _ => null };
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

    /// <summary>高级筛选参数</summary>
    private record AdvancedFilterValues(
        int? yearFrom, int? yearTo, int? ratingMin, int? ratingMax,
        List<string>? countries, List<string>? languages, int? runtimeMin, int? runtimeMax, List<string>? directors);

    private AdvancedFilterValues GetAdvancedFilterValues()
    {
        int? yearFrom = YearRangeSlider.LowerValue > YearRangeSlider.Minimum ? (int)YearRangeSlider.LowerValue : null;
        int? yearTo = YearRangeSlider.UpperValue < YearRangeSlider.Maximum ? (int)YearRangeSlider.UpperValue : null;
        int? ratingMin = RatingRangeSlider.LowerValue > RatingRangeSlider.Minimum ? (int)RatingRangeSlider.LowerValue : null;
        int? ratingMax = RatingRangeSlider.UpperValue < RatingRangeSlider.Maximum ? (int)RatingRangeSlider.UpperValue : null;
        int? runtimeMin = RuntimeRangeSlider.LowerValue > RuntimeRangeSlider.Minimum ? (int)RuntimeRangeSlider.LowerValue : null;
        int? runtimeMax = RuntimeRangeSlider.UpperValue < RuntimeRangeSlider.Maximum ? (int)RuntimeRangeSlider.UpperValue : null;

        var countries = GetMultiSelectValues(CountryFilter);
        var languages = GetMultiSelectValues(LanguageFilter);
        var directors = GetMultiSelectValues(DirectorFilter);

        return new AdvancedFilterValues(yearFrom, yearTo, ratingMin, ratingMax, countries, languages, runtimeMin, runtimeMax, directors);
    }

    private static List<string>? GetMultiSelectValues(System.Windows.Controls.ListBox listBox)
    {
        var items = listBox.SelectedItems.Cast<ComboBoxItem>()
            .Where(ci => ci.Tag is string s && s != "_all")
            .Select(ci => (string)ci.Tag)
            .ToList();
        return items.Count > 0 ? items : null;
    }

    private void PopulateAdvancedFilterOptions(List<Movie> allMovies)
    {
        // 国家
        CountryFilter.Items.Clear();
        var countries = allMovies
            .Where(m => !string.IsNullOrWhiteSpace(m.Country))
            .SelectMany(m => m.Country!.Split('/', ' ', '·', ','))
            .Select(c => CleanHtmlFragment(c.Trim()))
            .Where(c => !string.IsNullOrEmpty(c) && IsValidCategoryName(c))
            .Distinct()
            .OrderBy(c => c)
            .ToList();
        foreach (var c in countries) CountryFilter.Items.Add(new ComboBoxItem { Content = c, Tag = c });

        // 语言
        LanguageFilter.Items.Clear();
        var languages = allMovies
            .Where(m => !string.IsNullOrWhiteSpace(m.Language))
            .SelectMany(m => m.Language!.Split('/', ' ', '·', ','))
            .Select(l => CleanHtmlFragment(l.Trim()))
            .Where(l => !string.IsNullOrEmpty(l))
            .Distinct()
            .OrderBy(l => l)
            .ToList();
        foreach (var l in languages) LanguageFilter.Items.Add(new ComboBoxItem { Content = l, Tag = l });

        // 导演
        DirectorFilter.Items.Clear();
        var directors = allMovies
            .Where(m => !string.IsNullOrWhiteSpace(m.Director))
            .SelectMany(m => m.Director!.Split('/', ','))
            .Select(d => CleanHtmlFragment(d.Trim()))
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct()
            .OrderBy(d => d)
            .ToList();
        foreach (var d in directors) DirectorFilter.Items.Add(new ComboBoxItem { Content = d, Tag = d });

        // 根据实际数据设置范围滑块的 Minimum/Maximum
        var currentYear = DateTime.Now.Year;
        var validYears = allMovies
            .Where(m => m.Year >= 1880 && m.Year <= currentYear + 1)
            .Select(m => (double)m.Year).ToList();
        if (validYears.Count > 0)
        {
            var minY = Math.Floor(validYears.Min() / 10.0) * 10;
            var maxY = Math.Min(currentYear, Math.Ceiling(validYears.Max() / 10.0) * 10);
            YearRangeSlider.Minimum = minY;
            YearRangeSlider.Maximum = maxY;
            YearRangeSlider.LowerValue = minY;
            YearRangeSlider.UpperValue = maxY;
        }

        RatingRangeSlider.Minimum = 0;
        RatingRangeSlider.Maximum = 10;
        RatingRangeSlider.LowerValue = 0;
        RatingRangeSlider.UpperValue = 10;

        var validRuntimes = allMovies.Where(m => m.Runtime > 0 && m.Runtime < 600).Select(m => (double)m.Runtime).ToList();
        if (validRuntimes.Count > 0)
        {
            var minRT = Math.Floor(validRuntimes.Min() / 30.0) * 30;
            var maxRT = Math.Ceiling(validRuntimes.Max() / 30.0) * 30;
            RuntimeRangeSlider.Minimum = minRT;
            RuntimeRangeSlider.Maximum = maxRT;
            RuntimeRangeSlider.LowerValue = minRT;
            RuntimeRangeSlider.UpperValue = maxRT;
        }

        // 加载已保存筛选列表
        LoadSavedFilterList();
    }

    /// <summary>清洗HTML标签碎片，如 "1338249-gary-dauberman'>加里·道伯曼<" → "加里·道伯曼"</summary>
    private static string CleanHtmlFragment(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        // 移除所有 HTML 标签 <...>（包括不完整的如 <a>、</a>）
        var result = System.Text.RegularExpressions.Regex.Replace(input, "</?[a-zA-Z][^>]*>", "");
        // 移除 HTML 属性残留，如 "123-name'>张三" 或 "123-name\">张三" → "张三"
        result = System.Text.RegularExpressions.Regex.Replace(result, @"[\d\-a-zA-Z_/]+['" + "\"" + @">]+", "");
        // 移除残留的引号、尖括号
        result = System.Text.RegularExpressions.Regex.Replace(result, "[<>\"']", "");
        result = result.Trim(' ', ',', '/', '-', '=');
        // 如果结果为空，跳过
        if (string.IsNullOrWhiteSpace(result)) return "";
        // 过滤掉看起来像 HTML 属性/URL 的值（纯英文数字-下划线串，且不含中文）
        if (System.Text.RegularExpressions.Regex.IsMatch(result, @"^[\d\-a-zA-Z_=./&?]+$"))
            return "";
        return result.Trim();
    }

    private void LoadSavedFilterList()
    {
        SavedFilterCombo.Items.Clear();
        SavedFilterCombo.Items.Add(new ComboBoxItem { Content = LanguageManager.GetString("MovieLib_LoadFilter"), Tag = "_placeholder" });
        var filters = SavedFilter.LoadAll();
        foreach (var f in filters) SavedFilterCombo.Items.Add(new ComboBoxItem { Content = f.Name, Tag = f.Name });
        SavedFilterCombo.SelectedIndex = 0;
        DeleteFilterBtn.Visibility = Visibility.Collapsed;
    }

    private async void ApplyAdvancedFilter_Click(object sender, RoutedEventArgs e)
    {
        _currentPage = 1;
        await LoadMoviesAsync();
    }

    private async void ResetAdvancedFilter_Click(object sender, RoutedEventArgs e)
    {
        YearRangeSlider.LowerValue = YearRangeSlider.Minimum;
        YearRangeSlider.UpperValue = YearRangeSlider.Maximum;
        RatingRangeSlider.LowerValue = RatingRangeSlider.Minimum;
        RatingRangeSlider.UpperValue = RatingRangeSlider.Maximum;
        RuntimeRangeSlider.LowerValue = RuntimeRangeSlider.Minimum;
        RuntimeRangeSlider.UpperValue = RuntimeRangeSlider.Maximum;
        CountryFilter.SelectedItems.Clear();
        LanguageFilter.SelectedItems.Clear();
        DirectorFilter.SelectedItems.Clear();
        _currentPage = 1;
        await LoadMoviesAsync();
    }

    private void SaveFilter_Click(object sender, RoutedEventArgs e)
    {
        var dlg = CreateThemedWindow(LanguageManager.GetString("Msg_SaveFilterTitle"), 350, 160);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = LanguageManager.GetString("Msg_FilterName") + "：", Margin = new Thickness(0, 0, 0, 8) });
        var nameBox = new TextBox { Style = (Style)Application.Current.FindResource("MaterialDesignFloatingHintTextBox") };
        MaterialDesignThemes.Wpf.HintAssist.SetHint(nameBox, LanguageManager.GetString("Msg_FilterName"));
        panel.Children.Add(nameBox);
        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancelBtn = new Button { Content = LanguageManager.GetString("Msg_Cancel"), Style = (Style)Application.Current.FindResource("MaterialDesignFlatButton"), Margin = new Thickness(0, 0, 8, 0) };
        cancelBtn.Click += (s, ev) => { dlg.Close(); };
        var saveBtn = new Button { Content = LanguageManager.GetString("CatTag_Save"), Style = (Style)Application.Current.FindResource("MaterialDesignRaisedButton") };
        saveBtn.Click += (s, ev) =>
        {
            var name = nameBox.Text?.Trim();
            if (string.IsNullOrEmpty(name)) { AppMessageBox.ShowInfo(LanguageManager.GetString("Msg_EnterName")); return; }
            var filter = new SavedFilter
            {
                Name = name,
                Keyword = SearchBox.Text?.Trim(),
                CategoryId = CategoryFilter.SelectedItem is ComboBoxItem ci && ci.Tag is int cid ? cid : (int?)null,
                Status = StatusFilter.SelectedItem is ComboBoxItem si && si.Tag is string st ? st : null,
                YearFrom = YearRangeSlider.LowerValue > YearRangeSlider.Minimum ? (int)YearRangeSlider.LowerValue : (int?)null,
                YearTo = YearRangeSlider.UpperValue < YearRangeSlider.Maximum ? (int)YearRangeSlider.UpperValue : (int?)null,
                RatingMin = RatingRangeSlider.LowerValue > RatingRangeSlider.Minimum ? (int)RatingRangeSlider.LowerValue : (int?)null,
                RatingMax = RatingRangeSlider.UpperValue < RatingRangeSlider.Maximum ? (int)RatingRangeSlider.UpperValue : (int?)null,
                Countries = CountryFilter.SelectedItems.Cast<ComboBoxItem>().Where(ci => ci.Tag is string).Select(ci => (string)ci.Tag).ToList(),
                Languages = LanguageFilter.SelectedItems.Cast<ComboBoxItem>().Where(ci => ci.Tag is string).Select(ci => (string)ci.Tag).ToList(),
                RuntimeMin = RuntimeRangeSlider.LowerValue > RuntimeRangeSlider.Minimum ? (int)RuntimeRangeSlider.LowerValue : (int?)null,
                RuntimeMax = RuntimeRangeSlider.UpperValue < RuntimeRangeSlider.Maximum ? (int)RuntimeRangeSlider.UpperValue : (int?)null,
                Directors = DirectorFilter.SelectedItems.Cast<ComboBoxItem>().Where(ci => ci.Tag is string).Select(ci => (string)ci.Tag).ToList(),
                SortBy = GetSortInfo().sortBy,
                SortDesc = GetSortInfo().sortDesc
            };
            var filters = SavedFilter.LoadAll();
            filters.Add(filter);
            SavedFilter.SaveAll(filters);
            dlg.Close();
            LoadSavedFilterList();
            AppMessageBox.ShowInfo(LanguageManager.GetString("Msg_FilterSaved"));
        };
        btnPanel.Children.Add(cancelBtn);
        btnPanel.Children.Add(saveBtn);
        panel.Children.Add(btnPanel);
        dlg.Content = panel;
        dlg.ShowDialog();
    }

    /// <summary>保存的筛选条件</summary>
    private class SavedFilter
    {
        public string Name { get; set; } = "";
        public string? Keyword { get; set; }
        public int? CategoryId { get; set; }
        public string? Status { get; set; }
        public int? YearFrom { get; set; }
        public int? YearTo { get; set; }
        public int? RatingMin { get; set; }
        public int? RatingMax { get; set; }
        public List<string>? Countries { get; set; }
        public List<string>? Languages { get; set; }
        public int? RuntimeMin { get; set; }
        public int? RuntimeMax { get; set; }
        public List<string>? Directors { get; set; }
        public string? SortBy { get; set; }
        public bool SortDesc { get; set; }

        private static readonly string SavePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EasyMovie", "saved_filters.json");

        public static List<SavedFilter> LoadAll()
        {
            try
            {
                if (!File.Exists(SavePath)) return new List<SavedFilter>();
                var json = File.ReadAllText(SavePath);
                return System.Text.Json.JsonSerializer.Deserialize<List<SavedFilter>>(json) ?? new List<SavedFilter>();
            }
            catch { return new List<SavedFilter>(); }
        }

        public static void SaveAll(List<SavedFilter> filters)
        {
            try
            {
                var dir = Path.GetDirectoryName(SavePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var json = System.Text.Json.JsonSerializer.Serialize(filters, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SavePath, json);
            }
            catch { }
        }
    }

    private void SavedFilterCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SavedFilterCombo.SelectedItem is not ComboBoxItem ci || ci.Tag is not string name || name == "_placeholder") return;
        var filter = SavedFilter.LoadAll().FirstOrDefault(f => f.Name == name);
        if (filter == null) return;

        // 应用筛选条件
        SearchBox.Text = filter.Keyword ?? "";
        if (filter.CategoryId.HasValue)
        {
            foreach (var item in CategoryFilter.Items)
                if (item is ComboBoxItem cci && cci.Tag is int cid && cid == filter.CategoryId.Value)
                    { CategoryFilter.SelectedItem = cci; break; }
        }
        else CategoryFilter.SelectedIndex = 0;

        if (filter.Status != null)
        {
            foreach (var item in StatusFilter.Items)
                if (item is ComboBoxItem si && si.Tag is string st && st == filter.Status)
                    { StatusFilter.SelectedItem = si; break; }
        }
        else StatusFilter.SelectedIndex = 0;

        YearRangeSlider.LowerValue = filter.YearFrom ?? YearRangeSlider.Minimum;
        YearRangeSlider.UpperValue = filter.YearTo ?? YearRangeSlider.Maximum;
        RatingRangeSlider.LowerValue = filter.RatingMin ?? RatingRangeSlider.Minimum;
        RatingRangeSlider.UpperValue = filter.RatingMax ?? RatingRangeSlider.Maximum;
        RuntimeRangeSlider.LowerValue = filter.RuntimeMin ?? RuntimeRangeSlider.Minimum;
        RuntimeRangeSlider.UpperValue = filter.RuntimeMax ?? RuntimeRangeSlider.Maximum;

        // 多选
        ApplyMultiSelect(CountryFilter, filter.Countries);
        ApplyMultiSelect(LanguageFilter, filter.Languages);
        ApplyMultiSelect(DirectorFilter, filter.Directors);

        DeleteFilterBtn.Visibility = Visibility.Visible;
        _currentPage = 1;
        _ = LoadMoviesAsync();
    }

    private static void ApplyMultiSelect(System.Windows.Controls.ListBox listBox, List<string>? values)
    {
        listBox.SelectedItems.Clear();
        if (values == null || values.Count == 0) return;
        foreach (var item in listBox.Items)
        {
            if (item is ComboBoxItem ci && ci.Tag is string tag && values.Contains(tag))
                listBox.SelectedItems.Add(ci);
        }
    }

    private void DeleteFilter_Click(object sender, RoutedEventArgs e)
    {
        if (SavedFilterCombo.SelectedItem is not ComboBoxItem ci || ci.Tag is not string name || name == "_placeholder") return;
        if (!AppMessageBox.Confirm(string.Format(LanguageManager.GetString("Msg_ConfirmDeleteFilter") ?? "Delete filter '{0}'?", name),
            LanguageManager.GetString("Msg_Confirm"))) return;
        var filters = SavedFilter.LoadAll();
        filters.RemoveAll(f => f.Name == name);
        SavedFilter.SaveAll(filters);
        LoadSavedFilterList();
    }

    private void RangeSlider_RangeChanged(object sender, RoutedEventArgs e)
    {
        // 范围滑块值变化时的回调（可用于实时筛选）
    }

    private async Task LoadRecommendationsAsync()
    {
        try
        {
            var recommendations = await _recommendationService.GetRecommendationsAsync(20);
            if (recommendations.Count == 0)
            {
                AppMessageBox.ShowInfo(LanguageManager.GetString("Msg_NoRecommendData"), LanguageManager.GetString("Msg_Hint"));
                return;
            }

            var ownerWindow = Window.GetWindow(this) ?? Application.Current.MainWindow;
            var dlg = new Window
            {
                Title = LanguageManager.GetString("Msg_RecommendTitle"),
                Width = 1200,
                Height = 580,
                WindowStartupLocation = ownerWindow != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                Owner = ownerWindow,
                ResizeMode = ResizeMode.CanResizeWithGrip,
                Background = (Brush)Application.Current.FindResource("MaterialDesignPaper")
            };

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 标题栏
            var header = new DockPanel { Margin = new Thickness(16, 12, 16, 0) };
            var titlePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            titlePanel.Children.Add(new PackIcon { Kind = PackIconKind.StarShooting, Width = 22, Height = 22, Margin = new Thickness(0, 0, 8, 0), Foreground = new SolidColorBrush(Color.FromRgb(121, 134, 203)) });
            titlePanel.Children.Add(new TextBlock { Text = LanguageManager.GetString("Msg_RecommendTitle"), FontSize = 20, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
            header.Children.Add(titlePanel);
            var hint = new TextBlock { Text = "  " + LanguageManager.GetString("Msg_RecommendHint"), FontSize = 12, Foreground = SafeFindBrush("MaterialDesignHintForeground", Color.FromRgb(117, 117, 117)), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(hint, Dock.Right);
            header.Children.Add(hint);
            root.Children.Add(header);
            Grid.SetRow(header, 0);

            // 海报墙区域
            var wallPanel = new Grid { Margin = new Thickness(4, 4, 4, 8) };
            wallPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            wallPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            wallPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 左箭头 - 垂直居中，圆形按钮
            var leftBtn = new Button
            {
                Style = (Style)Application.Current.FindResource("MaterialDesignIconButton"),
                Content = new PackIcon { Kind = PackIconKind.ChevronLeft, Width = 36, Height = 36 },
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Width = 48, Height = 48
            };
            Grid.SetColumn(leftBtn, 0);
            wallPanel.Children.Add(leftBtn);

            // 海报墙 ScrollViewer
            var posterScroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                CanContentScroll = false,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            var posterWrap = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var rec in recommendations)
            {
                posterWrap.Children.Add(BuildPosterCard(rec));
            }
            posterScroll.Content = posterWrap;
            Grid.SetColumn(posterScroll, 1);
            wallPanel.Children.Add(posterScroll);

            // 右箭头 - 垂直居中，圆形按钮
            var rightBtn = new Button
            {
                Style = (Style)Application.Current.FindResource("MaterialDesignIconButton"),
                Content = new PackIcon { Kind = PackIconKind.ChevronRight, Width = 36, Height = 36 },
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Width = 48, Height = 48
            };
            Grid.SetColumn(rightBtn, 2);
            wallPanel.Children.Add(rightBtn);

            // 左右按钮滚动
            leftBtn.Click += (s, e) => posterScroll.ScrollToHorizontalOffset(posterScroll.HorizontalOffset - 360);
            rightBtn.Click += (s, e) => posterScroll.ScrollToHorizontalOffset(posterScroll.HorizontalOffset + 360);
            // 鼠标滚轮横向滚动
            posterScroll.PreviewMouseWheel += (s, e) =>
            {
                posterScroll.ScrollToHorizontalOffset(posterScroll.HorizontalOffset - e.Delta);
                e.Handled = true;
            };

            root.Children.Add(wallPanel);
            Grid.SetRow(wallPanel, 1);
            dlg.Content = root;
            dlg.ShowDialog();
        }
        catch { }
    }

    private Border BuildPosterCard(RecommendedMovie rec)
    {
        var movie = rec.Movie;
        var dividerBrush = SafeFindBrush("MaterialDesignDivider", Color.FromRgb(48, 48, 48));
        var hintBrush = SafeFindBrush("MaterialDesignHintForeground", Color.FromRgb(117, 117, 117));
        var bodyBrush = SafeFindBrush("MaterialDesignBody", Colors.White);

        var card = new Border
        {
            Width = 200,
            Margin = new Thickness(6, 6, 6, 6),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Background = (Brush)Application.Current.FindResource("MaterialDesignCardBackground"),
            Tag = movie
        };

        var stack = new StackPanel();

        // 海报区域（带播放按钮叠加）
        var posterGrid = new Grid { Height = 260 };

        var posterBorder = new Border { ClipToBounds = true, CornerRadius = new CornerRadius(8, 8, 0, 0) };
        var img = new Image { Stretch = Stretch.UniformToFill, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

        if (movie.PosterData != null && movie.PosterData.Length > 0)
        {
            try
            {
                var bitmap = new BitmapImage();
                using var ms = new MemoryStream(movie.PosterData);
                bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = ms; bitmap.EndInit(); bitmap.Freeze();
                img.Source = bitmap;
            }
            catch { }
        }

        if (img.Source != null)
        {
            posterBorder.Child = img;
        }
        else
        {
            var ph = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Background = dividerBrush };
            ph.Children.Add(new PackIcon { Kind = PackIconKind.MovieOpen, Width = 40, Height = 40, HorizontalAlignment = HorizontalAlignment.Center, Foreground = hintBrush });
            posterBorder.Child = ph;
        }

        posterGrid.Children.Add(posterBorder);

        // 播放按钮叠加层
        if (!string.IsNullOrEmpty(movie.FilePath))
        {
            var playBg = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0));
            var playBgHover = new SolidColorBrush(Color.FromArgb(220, 121, 134, 203));
            var playOverlay = new Border
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Width = 44, Height = 44,
                CornerRadius = new CornerRadius(22),
                Background = playBg,
                Cursor = System.Windows.Input.Cursors.Hand,
                RenderTransform = new ScaleTransform(1, 1),
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            var playIcon = new PackIcon { Kind = PackIconKind.Play, Width = 22, Height = 22, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var playContainer = new Grid();
            playContainer.Children.Add(playIcon);
            playOverlay.Child = playContainer;
            playOverlay.MouseEnter += (s, e) => { playOverlay.Background = playBgHover; playOverlay.RenderTransform = new ScaleTransform(1.15, 1.15); };
            playOverlay.MouseLeave += (s, e) => { playOverlay.Background = playBg; playOverlay.RenderTransform = new ScaleTransform(1, 1); };
            playOverlay.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                if (!File.Exists(movie.FilePath))
                    AppMessageBox.ShowWarning(string.Format(LanguageManager.GetString("Msg_FileNotFound"), movie.FilePath), LanguageManager.GetString("Msg_Hint"));
                else
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(movie.FilePath) { UseShellExecute = true }); }
                    catch (Exception ex) { AppMessageBox.ShowError(LanguageManager.GetString("Msg_PlayFailed") + ex.Message); }
            };
            posterGrid.Children.Add(playOverlay);
        }

        // 底部渐变标题条
        var infoBar = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Padding = new Thickness(8, 6, 8, 6)
        };
        var gradBrush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        gradBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0));
        gradBrush.GradientStops.Add(new GradientStop(Color.FromArgb(180, 0, 0, 0), 0.5));
        gradBrush.GradientStops.Add(new GradientStop(Color.FromArgb(230, 0, 0, 0), 1));
        infoBar.Background = gradBrush;

        var infoStack = new StackPanel();
        infoStack.Children.Add(new TextBlock { Text = movie.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
        var metaRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        if (movie.Year > 0) metaRow.Children.Add(new TextBlock { Text = movie.Year.ToString(), FontSize = 10, Foreground = ColorToBrush(Color.FromArgb(187, 255, 255, 255)), Margin = new Thickness(0, 0, 8, 0) });
        if (movie.Rating.HasValue) metaRow.Children.Add(new TextBlock { Text = "⭐" + movie.Rating, FontSize = 10, Foreground = Brushes.Gold });
        infoStack.Children.Add(metaRow);
        infoBar.Child = infoStack;
        posterGrid.Children.Add(infoBar);

        stack.Children.Add(posterGrid);

        // 详细信息区域（和主界面左侧详情一致）
        var detailPanel = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };

        if (!string.IsNullOrEmpty(movie.OriginalTitle))
            detailPanel.Children.Add(new TextBlock { Text = movie.OriginalTitle, FontSize = 10, Foreground = hintBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });

        // 年份/时长/评分
        var metaLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        var yearSuffix = LanguageManager.GetString("Msg_YearSuffix");
        var minSuffix = LanguageManager.GetString("Msg_MinuteSuffix");
        if (movie.Year > 0) metaLine.Children.Add(new TextBlock { Text = movie.Year + yearSuffix, FontSize = 11, Foreground = bodyBrush, Margin = new Thickness(0, 0, 8, 0) });
        if (movie.Runtime.HasValue) metaLine.Children.Add(new TextBlock { Text = movie.Runtime + minSuffix, FontSize = 11, Foreground = bodyBrush, Margin = new Thickness(0, 0, 8, 0) });
        if (movie.Rating.HasValue) metaLine.Children.Add(new TextBlock { Text = "⭐" + movie.Rating, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(255, 215, 0)) });
        if (metaLine.Children.Count > 0) detailPanel.Children.Add(metaLine);

        if (!string.IsNullOrEmpty(movie.Director))
            detailPanel.Children.Add(new TextBlock { Text = "🎬 " + movie.Director, FontSize = 11, Foreground = bodyBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2) });
        if (!string.IsNullOrEmpty(movie.Country))
            detailPanel.Children.Add(new TextBlock { Text = "🌍 " + movie.Country, FontSize = 11, Foreground = bodyBrush, Margin = new Thickness(0, 0, 0, 2) });
        if (!string.IsNullOrEmpty(movie.Cast))
            detailPanel.Children.Add(new TextBlock { Text = "🎭 " + movie.Cast, FontSize = 11, Foreground = bodyBrush, TextWrapping = TextWrapping.Wrap, MaxHeight = 36, Margin = new Thickness(0, 0, 0, 2) });

        // 观看状态
        var statusText = movie.WatchStatus switch
        {
            WatchStatus.WantToWatch => LanguageManager.GetString("WatchStatus_WantToWatch"),
            WatchStatus.Watched => LanguageManager.GetString("WatchStatus_Watched"),
            _ => ""
        };
        if (!string.IsNullOrEmpty(statusText))
            detailPanel.Children.Add(new TextBlock { Text = statusText, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(255, 215, 0)), Margin = new Thickness(0, 0, 0, 2) });

        // 简介
        if (!string.IsNullOrEmpty(movie.Synopsis))
            detailPanel.Children.Add(new TextBlock { Text = movie.Synopsis, FontSize = 10, Foreground = hintBrush, TextWrapping = TextWrapping.Wrap, MaxHeight = 48, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });

        // 推荐理由
        if (!string.IsNullOrEmpty(rec.Reason))
        {
            var reasonBadge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(40, 121, 134, 203)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            reasonBadge.Child = new TextBlock { Text = rec.Reason, FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(121, 134, 203)), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 160 };
            detailPanel.Children.Add(reasonBadge);
        }

        stack.Children.Add(detailPanel);
        card.Child = stack;

        // 点击卡片显示主界面详情
        card.MouseLeftButtonUp += (s, e) =>
        {
            if (e.Handled) return;
            _mainWindow?.ShowMovieDetail(movie);
        };

        // 异步加载远程海报
        if (img.Source == null && !string.IsNullOrEmpty(movie.PosterUrl))
        {
            _ = LoadPosterAsync(img, posterBorder, movie.PosterUrl, dividerBrush);
        }

        return card;
    }

    private async Task LoadPosterAsync(Image img, Border posterBorder, string posterUrl, Brush fallbackBg)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var bytes = await http.GetByteArrayAsync(posterUrl);
            var bmp = new BitmapImage();
            bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = new MemoryStream(bytes); bmp.EndInit(); bmp.Freeze();
            img.Source = bmp;
            if (posterBorder.Child is not Image) posterBorder.Child = img;
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

    private async void RecommendToggle_Click(object sender, RoutedEventArgs e)
    {
        await LoadRecommendationsAsync();
    }

    private static SolidColorBrush ColorToBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void MovieDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateBatchPanel();
        if (MovieDataGrid.SelectedItems.Count == 1 && MovieDataGrid.SelectedItem is Movie movie)
            _mainWindow?.ShowMovieDetail(movie);
    }

    private void RenderCardView(List<Movie> movies)
    {
        _selectedCardIds.Clear();
        CardPanel.Children.Clear();
        var hintFg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(117, 117, 117));
        foreach (var movie in movies)
        {
            var card = new Card { Width = 220, Height = 380, Margin = new Thickness(8), Cursor = System.Windows.Input.Cursors.Hand };
            var stack = new StackPanel();

            // 封面区域
            var posterBorder = new Border { Height = 240, CornerRadius = new CornerRadius(4, 4, 0, 0), ClipToBounds = true };
            if (movie.PosterData is { Length: > 0 })
            {
                try
                {
                    var image = new System.Windows.Controls.Image
                    {
                        Stretch = Stretch.UniformToFill,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.StreamSource = new System.IO.MemoryStream(movie.PosterData);
                    bmp.EndInit();
                    bmp.Freeze();
                    image.Source = bmp;
                    posterBorder.Child = image;
                }
                catch { posterBorder.Background = System.Windows.Media.Brushes.Gray; }
            }
            else
            {
                posterBorder.Background = System.Windows.Media.Brushes.Gray;
                var placeholder = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                placeholder.Children.Add(new MaterialDesignThemes.Wpf.PackIcon { Kind = MaterialDesignThemes.Wpf.PackIconKind.Movie, Width = 40, Height = 40, HorizontalAlignment = HorizontalAlignment.Center, Foreground = System.Windows.Media.Brushes.White });
                placeholder.Children.Add(new TextBlock { Text = movie.Title, FontSize = 11, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxHeight = 50, Margin = new Thickness(4), Foreground = System.Windows.Media.Brushes.White });
                posterBorder.Child = placeholder;
            }
            stack.Children.Add(posterBorder);

            // 信息区域
            var infoPanel = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };
            infoPanel.Children.Add(new TextBlock { Text = movie.Title, FontWeight = FontWeights.Bold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis });

            // 年份 + 评分
            var line2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            if (movie.Year > 0) line2.Children.Add(new TextBlock { Text = movie.Year.ToString(), FontSize = 12, Foreground = hintFg, Margin = new Thickness(0, 0, 8, 0) });
            if (movie.Rating.HasValue) line2.Children.Add(new TextBlock { Text = "⭐" + movie.Rating, FontSize = 12, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Goldenrod) });
            infoPanel.Children.Add(line2);

            // 导演
            if (!string.IsNullOrEmpty(movie.Director))
                infoPanel.Children.Add(new TextBlock { Text = "🎬 " + movie.Director, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = hintFg, Margin = new Thickness(0, 2, 0, 0) });

            // 分类 + 状态
            var line3 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            if (movie.Category != null)
            {
                var catBadge = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 4, 0), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(121, 134, 203)) };
                catBadge.Child = new TextBlock { Text = movie.Category.Name, FontSize = 10, Foreground = System.Windows.Media.Brushes.White };
                line3.Children.Add(catBadge);
            }
            var st = movie.WatchStatus switch { WatchStatus.WantToWatch => "想看", WatchStatus.Watched => "已看", _ => "" };
            if (!string.IsNullOrEmpty(st))
            {
                var statusColor = movie.WatchStatus switch { WatchStatus.Watched => System.Windows.Media.Colors.Green, _ => System.Windows.Media.Colors.Orange };
                var statusBadge = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2), Background = new System.Windows.Media.SolidColorBrush(statusColor) };
                statusBadge.Child = new TextBlock { Text = st, FontSize = 10, Foreground = System.Windows.Media.Brushes.White };
                line3.Children.Add(statusBadge);
            }
            if (movie.IsFavorite)
            {
                var favBadge = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(4, 0, 0, 0), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Goldenrod) };
                favBadge.Child = new TextBlock { Text = "⭐收藏", FontSize = 10, Foreground = System.Windows.Media.Brushes.White };
                line3.Children.Add(favBadge);
            }
            infoPanel.Children.Add(line3);

            stack.Children.Add(infoPanel);
            card.Content = stack;
            card.MouseLeftButtonUp += (s, e) =>
            {
                if (System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
                {
                    if (_selectedCardIds.Contains(movie.Id))
                        _selectedCardIds.Remove(movie.Id);
                    else
                        _selectedCardIds.Add(movie.Id);
                    UpdateBatchPanel();
                }
                else if (_selectedCardIds.Count > 0 && _selectedCardIds.Contains(movie.Id))
                {
                    _selectedCardIds.Remove(movie.Id);
                    UpdateBatchPanel();
                }
                else
                {
                    _selectedCardIds.Clear();
                    _mainWindow?.ShowMovieDetail(movie);
                    OpenDetailView(movie.Id);
                }
            };
            card.DataContext = movie;
            card.Tag = movie.Id;
            CardPanel.Children.Add(card);
        }
    }

    private static Window CreateThemedWindow(string title, double width, double height)
    {
        return new Window
        {
            Title = title,
            Width = width,
            Height = height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(Application.Current.MainWindow),
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("MaterialDesignPaper")
        };
    }

    private void OpenDetailView(int movieId)
    {
        var detailView = new MovieDetailView(movieId, _movieService, _categoryService, _tagService);
        detailView.MovieSaved += async (s, e) => await LoadMoviesAsync();
        detailView.MovieAdded += async (s, id) => await FetchMovieInfoAsync(id);
        detailView.MovieDeleted += async (s, e) => await LoadMoviesAsync();
        var w = CreateThemedWindow("电影详情", 700, 780);
        w.Content = detailView;
        w.ResizeMode = ResizeMode.CanResize;
        w.ShowDialog();
    }

    private async Task FetchMovieInfoAsync(int movieId)
    {
        var m = await _movieService.GetByIdAsync(movieId);
        if (m == null || string.IsNullOrWhiteSpace(m.Title)) return;

        _mainWindow?.SetStatus($"🔍 {LanguageManager.GetString("Msg_FetchingInfo")}: {m.Title}", true);
        try
        {
            var douban = new DoubanApiClient();
            var maoyan = new MaoyanApiClient();
            var tmdb = new TmdbApiClient();

            var chineseKw = DoubanApiClient.ExtractChineseKeyword(m.Title);
            var engHint = DoubanApiClient.ExtractEnglishHint(m.Title);

            var searchKeywords = new List<string>();
            if (!string.IsNullOrWhiteSpace(chineseKw)) searchKeywords.Add(chineseKw);
            if (!string.IsNullOrWhiteSpace(engHint) && engHint != chineseKw) searchKeywords.Add(engHint);
            if (!searchKeywords.Contains(m.Title)) searchKeywords.Add(m.Title);

            MovieSearchResult? info = null;

            foreach (var kw in searchKeywords)
            {
                if (info != null) break;

                var sr = await douban.SearchAsync(new MovieSearchRequest { Keyword = kw, Page = 1, PageSize = 5 });
                if (sr.Results.Count > 0)
                {
                    MovieSearchResult? best = null;
                    if (!string.IsNullOrEmpty(engHint))
                        foreach (var r in sr.Results)
                            if (!string.IsNullOrEmpty(r.OriginalTitle) && r.OriginalTitle.Contains(engHint, StringComparison.OrdinalIgnoreCase)) { best = r; break; }
                    if (best == null && m.Year > 0)
                        best = sr.Results.FirstOrDefault(r => r.Year == m.Year);
                    if (best == null) best = sr.Results[0];
                    info = await douban.GetDetailAsync(best.ExternalId ?? "") ?? best;
                }
            }

            if (info == null)
            {
                foreach (var kw in searchKeywords)
                {
                    if (info != null) break;
                    var sr2 = await maoyan.SearchAsync(new MovieSearchRequest { Keyword = kw, Page = 1, PageSize = 3 });
                    if (sr2.Results.Count > 0)
                    {
                        var detail = await maoyan.GetDetailAsync(sr2.Results[0].ExternalId ?? "");
                        info = detail ?? sr2.Results[0];
                    }
                }
            }

            if (info == null)
            {
                var tmdbKw = !string.IsNullOrWhiteSpace(engHint) ? engHint : searchKeywords.FirstOrDefault() ?? m.Title;
                var sr3 = await tmdb.SearchAsync(new MovieSearchRequest { Keyword = tmdbKw, Page = 1, PageSize = 5 });
                if (sr3.Results.Count > 0)
                {
                    MovieSearchResult? best = null;
                    if (m.Year > 0)
                        best = sr3.Results.FirstOrDefault(r => r.Year == m.Year);
                    if (best == null) best = sr3.Results[0];
                    info = await tmdb.GetDetailAsync(best.ExternalId ?? "") ?? best;
                }
            }

            if (info != null)
            {
                if (!string.IsNullOrEmpty(info.Director) && string.IsNullOrEmpty(m.Director)) m.Director = info.Director;
                if (!string.IsNullOrEmpty(info.Cast) && string.IsNullOrEmpty(m.Cast)) m.Cast = info.Cast;
                if (!string.IsNullOrEmpty(info.Country) && string.IsNullOrEmpty(m.Country)) m.Country = info.Country;
                if (!string.IsNullOrEmpty(info.Synopsis) && string.IsNullOrEmpty(m.Synopsis)) m.Synopsis = info.Synopsis;
                if (!string.IsNullOrEmpty(info.PosterUrl) && string.IsNullOrEmpty(m.PosterUrl))
                {
                    m.PosterUrl = info.PosterUrl;
                    try
                    {
                        var imgClient = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(10) };
                        imgClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
                        if (info.PosterUrl.Contains("themoviedb.org") || info.PosterUrl.Contains("tmdb.org"))
                            imgClient.DefaultRequestHeaders.Add("Referer", "https://www.themoviedb.org/");
                        else if (info.PosterUrl.Contains("douban"))
                            imgClient.DefaultRequestHeaders.Add("Referer", "https://movie.douban.com/");
                        m.PosterData = await imgClient.GetByteArrayAsync(info.PosterUrl);
                    }
                    catch { }
                }
                if (info.Runtime.HasValue && !m.Runtime.HasValue) m.Runtime = info.Runtime;
                if (info.Year > 0 && m.Year == 0) m.Year = info.Year;
                if (!string.IsNullOrEmpty(info.OriginalTitle) && string.IsNullOrEmpty(m.OriginalTitle)) m.OriginalTitle = info.OriginalTitle;
                if (info.Source == "douban") m.DoubanId = info.ExternalId;
                else if (info.Source == "tmdb") m.TmdbId = info.ExternalId;

                if (!string.IsNullOrEmpty(info.Country) && !m.CategoryId.HasValue)
                {
                    var firstCountry = info.Country.Split('/', ' ', '·').FirstOrDefault(c => IsValidCategoryName(c.Trim()))?.Trim();
                    if (!string.IsNullOrEmpty(firstCountry) && IsValidCategoryName(firstCountry))
                    {
                        try { var category = await _categoryService.GetOrCreateByNameAsync(firstCountry); m.CategoryId = category.Id; } catch { }
                    }
                }

                await _movieService.UpdateAsync(m);
                _mainWindow?.ShowMovieDetail(m);
            }
        }
        catch { }
        finally
        {
            await LoadMoviesAsync();
            _mainWindow?.SetStatus($"✅ {m.Title}");
        }
    }

    private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { _currentPage = 1; await LoadMoviesAsync(); }
    private async void Filter_Changed(object sender, SelectionChangedEventArgs e) { _currentPage = 1; await LoadMoviesAsync(); }

    private async void QuickFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_movieService == null) return;
        _quickFilterFavorites = QuickFilterFavorites.IsChecked == true;
        _quickFilterWatchlist = QuickFilterWatchlist.IsChecked == true;
        _currentPage = 1;
        await LoadMoviesAsync();
    }
    private async void TableViewBtn_Click(object sender, RoutedEventArgs e) { _isCardView = false; _isPosterView = false; _isCollectionView = false; UpdateViewButtons(); await LoadMoviesAsync(); }
    private async void CardViewBtn_Click(object sender, RoutedEventArgs e) { _isCardView = true; _isPosterView = false; _isCollectionView = false; UpdateViewButtons(); await LoadMoviesAsync(); }
    private async void PosterViewBtn_Click(object sender, RoutedEventArgs e) { _isCardView = false; _isPosterView = true; _isCollectionView = false; UpdateViewButtons(); await LoadMoviesAsync(); }
    private async void CollectionView_Click(object sender, RoutedEventArgs e) { _isCardView = false; _isPosterView = false; _isCollectionView = true; UpdateViewButtons(); await LoadCollectionViewAsync(); }

    private void UpdateViewButtons()
    {
        var selectedBg = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0));
        TableViewBtn.Background = !_isCardView && !_isPosterView && !_isCollectionView ? selectedBg : Brushes.Transparent;
        CardViewBtn.Background = _isCardView ? selectedBg : Brushes.Transparent;
        PosterViewBtn.Background = _isPosterView ? selectedBg : Brushes.Transparent;
    }
    private void AddMovie_Click(object sender, RoutedEventArgs e) => OpenDetailView(0);

    private void OnlineSearch_Click(object sender, RoutedEventArgs e)
    {
        var sv = new OnlineSearchView();
        sv.MovieAdded += async (s, ev) => await LoadMoviesAsync();
        var w = CreateThemedWindow("在线搜索", 800, 650);
        w.Content = sv;
        w.ShowDialog();
    }

    private void MovieDataGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) { if (MovieDataGrid.SelectedItem is Movie m) OpenDetailView(m.Id); }

    private void MovieDataGrid_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null)
        {
            if (dep is DataGridRow row)
            {
                if (System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
                    row.IsSelected = !row.IsSelected;
                else if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Shift)
                {
                    grid.SelectedItem = row.Item;
                }
                return;
            }
            if (dep is Visual v) dep = VisualTreeHelper.GetParent(v);
            else break;
        }
    }
    private void PosterWall_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) { if (PosterWall.SelectedItem is Movie m) OpenDetailView(m.Id); }
    private void PosterWall_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateBatchPanel();
        if (PosterWall.SelectedItems.Count == 1 && PosterWall.SelectedItem is Movie movie)
            _mainWindow?.ShowMovieDetail(movie);
    }
    private void EditMovie_Click(object sender, RoutedEventArgs e) { if (sender is Button b && b.Tag is int id) OpenDetailView(id); }
    private async void DeleteMovie_Click(object sender, RoutedEventArgs e) { if (sender is Button b && b.Tag is int id && AppMessageBox.Confirm("确定删除？", "确认")) { await _movieService.DeleteAsync(id); await LoadMoviesAsync(); } }

    private async void FavoriteToggle_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBlock tb && tb.Tag is int id)
        {
            var movie = await _movieService.GetByIdAsync(id);
            if (movie != null)
            {
                movie.IsFavorite = !movie.IsFavorite;
                await _movieService.UpdateAsync(movie);
                tb.Text = movie.IsFavorite ? "★" : "☆";
                _mainWindow?.ShowMovieDetail(movie);
            }
        }
    }

    private async void WatchStatusToggle_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBlock tb && tb.Tag is int id)
        {
            var movie = await _movieService.GetByIdAsync(id);
            if (movie != null)
            {
                movie.WatchStatus = movie.WatchStatus switch
                {
                    WatchStatus.NotWatched => WatchStatus.WantToWatch,
                    WatchStatus.WantToWatch => WatchStatus.Watched,
                    WatchStatus.Watched => WatchStatus.NotWatched,
                    _ => WatchStatus.NotWatched
                };
                if (movie.WatchStatus == WatchStatus.Watched) movie.WatchDate = DateTime.Today;
                else movie.WatchDate = null;
                await _movieService.UpdateAsync(movie);
                // 切换到已看时自动添加观影记录
                if (movie.WatchStatus == WatchStatus.Watched)
                {
                    var existingLog = await _context.WatchLogs
                        .AnyAsync(w => w.MovieId == movie.Id && w.WatchDate.Date == DateTime.Today);
                    if (!existingLog)
                    {
                        _context.WatchLogs.Add(new WatchLog { MovieId = movie.Id, WatchDate = DateTime.Today });
                        await _context.SaveChangesAsync();
                    }
                }
                tb.Text = movie.WatchStatus switch
                {
                    WatchStatus.NotWatched => "未看",
                    WatchStatus.WantToWatch => "🕐 想看",
                    WatchStatus.Watched => "✅ 已看",
                    _ => ""
                };
                tb.Foreground = movie.WatchStatus switch
                {
                    WatchStatus.NotWatched => new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                    WatchStatus.WantToWatch => new SolidColorBrush(Color.FromRgb(0x26, 0xA6, 0x9A)),
                    WatchStatus.Watched => new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A)),
                    _ => Brushes.Gray
                };
                _mainWindow?.ShowMovieDetail(movie);
            }
        }
    }

    private async void PlayMovie_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not int id) return;
        var m = await _movieService.GetByIdAsync(id);
        if (m == null) return;
        if (string.IsNullOrEmpty(m.FilePath)) { AppMessageBox.ShowInfo(LanguageManager.GetString("Msg_NoFilePath"), LanguageManager.GetString("Msg_Hint")); return; }
        if (!File.Exists(m.FilePath)) { AppMessageBox.ShowWarning(string.Format(LanguageManager.GetString("Msg_FileNotFound"), m.FilePath), LanguageManager.GetString("Msg_Hint")); return; }

        // 每次播放都标记为已看并更新观影日期
        if (m.WatchStatus != WatchStatus.Watched)
        {
            m.WatchStatus = WatchStatus.Watched;
        }
        m.WatchDate = DateTime.Today;
        await _movieService.UpdateAsync(m);

        // 每次播放都添加观影记录，日历可显示
        var existingLog = await _context.WatchLogs
            .AnyAsync(w => w.MovieId == m.Id && w.WatchDate.Date == DateTime.Today);
        if (!existingLog)
        {
            _context.WatchLogs.Add(new WatchLog { MovieId = m.Id, WatchDate = DateTime.Today });
            await _context.SaveChangesAsync();
        }
        await LoadMoviesAsync();

        _mainWindow?.ShowMovieDetail(m);
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = m.FilePath, UseShellExecute = true }); }
        catch { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = m.FilePath, UseShellExecute = true }); }
    }

    private async void FetchInfo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is int id)
        {
            b.IsEnabled = false;
            _mainWindow?.SetStatus("获取信息中...", true);
            try
            {
                var m = await _movieService.GetByIdAsync(id);
                if (m == null || string.IsNullOrWhiteSpace(m.Title)) { _mainWindow?.SetStatus("电影不存在"); return; }

                var cookie = EasyMovie.Core.AppSettings.DoubanCookie;
                var tmdbKey = EasyMovie.Core.AppSettings.TmdbApiKey;
                var keyword = EasyMovie.Tools.MovieApi.DoubanApiClient.ExtractChineseKeyword(m.Title);
                var engHint = EasyMovie.Tools.MovieApi.DoubanApiClient.ExtractEnglishHint(m.Title);

                EasyMovie.Core.Interfaces.MovieSearchResult? info = null;
                var source = "";

                // 1. 尝试豆瓣
                if (!string.IsNullOrEmpty(cookie))
                {
                    try
                    {
                        _mainWindow?.SetStatus("豆瓣搜索: " + keyword + "...", true);
                        var douban = new EasyMovie.Tools.MovieApi.DoubanApiClient();
                        var sr = await douban.SearchAsync(new EasyMovie.Core.Interfaces.MovieSearchRequest { Keyword = m.Title, Page = 1, PageSize = 5 });
                        if (sr.Results.Count > 0)
                        {
                            EasyMovie.Core.Interfaces.MovieSearchResult? best = null;
                            if (!string.IsNullOrEmpty(engHint))
                                foreach (var r in sr.Results)
                                    if (!string.IsNullOrEmpty(r.OriginalTitle) && r.OriginalTitle.Contains(engHint, StringComparison.OrdinalIgnoreCase)) { best = r; break; }
                            if (best == null) best = sr.Results[0];
                            info = await douban.GetDetailAsync(best.ExternalId ?? "") ?? best;
                            source = "douban";
                        }
                    }
                    catch { }
                }

                // 2. 豆瓣无结果或关键信息缺失 → TMDB 备选
                bool needsMoreInfo(string? dir, string? country, string? poster) =>
                    string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(country) || string.IsNullOrEmpty(poster);

                if (info == null || needsMoreInfo(info.Director, info.Country, info.PosterUrl))
                {
                    try
                    {
                        var tmdb = new EasyMovie.Tools.MovieApi.TmdbApiClient();
                        var tmdbQueries = new List<string>();
                        if (!string.IsNullOrEmpty(engHint)) tmdbQueries.Add(engHint);
                        if (!string.IsNullOrEmpty(keyword) && keyword != engHint) tmdbQueries.Add(keyword);
                        if (tmdbQueries.Count == 0) tmdbQueries.Add(m.Title);

                        foreach (var q in tmdbQueries)
                        {
                            _mainWindow?.SetStatus("TMDB搜索: " + q + "...", true);
                            var sr = await tmdb.SearchAsync(new EasyMovie.Core.Interfaces.MovieSearchRequest { Keyword = q, Page = 1, PageSize = 10 });
                            if (sr.Results.Count > 0)
                            {
                                // 匹配逻辑：年份+标题 → 用户选择 → 第一个
                                var best = sr.Results[0];
                                if (!string.IsNullOrEmpty(engHint))
                                {
                                    // 先尝试标题+年份同时匹配
                                    if (m.Year > 0)
                                        foreach (var r in sr.Results)
                                            if (!string.IsNullOrEmpty(r.OriginalTitle) && r.OriginalTitle.Contains(engHint, StringComparison.OrdinalIgnoreCase) && r.Year == m.Year) { best = r; break; }

                                    // 年份未匹配：多个同名结果时让用户选择
                                    if (best.Year != m.Year || m.Year == 0)
                                    {
                                        var titleMatches = sr.Results
                                            .Where(r => !string.IsNullOrEmpty(r.OriginalTitle) && r.OriginalTitle.Contains(engHint, StringComparison.OrdinalIgnoreCase))
                                            .ToList();
                                        if (titleMatches.Count > 1)
                                        {
                                            var items = titleMatches.Select(r => $"{r.Title} ({r.Year}) - {r.OriginalTitle}").ToArray();
                                            var choice = Application.Current.Dispatcher.Invoke(() =>
                                            {
                                                var dlg = CreateThemedWindow("选择电影版本", 500, 300);
                                                var tb = new TextBlock { Text = $"\"{m.Title}\" 搜索到多个版本，请选择：", Margin = new Thickness(10), TextWrapping = TextWrapping.Wrap };
                                                var lb = new ListBox { Margin = new Thickness(10, 0, 10, 10) };
                                                foreach (var item in items) lb.Items.Add(item);
                                                lb.SelectedIndex = 0;
                                                var btn = new Button { Content = "确定", Width = 80, Height = 30, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10) };
                                                btn.Click += (s, e) => { dlg.DialogResult = true; dlg.Close(); };
                                                var panel = new StackPanel();
                                                panel.Children.Add(tb);
                                                panel.Children.Add(lb);
                                                panel.Children.Add(btn);
                                                dlg.Content = panel;
                                                return dlg.ShowDialog() == true ? lb.SelectedIndex : -1;
                                            });
                                            if (choice >= 0 && choice < titleMatches.Count) best = titleMatches[choice];
                                            else { _mainWindow?.SetStatus("已取消"); return; }
                                        }
                                        else if (titleMatches.Count == 1) best = titleMatches[0];
                                    }
                                }
                                var tmdbInfo = await tmdb.GetDetailAsync(best.ExternalId ?? "") ?? best;
                                // 用 TMDB 结果补充豆瓣缺失的字段（覆盖已有数据）
                                if (info == null) { info = tmdbInfo; source = "tmdb"; }
                                else
                                {
                                    if (!string.IsNullOrEmpty(tmdbInfo.Director)) info.Director = tmdbInfo.Director;
                                    if (!string.IsNullOrEmpty(tmdbInfo.Country)) info.Country = tmdbInfo.Country;
                                    if (!string.IsNullOrEmpty(tmdbInfo.PosterUrl)) info.PosterUrl = tmdbInfo.PosterUrl;
                                    if (!string.IsNullOrEmpty(tmdbInfo.Cast)) info.Cast = tmdbInfo.Cast;
                                    if (!string.IsNullOrEmpty(tmdbInfo.Synopsis)) info.Synopsis = tmdbInfo.Synopsis;
                                    if (tmdbInfo.Year > 0) info.Year = tmdbInfo.Year;
                                    if (tmdbInfo.Runtime.HasValue) info.Runtime = tmdbInfo.Runtime;
                                }
                                break;
                            }
                        }
                    }
                    catch (Exception ex) { _mainWindow?.SetStatus("TMDB异常: " + ex.Message); await Task.Delay(2000); }
                }

                if (info == null) { _mainWindow?.SetStatus("❌ 未找到: " + keyword); await Task.Delay(3000); return; }

                var updated = false;
                // 用新数据覆盖：新数据有效且与旧数据不同时更新
                if (!string.IsNullOrEmpty(info.Director) && info.Director != m.Director) { m.Director = info.Director; updated = true; }
                if (!string.IsNullOrEmpty(info.Cast) && info.Cast != m.Cast) { m.Cast = info.Cast; updated = true; }
                if (!string.IsNullOrEmpty(info.Country) && info.Country != m.Country) { m.Country = info.Country; updated = true; }
                if (!string.IsNullOrEmpty(info.Language) && info.Language != m.Language) { m.Language = info.Language; updated = true; }
                if (!string.IsNullOrEmpty(info.Synopsis) && info.Synopsis != m.Synopsis) { m.Synopsis = info.Synopsis; updated = true; }
                if (!string.IsNullOrEmpty(info.PosterUrl) && info.PosterUrl != m.PosterUrl)
                {
                    m.PosterUrl = info.PosterUrl;
                    updated = true;
                    // 下载海报存入数据库
                    try
                    {
                        var imgClient = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(10) };
                        imgClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
                        if (info.PosterUrl.Contains("themoviedb.org") || info.PosterUrl.Contains("tmdb.org"))
                            imgClient.DefaultRequestHeaders.Add("Referer", "https://www.themoviedb.org/");
                        else if (info.PosterUrl.Contains("douban"))
                            imgClient.DefaultRequestHeaders.Add("Referer", "https://movie.douban.com/");
                        var posterBytes = await imgClient.GetByteArrayAsync(info.PosterUrl);
                        m.PosterData = posterBytes;
                    }
                    catch { }
                }
                if (info.Runtime.HasValue && info.Runtime != m.Runtime) { m.Runtime = info.Runtime; updated = true; }
                if (info.Year > 0 && info.Year != m.Year) { m.Year = info.Year; updated = true; }
                if (source == "douban" && !string.IsNullOrEmpty(info.ExternalId) && info.ExternalId != m.DoubanId) { m.DoubanId = info.ExternalId; updated = true; }
                if (source == "tmdb" && !string.IsNullOrEmpty(info.ExternalId) && info.ExternalId != m.TmdbId) { m.TmdbId = info.ExternalId; updated = true; }

                if (!string.IsNullOrEmpty(info.Country))
                {
                    var firstCountry = info.Country.Split('/', ' ', '·').FirstOrDefault(c => IsValidCategoryName(c.Trim()))?.Trim();
                    if (!string.IsNullOrEmpty(firstCountry) && IsValidCategoryName(firstCountry))
                    {
                        try
                        {
                            var category = await _categoryService.GetOrCreateByNameAsync(firstCountry);
                            if (m.CategoryId != category.Id)
                            {
                                m.CategoryId = category.Id;
                                updated = true;
                            }
                        }
                        catch { }
                    }
                }

                if (updated) { await _movieService.UpdateAsync(m); _mainWindow?.ShowMovieDetail(m); await LoadMoviesAsync(); _mainWindow?.SetStatus("✅ 已更新(" + source + "): " + m.Title); }
                else _mainWindow?.SetStatus("ℹ️ 无需更新: " + m.Title);
            }
            catch (Exception ex) { _mainWindow?.SetStatus("❌ 获取失败: " + ex.Message); }
            finally
            {
                b.IsEnabled = true;
                await Task.Delay(2000);
                _mainWindow?.ClearStatus();
            }
        }
    }

    private async void FetchAll_Click(object sender, RoutedEventArgs e)
    {
        var cookie = EasyMovie.Core.AppSettings.DoubanCookie;
        var tmdbKey = EasyMovie.Core.AppSettings.TmdbApiKey;

        var (keyword, categoryId, status) = GetFilterValues();
        var sortInfo = GetSortInfo();
        var year = GetYearFilter();
        var (all, _) = await _movieService.SearchAsync(keyword, categoryId, null, year, year, null, null, status, null, null, null, null, null, sortInfo.sortBy, sortInfo.sortDesc, 1, 1000);
        var needFetch = all.Where(m => string.IsNullOrEmpty(m.Director) || !m.CategoryId.HasValue || string.IsNullOrEmpty(m.Country) || string.IsNullOrEmpty(m.PosterUrl)).ToList();
        if (needFetch.Count == 0) { AppMessageBox.ShowInfo("所有电影已有信息"); return; }

        _mainWindow?.SetStatus("批量获取中...", true);
        var douban = !string.IsNullOrEmpty(cookie) ? new EasyMovie.Tools.MovieApi.DoubanApiClient() : null;
        var tmdb = new EasyMovie.Tools.MovieApi.TmdbApiClient();
        var done = 0;
        foreach (var m in needFetch)
        {
            try
            {
                var kw = EasyMovie.Tools.MovieApi.DoubanApiClient.ExtractChineseKeyword(m.Title);
                var engHint = EasyMovie.Tools.MovieApi.DoubanApiClient.ExtractEnglishHint(m.Title);
                EasyMovie.Core.Interfaces.MovieSearchResult? info = null;
                var source = "";

                // 1. 尝试豆瓣
                if (douban != null)
                {
                    try
                    {
                        _mainWindow?.SetStatus($"({++done}/{needFetch.Count}) 豆瓣: {kw}...", true);
                        var sr = await douban.SearchAsync(new EasyMovie.Core.Interfaces.MovieSearchRequest { Keyword = m.Title, Page = 1, PageSize = 3 });
                        if (sr.Results.Count > 0)
                        {
                            EasyMovie.Core.Interfaces.MovieSearchResult? best = null;
                            if (!string.IsNullOrEmpty(engHint))
                                foreach (var r in sr.Results)
                                    if (!string.IsNullOrEmpty(r.OriginalTitle) && r.OriginalTitle.Contains(engHint, StringComparison.OrdinalIgnoreCase)) { best = r; break; }
                            if (best == null) best = sr.Results[0];
                            info = await douban.GetDetailAsync(best.ExternalId ?? "") ?? best;
                            source = "douban";
                        }
                    }
                    catch { }
                }
                else { done++; }

                // 2. 豆瓣无结果或关键信息缺失 → TMDB
                bool needsMoreInfo(string? dir, string? country, string? poster) =>
                    string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(country) || string.IsNullOrEmpty(poster);

                if (info == null || needsMoreInfo(info.Director, info.Country, info.PosterUrl))
                {
                    try
                    {
                        var tmdbQueries = new List<string>();
                        if (!string.IsNullOrEmpty(engHint)) tmdbQueries.Add(engHint);
                        if (!string.IsNullOrEmpty(kw) && kw != engHint) tmdbQueries.Add(kw);
                        if (tmdbQueries.Count == 0) tmdbQueries.Add(m.Title);

                        foreach (var q in tmdbQueries)
                        {
                            _mainWindow?.SetStatus($"({done}/{needFetch.Count}) TMDB: {q}...", true);
                            var sr = await tmdb.SearchAsync(new EasyMovie.Core.Interfaces.MovieSearchRequest { Keyword = q, Page = 1, PageSize = 10 });
                            if (sr.Results.Count > 0)
                            {
                                var best = sr.Results[0];
                                if (!string.IsNullOrEmpty(engHint))
                                {
                                    if (m.Year > 0)
                                        foreach (var r in sr.Results)
                                            if (!string.IsNullOrEmpty(r.OriginalTitle) && r.OriginalTitle.Contains(engHint, StringComparison.OrdinalIgnoreCase) && r.Year == m.Year) { best = r; break; }
                                    // 批量获取时不弹选择框，选最新版
                                    if (best.Year != m.Year || m.Year == 0)
                                    {
                                        var titleMatches = sr.Results
                                            .Where(r => !string.IsNullOrEmpty(r.OriginalTitle) && r.OriginalTitle.Contains(engHint, StringComparison.OrdinalIgnoreCase))
                                            .ToList();
                                        if (titleMatches.Count > 0)
                                            best = titleMatches.OrderByDescending(r => r.Year).First();
                                    }
                                }
                                var tmdbInfo = await tmdb.GetDetailAsync(best.ExternalId ?? "") ?? best;
                                if (info == null) { info = tmdbInfo; source = "tmdb"; }
                                else
                                {
                                    if (!string.IsNullOrEmpty(tmdbInfo.Director)) info.Director = tmdbInfo.Director;
                                    if (!string.IsNullOrEmpty(tmdbInfo.Country)) info.Country = tmdbInfo.Country;
                                    if (!string.IsNullOrEmpty(tmdbInfo.PosterUrl)) info.PosterUrl = tmdbInfo.PosterUrl;
                                    if (!string.IsNullOrEmpty(tmdbInfo.Cast)) info.Cast = tmdbInfo.Cast;
                                    if (!string.IsNullOrEmpty(tmdbInfo.Synopsis)) info.Synopsis = tmdbInfo.Synopsis;
                                    if (tmdbInfo.Year > 0) info.Year = tmdbInfo.Year;
                                    if (tmdbInfo.Runtime.HasValue) info.Runtime = tmdbInfo.Runtime;
                                }
                                break;
                            }
                        }
                    }
                    catch { }
                }

                if (info == null) continue;

                if (!string.IsNullOrEmpty(info.Director) && info.Director != m.Director) m.Director = info.Director;
                if (!string.IsNullOrEmpty(info.Cast) && info.Cast != m.Cast) m.Cast = info.Cast;
                if (!string.IsNullOrEmpty(info.Country) && info.Country != m.Country) m.Country = info.Country;
                if (!string.IsNullOrEmpty(info.Language) && info.Language != m.Language) m.Language = info.Language;
                if (!string.IsNullOrEmpty(info.Synopsis) && info.Synopsis != m.Synopsis) m.Synopsis = info.Synopsis;
                if (!string.IsNullOrEmpty(info.PosterUrl) && info.PosterUrl != m.PosterUrl)
                {
                    m.PosterUrl = info.PosterUrl;
                    try
                    {
                        var imgClient = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(10) };
                        imgClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
                        if (info.PosterUrl.Contains("themoviedb.org") || info.PosterUrl.Contains("tmdb.org"))
                            imgClient.DefaultRequestHeaders.Add("Referer", "https://www.themoviedb.org/");
                        else if (info.PosterUrl.Contains("douban"))
                            imgClient.DefaultRequestHeaders.Add("Referer", "https://movie.douban.com/");
                        m.PosterData = await imgClient.GetByteArrayAsync(info.PosterUrl);
                    }
                    catch { }
                }
                if (info.Runtime.HasValue && info.Runtime != m.Runtime) m.Runtime = info.Runtime;
                if (info.Year > 0 && info.Year != m.Year) m.Year = info.Year;
                if (source == "douban" && !string.IsNullOrEmpty(info.ExternalId) && info.ExternalId != m.DoubanId) m.DoubanId = info.ExternalId;
                if (source == "tmdb" && !string.IsNullOrEmpty(info.ExternalId) && info.ExternalId != m.TmdbId) m.TmdbId = info.ExternalId;

                if (!string.IsNullOrEmpty(info.Country) && !m.CategoryId.HasValue)
                {
                    var firstCountry = info.Country.Split('/', ' ', '·').FirstOrDefault(c => IsValidCategoryName(c.Trim()))?.Trim();
                    if (!string.IsNullOrEmpty(firstCountry) && IsValidCategoryName(firstCountry))
                    {
                        try
                        {
                            var category = await _categoryService.GetOrCreateByNameAsync(firstCountry);
                            m.CategoryId = category.Id;
                        }
                        catch { }
                    }
                }

                await _movieService.UpdateAsync(m);
                await LoadMoviesAsync();
            }
            catch { }
            await Task.Delay(1500);
        }
        _mainWindow?.ClearStatus();
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
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) { AppMessageBox.ShowInfo("请选择有效文件夹"); return; }

            _mainWindow?.SetStatus("批量获取中...", true);
            var files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories).Where(f => VideoExts.Contains(Path.GetExtension(f))).ToList();
            var addedIds = new List<int>();

            // 阶段1: 快速导入所有文件 (跳过已存在的)
            var existingPaths = new HashSet<string>((await _movieService.GetAllAsync()).Where(m => m.FilePath != null).Select(m => m.FilePath!));
            for (int i = 0; i < files.Count; i++)
            {
                if (existingPaths.Contains(files[i])) { _mainWindow?.SetStatus("(" + (i + 1) + "/" + files.Count + ") 跳过重复: " + Path.GetFileName(files[i]), true); continue; }
                _mainWindow?.SetStatus("(" + (i + 1) + "/" + files.Count + ") " + Path.GetFileName(files[i]), true);
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
                var maoyan = new EasyMovie.Tools.MovieApi.MaoyanApiClient();
                var tmdb = new TmdbApiClient();
                // 猫眼优先 -> 豆瓣 -> TMDB
                var api = new MovieApiService(maoyan, tmdb);
                var done = 0;
                foreach (var id in addedIds)
                {
                    var m = await _movieService.GetByIdAsync(id);
                    if (m == null || string.IsNullOrWhiteSpace(m.Title)) { done++; continue; }
                    _mainWindow?.SetStatus("获取信息 (" + (++done) + "/" + addedIds.Count + "): " + m.Title, true);
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
                            if (!string.IsNullOrEmpty(info.PosterUrl))
                            {
                                m.PosterUrl = info.PosterUrl;
                                try
                                {
                                    var imgClient = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(10) };
                                    imgClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
                                    if (info.PosterUrl.Contains("themoviedb.org") || info.PosterUrl.Contains("tmdb.org"))
                                        imgClient.DefaultRequestHeaders.Add("Referer", "https://www.themoviedb.org/");
                                    else if (info.PosterUrl.Contains("douban"))
                                        imgClient.DefaultRequestHeaders.Add("Referer", "https://movie.douban.com/");
                                    m.PosterData = await imgClient.GetByteArrayAsync(info.PosterUrl);
                                }
                                catch { }
                            }
                            if (info.Runtime.HasValue) m.Runtime = info.Runtime;
                            if (info.Year > 0 && m.Year == 0) m.Year = info.Year;
                            if (info.Source == "douban") m.DoubanId = info.ExternalId;
                            else if (info.Source == "tmdb") m.TmdbId = info.ExternalId;

                            if (!string.IsNullOrEmpty(info.Country) && !m.CategoryId.HasValue)
                            {
                                var firstCountry = info.Country.Split('/', ' ', '·').FirstOrDefault(c => IsValidCategoryName(c.Trim()))?.Trim();
                                if (!string.IsNullOrEmpty(firstCountry) && IsValidCategoryName(firstCountry))
                                {
                                    try { var category = await _categoryService.GetOrCreateByNameAsync(firstCountry); m.CategoryId = category.Id; } catch { }
                                }
                            }

                            await _movieService.UpdateAsync(m);
                            await LoadMoviesAsync(); // 每部更新后立即刷新列表
                        }
                    }
                    catch { }
                    await Task.Delay(600);
                }
            }

            _mainWindow?.ClearStatus();
        }
        catch (Exception ex) { _mainWindow?.ClearStatus(); AppMessageBox.ShowError("导入失败: " + ex.Message); }
    }

    private List<Movie> GetSelectedMovies()
    {
        if (_isCardView)
        {
            return CardPanel.Children.OfType<Card>()
                .Where(c => c.Tag is int id && _selectedCardIds.Contains(id))
                .Select(c => c.DataContext as Movie)
                .Where(m => m != null).Cast<Movie>().ToList();
        }
        if (_isPosterView)
        {
            return PosterWall.SelectedItems.Cast<Movie>().ToList();
        }
        return MovieDataGrid.SelectedItems.Cast<Movie>().ToList();
    }

    private readonly HashSet<int> _selectedCardIds = new();

    private void UpdateBatchPanel()
    {
        var selected = GetSelectedMovies();
        if (selected.Count >= 2)
        {
            BatchEditPanel.Visibility = Visibility.Visible;
            PaginationBorder.Visibility = Visibility.Collapsed;
            BatchCountText.Text = string.Format(LanguageManager.GetString("MovieLib_BatchSelected"), selected.Count);
        }
        else
        {
            BatchEditPanel.Visibility = Visibility.Collapsed;
            PaginationBorder.Visibility = Visibility.Visible;
        }
    }

    private void BatchPanelClose_Click(object sender, RoutedEventArgs e)
    {
        BatchEditPanel.Visibility = Visibility.Collapsed;
        PaginationBorder.Visibility = Visibility.Visible;
        if (!_isCardView && !_isPosterView)
            MovieDataGrid.SelectedItems.Clear();
        else if (_isPosterView)
            PosterWall.SelectedItems.Clear();
        else
            _selectedCardIds.Clear();
    }

    private async void BatchApply_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedMovies();
        if (selected.Count == 0)
        {
            AppMessageBox.ShowInfo(LanguageManager.GetString("MovieLib_BatchNoSelection"));
            return;
        }

        int? categoryId = null;
        if (BatchCategoryCombo.SelectedItem is ComboBoxItem catItem && catItem.Tag is int cid)
            categoryId = cid;

        WatchStatus? status = null;
        if (BatchStatusCombo.SelectedItem is ComboBoxItem stItem && stItem.Tag is string st && !string.IsNullOrEmpty(st))
            status = st switch { "NotWatched" => WatchStatus.NotWatched, "WantToWatch" => WatchStatus.WantToWatch, "Watched" => WatchStatus.Watched, _ => null };

        int? rating = null;
        if (BatchRatingCombo.SelectedItem is ComboBoxItem rtItem && rtItem.Tag is string rt && int.TryParse(rt, out var rv))
            rating = rv;

        bool? favorite = null;
        if (BatchFavoriteCombo.SelectedItem is ComboBoxItem favItem && favItem.Tag is string fav && bool.TryParse(fav, out var fv))
            favorite = fv;

        foreach (var m in selected)
        {
            if (categoryId.HasValue) m.CategoryId = categoryId;
            if (status.HasValue) m.WatchStatus = status.Value;
            if (rating.HasValue) m.Rating = rating.Value;
            if (favorite.HasValue) m.IsFavorite = favorite.Value;
        }

        await _context.SaveChangesAsync();
        AppMessageBox.ShowInfo(string.Format(LanguageManager.GetString("MovieLib_BatchApplied"), selected.Count));

        BatchCategoryCombo.SelectedIndex = 0;
        BatchStatusCombo.SelectedIndex = 0;
        BatchRatingCombo.SelectedIndex = 0;
        BatchFavoriteCombo.SelectedIndex = 0;
        BatchEditPanel.Visibility = Visibility.Collapsed;

        await LoadMoviesAsync();
    }

    private async void BatchDelete_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedMovies();
        if (selected.Count == 0)
        {
            AppMessageBox.ShowInfo(LanguageManager.GetString("MovieLib_BatchNoSelection"));
            return;
        }

        if (!AppMessageBox.Confirm(
            string.Format(LanguageManager.GetString("MovieLib_BatchConfirmDelete"), selected.Count),
            "")) return;

        _context.Movies.RemoveRange(selected);
        await _context.SaveChangesAsync();
        AppMessageBox.ShowInfo(string.Format(LanguageManager.GetString("MovieLib_BatchDeleted"), selected.Count));
        BatchEditPanel.Visibility = Visibility.Collapsed;
        await LoadMoviesAsync();
    }

    private void BatchSelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_isPosterView)
        {
            PosterWall.SelectAll();
        }
        else if (!_isCardView)
        {
            MovieDataGrid.SelectAll();
        }
        else
        {
            foreach (var child in CardPanel.Children.OfType<Card>())
            {
                if (child.DataContext is Movie m)
                    _selectedCardIds.Add(m.Id);
            }
            UpdateBatchPanel();
        }
    }

    public void FocusSearchBox()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    public void AddNewMovie() => OpenDetailView(0);

    public async void DeleteSelectedMovie()
    {
        var selected = GetSelectedMovies();
        if (selected.Count == 0) return;
        if (selected.Count == 1)
        {
            if (AppMessageBox.Confirm(LanguageManager.GetString("Msg_ConfirmDelete"), LanguageManager.GetString("Msg_Confirm")))
            {
                await _movieService.DeleteAsync(selected[0].Id);
                await LoadMoviesAsync();
            }
        }
        else
        {
            BatchDelete_Click(null, null);
        }
    }

    public void OpenSelectedMovieDetail()
    {
        var selected = GetSelectedMovies();
        if (selected.Count == 1) OpenDetailView(selected[0].Id);
    }

    public async void RefreshData()
    {
        _currentPage = 1;
        await LoadMoviesAsync();
    }

    public void SelectAllMovies()
    {
        if (_isPosterView) PosterWall.SelectAll();
        else if (!_isCardView) MovieDataGrid.SelectAll();
        else
        {
            foreach (var child in CardPanel.Children.OfType<Card>())
                if (child.DataContext is Movie m) _selectedCardIds.Add(m.Id);
            UpdateBatchPanel();
        }
    }

    public void DeselectAll()
    {
        if (_isPosterView) PosterWall.SelectedItems.Clear();
        else if (!_isCardView) MovieDataGrid.SelectedItems.Clear();
        else { _selectedCardIds.Clear(); UpdateBatchPanel(); }
        _mainWindow?.ShowMovieDetail(null);
    }

    public async void CycleView()
    {
        if (!_isCardView && !_isPosterView && !_isCollectionView) { _isCardView = true; _isPosterView = false; _isCollectionView = false; }
        else if (_isCardView) { _isCardView = false; _isPosterView = true; _isCollectionView = false; }
        else if (_isPosterView) { _isCardView = false; _isPosterView = false; _isCollectionView = true; }
        else { _isCardView = false; _isPosterView = false; _isCollectionView = false; }
        UpdateViewButtons();
        if (_isCollectionView) await LoadCollectionViewAsync();
        else await LoadMoviesAsync();
    }

    private async Task LoadCollectionViewAsync()
    {
        MovieDataGrid.Visibility = Visibility.Collapsed;
        CardScrollViewer.Visibility = Visibility.Collapsed;
        PosterWall.Visibility = Visibility.Collapsed;
        EmptyLabel.Visibility = Visibility.Collapsed;
        CollectionScrollViewer.Visibility = Visibility.Visible;
        PaginationBorder.Visibility = Visibility.Collapsed;

        CollectionPanel.Children.Clear();

        var backBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        var backBtn = new Button
        {
            Style = (Style)Application.Current.FindResource("MaterialDesignFlatButton"),
            Content = LanguageManager.GetString("Collection_BackToList"),
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        backBtn.Click += async (s, e) => { _isCollectionView = false; UpdateViewButtons(); await LoadMoviesAsync(); };
        backBar.Children.Add(backBtn);
        CollectionPanel.Children.Add(backBar);

        var collections = await _collectionService.GetAllWithMoviesAsync();

        if (collections.Count == 0)
        {
            var emptyText = new TextBlock
            {
                Text = LanguageManager.GetString("Collection_Empty"),
                FontSize = 14,
                Foreground = SafeFindBrush("MaterialDesignHintForeground", Color.FromRgb(117, 117, 117)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 40, 0, 16)
            };
            CollectionPanel.Children.Add(emptyText);
        }

        if (collections.Count == 0) return;

        foreach (var col in collections)
        {
            var card = new Border
            {
                Background = SafeFindBrush("MaterialDesignCardBackground", Color.FromRgb(45, 45, 45)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16),
                Margin = new Thickness(0, 0, 0, 12)
            };

            var stack = new StackPanel();

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titlePanel = new StackPanel { Orientation = Orientation.Horizontal };
            titlePanel.Children.Add(new PackIcon { Kind = PackIconKind.FolderStar, Width = 20, Height = 20, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(121, 134, 203)) });
            titlePanel.Children.Add(new TextBlock { Text = col.Name, FontSize = 16, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Foreground = SafeFindBrush("MaterialDesignBody", Colors.White) });
            titlePanel.Children.Add(new TextBlock { Text = $" ({col.Movies.Count})", FontSize = 13, Foreground = SafeFindBrush("MaterialDesignHintForeground", Color.FromRgb(117, 117, 117)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) });
            Grid.SetColumn(titlePanel, 0);
            header.Children.Add(titlePanel);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal };
            var editBtn = new Button { Style = (Style)Application.Current.FindResource("MaterialDesignIconForegroundButton"), Tag = col.Id, ToolTip = LanguageManager.GetString("Collection_Edit"), Width = 28, Height = 28 };
            editBtn.Content = new PackIcon { Kind = PackIconKind.Pencil, Width = 16, Height = 16 };
            editBtn.Click += EditCollection_Click;
            btnPanel.Children.Add(editBtn);

            var deleteBtn = new Button { Style = (Style)Application.Current.FindResource("MaterialDesignIconForegroundButton"), Tag = col.Id, ToolTip = LanguageManager.GetString("Collection_Delete"), Width = 28, Height = 28, Margin = new Thickness(4, 0, 0, 0) };
            deleteBtn.Content = new PackIcon { Kind = PackIconKind.Delete, Width = 16, Height = 16 };
            deleteBtn.Click += DeleteCollection_Click;
            btnPanel.Children.Add(deleteBtn);

            Grid.SetColumn(btnPanel, 1);
            header.Children.Add(btnPanel);

            stack.Children.Add(header);

            if (!string.IsNullOrEmpty(col.Description))
                stack.Children.Add(new TextBlock { Text = col.Description, FontSize = 12, Foreground = SafeFindBrush("MaterialDesignHintForeground", Color.FromRgb(117, 117, 117)), Margin = new Thickness(0, 4, 0, 8), TextWrapping = TextWrapping.Wrap });

            var movieWrap = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var movie in col.Movies)
            {
                var movieCard = BuildCollectionMovieCard(movie, col.Id);
                movieWrap.Children.Add(movieCard);
            }

            var addMovieBtn = new Border
            {
                Width = 120, Height = 170, Margin = new Thickness(6),
                Background = SafeFindBrush("MaterialDesignCardBackground", Color.FromRgb(45, 45, 45)),
                CornerRadius = new CornerRadius(8),
                BorderBrush = SafeFindBrush("MaterialDesignDivider", Color.FromRgb(80, 80, 80)),
                BorderThickness = new Thickness(1, 1, 1, 1),
                Cursor = Cursors.Hand,
                Tag = col.Id
            };
            var addStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            addStack.Children.Add(new PackIcon { Kind = PackIconKind.Plus, Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Center, Foreground = SafeFindBrush("MaterialDesignHintForeground", Color.FromRgb(117, 117, 117)) });
            addStack.Children.Add(new TextBlock { Text = LanguageManager.GetString("Collection_AddMovie"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Foreground = SafeFindBrush("MaterialDesignHintForeground", Color.FromRgb(117, 117, 117)), Margin = new Thickness(0, 4, 0, 0) });
            addMovieBtn.Child = addStack;
            addMovieBorder_MouseLeftButtonUp(addMovieBtn, col.Id);
            movieWrap.Children.Add(addMovieBtn);

            stack.Children.Add(movieWrap);
            card.Child = stack;
            CollectionPanel.Children.Add(card);
        }

        var addColBtn = new Button
        {
            Content = LanguageManager.GetString("Collection_AddNew"),
            Style = (Style)Application.Current.FindResource("MaterialDesignRaisedButton"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0)
        };
        addColBtn.Click += AddCollection_Click;
        CollectionPanel.Children.Add(addColBtn);
    }

    private void addMovieBorder_MouseLeftButtonUp(Border border, int collectionId)
    {
        border.MouseLeftButtonUp += async (s, e) =>
        {
            var dlg = new AddMovieToCollectionDialog(collectionId, _context)
            {
                Owner = Window.GetWindow(this)
            };
            if (dlg.ShowDialog() == true)
                await LoadCollectionViewAsync();
        };
    }

    private Border BuildCollectionMovieCard(Movie movie, int collectionId)
    {
        var card = new Border
        {
            Width = 120, Height = 170, Margin = new Thickness(6),
            Background = SafeFindBrush("MaterialDesignCardBackground", Color.FromRgb(45, 45, 45)),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Cursor = Cursors.Hand,
            Tag = movie.Id
        };

        var grid = new Grid();

        if (movie.PosterData is { Length: > 0 })
        {
            try
            {
                var img = new Image { Stretch = Stretch.UniformToFill, VerticalAlignment = VerticalAlignment.Center };
                var bmp = new BitmapImage();
                bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = new MemoryStream(movie.PosterData); bmp.EndInit(); bmp.Freeze();
                img.Source = bmp;
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                grid.Children.Add(img);
            }
            catch { }
        }

        if (grid.Children.Count == 0)
        {
            var ph = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Background = SafeFindBrush("MaterialDesignDivider", Color.FromRgb(48, 48, 48)) };
            ph.Children.Add(new PackIcon { Kind = PackIconKind.MovieOpen, Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Center, Foreground = SafeFindBrush("MaterialDesignHintForeground", Color.FromRgb(117, 117, 117)) });
            grid.Children.Add(ph);
        }

        // 播放按钮叠加层
        if (!string.IsNullOrEmpty(movie.FilePath))
        {
            var playBg = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0));
            var playBgHover = new SolidColorBrush(Color.FromArgb(220, 121, 134, 203));
            var playOverlay = new Border
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Width = 36, Height = 36,
                CornerRadius = new CornerRadius(18),
                Background = playBg,
                Cursor = Cursors.Hand,
                RenderTransform = new ScaleTransform(1, 1),
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            var playIcon = new PackIcon { Kind = PackIconKind.Play, Width = 18, Height = 18, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var playContainer = new Grid();
            playContainer.Children.Add(playIcon);
            playOverlay.Child = playContainer;
            playOverlay.MouseEnter += (s, e) => { playOverlay.Background = playBgHover; playOverlay.RenderTransform = new ScaleTransform(1.15, 1.15); };
            playOverlay.MouseLeave += (s, e) => { playOverlay.Background = playBg; playOverlay.RenderTransform = new ScaleTransform(1, 1); };
            playOverlay.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                if (!File.Exists(movie.FilePath))
                    AppMessageBox.ShowWarning(string.Format(LanguageManager.GetString("Msg_FileNotFound"), movie.FilePath), LanguageManager.GetString("Msg_Hint"));
                else
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(movie.FilePath) { UseShellExecute = true }); }
                    catch { }
            };
            grid.Children.Add(playOverlay);
        }

        var infoBar = new Border { VerticalAlignment = VerticalAlignment.Bottom, Padding = new Thickness(6, 4, 6, 4) };
        var gradBrush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        gradBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0));
        gradBrush.GradientStops.Add(new GradientStop(Color.FromArgb(200, 0, 0, 0), 1));
        infoBar.Background = gradBrush;
        var infoStack = new StackPanel();
        infoStack.Children.Add(new TextBlock { Text = movie.Title, FontSize = 11, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
        if (movie.Year > 0) infoStack.Children.Add(new TextBlock { Text = movie.Year.ToString(), FontSize = 10, Foreground = ColorToBrush(Color.FromArgb(187, 255, 255, 255)) });
        infoBar.Child = infoStack;
        grid.Children.Add(infoBar);

        var removeBtn = new Button
        {
            Style = (Style)Application.Current.FindResource("MaterialDesignIconForegroundButton"),
            Width = 22, Height = 22,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 2, 0),
            Tag = (movie.Id, collectionId),
            ToolTip = LanguageManager.GetString("Collection_RemoveMovie")
        };
        removeBtn.Content = new PackIcon { Kind = PackIconKind.Close, Width = 12, Height = 12 };
        removeBtn.Click += RemoveMovieFromCollection_Click;
        grid.Children.Add(removeBtn);

        card.Child = grid;
        card.MouseLeftButtonUp += (s, e) =>
        {
            if (e.Handled) return;
            _mainWindow?.ShowMovieDetail(movie);
        };

        return card;
    }

    private async void RemoveMovieFromCollection_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button btn && btn.Tag is ValueTuple<int, int> tuple)
        {
            await _collectionService.RemoveMovieFromCollectionAsync(tuple.Item1);
            await LoadCollectionViewAsync();
        }
    }

    private async void AddCollection_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new CollectionEditDialog()
        {
            Owner = Window.GetWindow(this)
        };
        if (dlg.ShowDialog() == true)
        {
            await _collectionService.AddAsync(new MovieCollection { Name = dlg.CollectionName, Description = dlg.CollectionDescription });
            await LoadCollectionViewAsync();
        }
    }

    private async void EditCollection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not int id) return;
        var col = await _collectionService.GetByIdAsync(id);
        if (col == null) return;
        var dlg = new CollectionEditDialog(col)
        {
            Owner = Window.GetWindow(this)
        };
        if (dlg.ShowDialog() == true)
        {
            col.Name = dlg.CollectionName;
            col.Description = dlg.CollectionDescription;
            await _collectionService.UpdateAsync(col);
            await LoadCollectionViewAsync();
        }
    }

    private async void DeleteCollection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not int id) return;
        if (!AppMessageBox.Confirm(LanguageManager.GetString("Collection_ConfirmDelete"), LanguageManager.GetString("Msg_Confirm"))) return;
        await _collectionService.DeleteAsync(id);
        await LoadCollectionViewAsync();
    }

    public void CloseDetailPanel()
    {
        _mainWindow?.ShowMovieDetail(null);
    }

    private void PopulateBatchCategoryCombo(List<Category> categories)
    {
        BatchCategoryCombo.Items.Clear();
        BatchCategoryCombo.Items.Add(new ComboBoxItem { Content = LanguageManager.GetString("MovieLib_BatchNoChange"), Tag = "" });
        foreach (var cat in categories)
            BatchCategoryCombo.Items.Add(new ComboBoxItem { Content = cat.Name, Tag = cat.Id });
        BatchCategoryCombo.SelectedIndex = 0;
    }
}
