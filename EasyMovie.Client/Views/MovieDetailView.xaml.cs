using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EasyMovie.Core.Enums;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;

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
    public string TitleText => _movieId == 0 ? "添加电影" : "编辑电影";
    public event EventHandler? MovieSaved;
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
        CategoryCombo.Items.Clear(); CategoryCombo.Items.Add(new ComboBoxItem { Content = "无分类" });
        foreach (var c in cats) CategoryCombo.Items.Add(new ComboBoxItem { Content = c.Name, Tag = c.Id });
        _allTags = await _tagService.GetAllAsync(); BuildTags();
        RatingCombo.Items.Clear(); RatingCombo.Items.Add(new ComboBoxItem { Content = "未评分" });
        for (var i = 1; i <= 10; i++) RatingCombo.Items.Add(new ComboBoxItem { Content = i + " 分", Tag = i });
        if (_movieId > 0) { _movie = await _movieService.GetByIdAsync(_movieId); if (_movie != null) await PopulateAsync(); }
        StatusCombo.SelectedIndex = 0; RatingCombo.SelectedIndex = 0;
    }

    private void BuildTags()
    {
        TagPanel.Children.Clear();
        foreach (var t in _allTags)
        {
            var cb = new CheckBox { Content = t.Name, Tag = t.Id, Margin = new Thickness(0, 0, 16, 6), IsChecked = _selectedTagIds.Contains(t.Id) };
            if (!string.IsNullOrEmpty(t.Color)) try { cb.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(t.Color)); } catch { }
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
        StatusCombo.SelectedIndex = (int)_movie.WatchStatus;
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
            if (string.IsNullOrWhiteSpace(TitleBox.Text)) { MessageBox.Show("请输入片名"); return; }
            m.Title = TitleBox.Text.Trim(); m.OriginalTitle = NullIfEmpty(OriginalTitleBox.Text);
            m.Year = int.TryParse(YearBox.Text, out var y) ? y : 0;
            m.Runtime = int.TryParse(RuntimeBox.Text, out var rt) ? rt : null;
            m.Director = NullIfEmpty(DirectorBox.Text); m.Country = NullIfEmpty(CountryBox.Text);
            m.Cast = NullIfEmpty(CastBox.Text); m.Synopsis = NullIfEmpty(SynopsisBox.Text);
            m.CategoryId = CategoryCombo.SelectedItem is ComboBoxItem ci && ci.Tag is int cid ? cid : null;
            m.Rating = RatingCombo.SelectedItem is ComboBoxItem ri && ri.Tag is int r ? r : null;
            m.WatchStatus = (WatchStatus)StatusCombo.SelectedIndex;
            m.WatchDate = StatusCombo.SelectedIndex == 2 ? WatchDatePicker.SelectedDate : null;
            m.IsFavorite = FavoriteCheck.IsChecked == true;
            m.FilePath = NullIfEmpty(FilePathBox.Text);
            m.Notes = NullIfEmpty(NotesBox.Text);
            if (_movieId == 0) m = await _movieService.AddAsync(m); else await _movieService.UpdateAsync(m);
            await _movieService.SetTagsAsync(m.Id, _selectedTagIds.ToList());
            MovieSaved?.Invoke(this, EventArgs.Empty); CloseWin();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message); }
    }

    private string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private async void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_movieId > 0 && MessageBox.Show("确定删除？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        { await _movieService.DeleteAsync(_movieId); MovieDeleted?.Invoke(this, EventArgs.Empty); CloseWin(); }
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => CloseWin();
    private void StatusCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => WatchDatePicker.IsEnabled = StatusCombo.SelectedIndex == 2;
    private void CloseWin() => Window.GetWindow(this)?.Close();

    private void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "视频文件|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv|所有文件|*.*" };
        if (dlg.ShowDialog() == true) FilePathBox.Text = dlg.FileName;
    }

    private void PlayFile_Click(object sender, RoutedEventArgs e)
    {
        var p = FilePathBox.Text?.Trim();
        if (!string.IsNullOrEmpty(p) && File.Exists(p)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = p, UseShellExecute = true });
        else MessageBox.Show("文件不存在。");
    }
}
