﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EasyMovie.Core.Enums;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;
using EasyMovie.Tools.MovieApi;

using Serilog;

namespace EasyMovie.Client.Views;

public partial class MovieDetailView : UserControl
{
    private readonly int _movieId;
    private readonly IMovieService _movieService;
    private readonly ICategoryService _categoryService;
    private readonly ITagService _tagService;
    private Movie? _movie;
    private List<Tag> _allTags = new();
    private readonly HashSet<int> _selectedTagIds = new();
    public string TitleText => _movieId == 0 ? LanguageManager.GetString("MovieDetail_AddTitle") : LanguageManager.GetString("MovieDetail_EditTitle");
    public event EventHandler? MovieSaved;
    public event EventHandler<int>? MovieAdded;
    public event EventHandler? MovieDeleted;

    public MovieDetailView(int movieId, IMovieService movieService, ICategoryService categoryService, ITagService tagService)
    {
        InitializeComponent();
        _movieId = movieId; _movieService = movieService; _categoryService = categoryService; _tagService = tagService;
        DataContext = this;
        DeleteBtn.Visibility = movieId == 0 ? Visibility.Collapsed : Visibility.Visible;
        Loaded += async (s, e) => await InitAsync();
    }

    private async Task InitAsync()
    {
        var cats = await _categoryService.GetAllAsync();
        CategoryCombo.Items.Clear(); CategoryCombo.Items.Add(new ComboBoxItem { Content = LanguageManager.GetString("MovieDetail_NoCategory") });
        foreach (var c in cats) CategoryCombo.Items.Add(new ComboBoxItem { Content = c.Name, Tag = c.Id });
        _allTags = await _tagService.GetAllAsync(); BuildTags();
        RatingCombo.Items.Clear(); RatingCombo.Items.Add(new ComboBoxItem { Content = LanguageManager.GetString("MovieDetail_Unrated") });
        for (var i = 10; i >= 1; i--) RatingCombo.Items.Add(new ComboBoxItem { Content = string.Format(LanguageManager.GetString("MovieDetail_RatingPoint"), i), Tag = i });
        if (_movieId > 0) { _movie = await _movieService.GetByIdAsync(_movieId); if (_movie != null) await PopulateAsync(); else { StatusCombo.SelectedIndex = 0; RatingCombo.SelectedIndex = 0; } }
        else { StatusCombo.SelectedIndex = 0; RatingCombo.SelectedIndex = 0; }
    }

    private void BuildTags()
    {
        TagPanel.Children.Clear();
        foreach (var t in _allTags)
        {
            var cb = new CheckBox { Content = t.Name, Tag = t.Id, Margin = new Thickness(0, 0, 16, 6), IsChecked = _selectedTagIds.Contains(t.Id) };
            if (!string.IsNullOrEmpty(t.Color)) try { cb.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(t.Color)); } catch (Exception ex) { Log.Error(ex, "MovieDetailView 操作异常"); }
            cb.Checked += (s, e) => _selectedTagIds.Add(t.Id);
            cb.Unchecked += (s, e) => _selectedTagIds.Remove(t.Id);
            TagPanel.Children.Add(cb);
        }
    }

    private async Task PopulateAsync()
    {
        if (_movie == null) return;
        TitleBox.Text = _movie.Title; OriginalTitleBox.Text = _movie.OriginalTitle ?? "";
        YearBox.Text = _movie.Year > 0 ? _movie.Year.ToString() : ""; RuntimeBox.Text = _movie.Runtime?.ToString() ?? "";
        DirectorBox.Text = _movie.Director ?? ""; CountryBox.Text = _movie.Country ?? "";
        CastBox.Text = _movie.Cast ?? ""; SynopsisBox.Text = _movie.Synopsis ?? "";
        for (var i = 0; i < CategoryCombo.Items.Count; i++) if (CategoryCombo.Items[i] is ComboBoxItem ci && ci.Tag is int cid && cid == _movie.CategoryId) { CategoryCombo.SelectedIndex = i; break; }
        foreach (var t in await _tagService.GetTagsForMovieAsync(_movie.Id)) _selectedTagIds.Add(t.Id);
        BuildTags();
        if (_movie.Rating.HasValue) for (var i = 0; i < RatingCombo.Items.Count; i++) if (RatingCombo.Items[i] is ComboBoxItem ri && ri.Tag is int r && r == _movie.Rating) { RatingCombo.SelectedIndex = i; break; }
        for (var i = 0; i < StatusCombo.Items.Count; i++) if (StatusCombo.Items[i] is ComboBoxItem si && si.Tag is string st && st == _movie.WatchStatus.ToString()) { StatusCombo.SelectedIndex = i; break; }
        if (_movie.WatchDate.HasValue) WatchDatePicker.SelectedDate = _movie.WatchDate.Value;
        FavoriteCheck.IsChecked = _movie.IsFavorite;
        FilePathBox.Text = _movie.FilePath ?? "";
        NotesBox.Text = _movie.Notes ?? "";
    }

    private async void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var m = _movie ?? new Movie();
            if (string.IsNullOrWhiteSpace(TitleBox.Text)) { AppMessageBox.ShowInfo(LanguageManager.GetString("MovieDetail_EnterTitle")); return; }
            m.Title = TitleBox.Text.Trim(); m.OriginalTitle = NullIfEmpty(OriginalTitleBox.Text);
            m.Year = int.TryParse(YearBox.Text, out var y) ? y : 0;
            m.Runtime = int.TryParse(RuntimeBox.Text, out var rt) ? rt : null;
            m.Director = NullIfEmpty(DirectorBox.Text); m.Country = NullIfEmpty(CountryBox.Text);
            m.Cast = NullIfEmpty(CastBox.Text); m.Synopsis = NullIfEmpty(SynopsisBox.Text);
            m.CategoryId = CategoryCombo.SelectedItem is ComboBoxItem ci && ci.Tag is int cid ? cid : null;
            m.Rating = RatingCombo.SelectedItem is ComboBoxItem ri && ri.Tag is int r ? r : null;
            m.WatchStatus = StatusCombo.SelectedItem is ComboBoxItem si && si.Tag is string st && Enum.TryParse<WatchStatus>(st, out var ws) ? ws : WatchStatus.NotWatched;
            m.WatchDate = m.WatchStatus == WatchStatus.Watched ? WatchDatePicker.SelectedDate : null;
            m.IsFavorite = FavoriteCheck.IsChecked == true;
            m.FilePath = NullIfEmpty(FilePathBox.Text);
            m.Notes = NullIfEmpty(NotesBox.Text);

            // 合并自动获取到的海报和 ExternalId（仅新增模式）
            if (_movieId == 0 && _fetchedInfo != null)
            {
                if (!string.IsNullOrEmpty(_fetchedInfo.PosterUrl) && string.IsNullOrEmpty(m.PosterUrl))
                {
                    m.PosterUrl = _fetchedInfo.PosterUrl;
                    try
                    {
                        var imgClient = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(10) };
                        imgClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
                        if (_fetchedInfo.PosterUrl.Contains("themoviedb.org") || _fetchedInfo.PosterUrl.Contains("tmdb.org"))
                            imgClient.DefaultRequestHeaders.Add("Referer", "https://www.themoviedb.org/");
                        else if (_fetchedInfo.PosterUrl.Contains("douban"))
                            imgClient.DefaultRequestHeaders.Add("Referer", "https://movie.douban.com/");
                        m.PosterData = await imgClient.GetByteArrayAsync(_fetchedInfo.PosterUrl);
                        if (m.PosterData != null) EasyMovie.Client.Helpers.PosterCache.Save(m.Id, m.PosterData);
                    }
                    catch (Exception ex) { Log.Error(ex, "MovieDetailView 操作异常"); }
                }
                if (_fetchedSource.Contains("douban") && !string.IsNullOrEmpty(_fetchedInfo.ExternalId))
                    m.DoubanId = _fetchedInfo.ExternalId;
                if (_fetchedSource.Contains("tmdb") && !string.IsNullOrEmpty(_fetchedInfo.ExternalId))
                    m.TmdbId = _fetchedInfo.ExternalId;
            }

            if (_movieId == 0) { m = await _movieService.AddAsync(m); MovieAdded?.Invoke(this, m.Id); }
            else await _movieService.UpdateAsync(m);
            await _movieService.SetTagsAsync(m.Id, _selectedTagIds.ToList());
            MovieSaved?.Invoke(this, EventArgs.Empty); CloseWin();
        }
        catch (Exception ex) { AppMessageBox.ShowError(ex.Message); }
    }

    private string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private async void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_movieId > 0 && AppMessageBox.Confirm(LanguageManager.GetString("Msg_ConfirmDelete"), LanguageManager.GetString("Msg_Confirm")))
        { await _movieService.DeleteAsync(_movieId); MovieDeleted?.Invoke(this, EventArgs.Empty); CloseWin(); }
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => CloseWin();
    private void StatusCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => WatchDatePicker.IsEnabled = StatusCombo.SelectedItem is ComboBoxItem si && si.Tag is string st && st == "Watched";
    private void CloseWin() => Window.GetWindow(this)?.Close();

    private void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = LanguageManager.GetString("MovieDetail_VideoFiles") + "|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv|" + LanguageManager.GetString("MovieDetail_AllFiles") + "|*.*" };
        if (dlg.ShowDialog() == true)
        {
            FilePathBox.Text = dlg.FileName;
            // 浏览选定文件后自动提取标题并获取电影信息
            AutoFillFromFileName(dlg.FileName);
        }
    }

    /// <summary>从文件名提取标题和年份（复用统一的 FileNameParser 清洗逻辑）</summary>
    private static (string title, int? year) ParseFileName(string fileName)
        => EasyMovie.Tools.ImportExport.FileNameParser.Parse(fileName);

    /// <summary>浏览文件后自动填充标题并触发在线获取电影信息</summary>
    private async void AutoFillFromFileName(string filePath)
    {
        // 仅在新增模式（_movieId == 0）且标题为空时自动填充，避免覆盖用户已输入内容
        if (_movieId != 0) return;

        var (title, year) = ParseFileName(filePath);
        if (string.IsNullOrWhiteSpace(title)) return;

        // 自动填充标题和年份（仅当为空时）
        if (string.IsNullOrWhiteSpace(TitleBox.Text))
            TitleBox.Text = title;
        if (year.HasValue && string.IsNullOrWhiteSpace(YearBox.Text))
            YearBox.Text = year.Value.ToString();

        // 触发自动获取电影信息
        await AutoFetchInfoAsync(title, year);
    }

    private void ShowFetchStatus(string text)
    {
        FetchStatus.Text = text;
        FetchStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>自动获取电影信息并填充表单（统一调度器：豆瓣→TMDB→OMDb→百度百科）</summary>
    private async Task AutoFetchInfoAsync(string title, int? yearHint)
    {
        ShowFetchStatus("正在获取电影信息...");
        // 总体超时：60 秒（4个数据源级联）
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = cts.Token;
        try
        {
            // 使用统一调度器
            var tempMovie = new Movie { Title = title, Year = yearHint ?? 0 };
            var fetcher = new EasyMovie.Tools.MovieApi.MovieInfoFetcher
            {
                Progress = new Progress<string>(msg => ShowFetchStatus(msg))
            };
            var fetchResult = await fetcher.FetchAsync(tempMovie, ct);
            var info = fetchResult.Info;
            var source = fetchResult.Source;

            if (info == null) { ShowFetchStatus("未找到电影信息，请手动填写"); await Task.Delay(3000); ShowFetchStatus(""); return; }

            // 清理 HTML 标签
            if (!string.IsNullOrEmpty(info.Synopsis))
                info.Synopsis = Regex.Replace(info.Synopsis, @"<[^>]+>", "").Trim();
            if (!string.IsNullOrEmpty(info.Director))
                info.Director = Regex.Replace(info.Director, @"<[^>]+>", "").Trim();
            if (!string.IsNullOrEmpty(info.Cast))
                info.Cast = Regex.Replace(info.Cast, @"<[^>]+>", "").Trim();

            // 填充表单（仅当字段为空时，避免覆盖用户输入）
            if (string.IsNullOrWhiteSpace(OriginalTitleBox.Text) && !string.IsNullOrEmpty(info.OriginalTitle))
                OriginalTitleBox.Text = info.OriginalTitle;
            if (string.IsNullOrWhiteSpace(DirectorBox.Text) && !string.IsNullOrEmpty(info.Director))
                DirectorBox.Text = info.Director;
            if (string.IsNullOrWhiteSpace(CastBox.Text) && !string.IsNullOrEmpty(info.Cast))
                CastBox.Text = info.Cast;
            if (string.IsNullOrWhiteSpace(CountryBox.Text) && !string.IsNullOrEmpty(info.Country))
                CountryBox.Text = info.Country;
            if (string.IsNullOrWhiteSpace(SynopsisBox.Text) && !string.IsNullOrEmpty(info.Synopsis))
                SynopsisBox.Text = info.Synopsis;
            if (string.IsNullOrWhiteSpace(RuntimeBox.Text) && info.Runtime.HasValue)
                RuntimeBox.Text = info.Runtime.Value.ToString();
            if (string.IsNullOrWhiteSpace(YearBox.Text) && info.Year > 0)
                YearBox.Text = info.Year.ToString();
            if (string.IsNullOrWhiteSpace(TitleBox.Text) && !string.IsNullOrEmpty(info.Title))
                TitleBox.Text = info.Title;

            // 缓存获取到的信息，供保存时使用（海报、ExternalId 等）
            _fetchedInfo = info;
            _fetchedSource = source;

            ShowFetchStatus("已获取信息(" + source + ")，可点击保存");
        }
        catch (OperationCanceledException) { ShowFetchStatus("搜索超时（60秒），请检查网络或代理"); }
        catch (Exception ex)
        {
            ShowFetchStatus("获取失败: " + ex.Message);
        }
    }

    private MovieSearchResult? _fetchedInfo;
    private string _fetchedSource = "";

    private void PlayFile_Click(object sender, RoutedEventArgs e)
    {
        var p = FilePathBox.Text?.Trim();
        if (!string.IsNullOrEmpty(p) && File.Exists(p))
        {
            if (_movie == null)
            {
                _movie = new Movie { FilePath = p, Title = TitleBox.Text?.Trim() ?? p };
            }
            else
            {
                _movie.FilePath = p;
            }
            VideoPlayerHelper.Play(_movie);
        }
        else AppMessageBox.ShowInfo(LanguageManager.GetString("MovieDetail_FileNotExist"));
    }
}
