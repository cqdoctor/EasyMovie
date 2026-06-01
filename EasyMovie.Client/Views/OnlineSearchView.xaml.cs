using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Services;
using EasyMovie.Data;
using EasyMovie.Data.Repositories;
using EasyMovie.Tools.MovieApi;

namespace EasyMovie.Client.Views;

public partial class OnlineSearchView : UserControl
{
    private readonly MovieDbContext _context;
    private readonly MovieApiService _apiService;
    private readonly IMovieService _movieService;
    private readonly ICategoryService _categoryService;
    public event EventHandler? MovieAdded;

    public OnlineSearchView(string? tmdbApiKey = null)
    {
        InitializeComponent();
        _context = DbHelper.CreateContext();
        _movieService = new MovieService(new MovieRepository(_context), new TagRepository(_context));
        _categoryService = new CategoryService(new CategoryRepository(_context));
        var douban = new DoubanApiClient();
        var tmdb = new TmdbApiClient();
        _apiService = new MovieApiService(douban, tmdb);
        SourceLabel.Text = LanguageManager.GetString("OnlineSearch_SourceLabel");
        Unloaded += (s, e) => _context.Dispose();
    }

    private async Task DoSearchAsync()
    {
        var kw = SearchBox.Text?.Trim(); if (string.IsNullOrWhiteSpace(kw)) return;
        SetLoading(true);
        try
        {
            var r = await _apiService.SearchAsync(kw, 1, 20);
            if (r.Results.Count == 0) ShowEmpty(LanguageManager.GetString("OnlineSearch_NoResult"));
            else { ResultListBox.ItemsSource = r.Results; ResultListBox.Visibility = Visibility.Visible; EmptyPanel.Visibility = Visibility.Collapsed; }
        }
        catch (Exception ex) { ShowEmpty(ex.Message); }
        finally { SetLoading(false); }
    }

    private async void AddResult_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is MovieSearchResult r)
        {
            try
            {
                if (string.IsNullOrEmpty(r.Synopsis)) { b.IsEnabled = false; r = await _apiService.GetDetailAsync(r.ExternalId??"", r.Source) ?? r; b.IsEnabled = true; }
                var movie = await MovieApiService.MapToMovieAsync(r, _categoryService);
                await _movieService.AddAsync(movie);
                AppMessageBox.ShowInfo(LanguageManager.GetString("OnlineSearch_Added") + r.Title); MovieAdded?.Invoke(this, EventArgs.Empty);
                var lst = ResultListBox.ItemsSource?.Cast<MovieSearchResult>().ToList();
                if (lst != null) { lst.Remove(r); ResultListBox.ItemsSource = lst; if (!lst.Any()) ShowEmpty(LanguageManager.GetString("OnlineSearch_AllAdded")); }
            }
            catch (Exception ex) { AppMessageBox.ShowError(ex.Message); }
        }
    }

    private void ShowEmpty(string msg) { ResultListBox.Visibility = Visibility.Collapsed; EmptyPanel.Visibility = Visibility.Visible; EmptyText.Text = msg; }
    private void SetLoading(bool l) { LoadingPanel.Visibility = l ? Visibility.Visible : Visibility.Collapsed; }
    private async void Search_Click(object sender, RoutedEventArgs e) => await DoSearchAsync();
    private async void SearchBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) await DoSearchAsync(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Window.GetWindow(this)?.Close();
}
