using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MovieManager.Core.Interfaces;
using MovieManager.Core.Services;
using MovieManager.Data.Repositories;
using MovieManager.Tools.MovieApi;

namespace MovieManager.Client.Views;

public partial class OnlineSearchView : UserControl
{
    private readonly MovieApiService _apiService;
    private readonly IMovieService _movieService;
    public event EventHandler? MovieAdded;

    public OnlineSearchView(string? tmdbApiKey = null)
    {
        InitializeComponent();
        var context = DbHelper.CreateContext();
        _movieService = new MovieService(new MovieRepository(context), new TagRepository(context));
        var douban = new DoubanApiClient();
        IMovieApiClient? tmdb = !string.IsNullOrWhiteSpace(tmdbApiKey) ? new TmdbApiClient(tmdbApiKey) : null;
        _apiService = new MovieApiService(douban, tmdb);
        SourceLabel.Text = string.IsNullOrWhiteSpace(tmdbApiKey) ? "豆瓣" : "豆瓣/TMDB";
    }

    private async Task DoSearchAsync()
    {
        var kw = SearchBox.Text?.Trim(); if (string.IsNullOrWhiteSpace(kw)) return;
        SetLoading(true);
        try
        {
            var r = await _apiService.SearchAsync(kw, 1, 20);
            if (r.Results.Count == 0) ShowEmpty("未找到结果");
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
                await _movieService.AddAsync(MovieApiService.MapToMovie(r));
                MessageBox.Show("已添加: " + r.Title); MovieAdded?.Invoke(this, EventArgs.Empty);
                var lst = ResultListBox.ItemsSource?.Cast<MovieSearchResult>().ToList();
                if (lst != null) { lst.Remove(r); ResultListBox.ItemsSource = lst; if (!lst.Any()) ShowEmpty("全部已添加"); }
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }
    }

    private void ShowEmpty(string msg) { ResultListBox.Visibility = Visibility.Collapsed; EmptyPanel.Visibility = Visibility.Visible; EmptyText.Text = msg; }
    private void SetLoading(bool l) { LoadingPanel.Visibility = l ? Visibility.Visible : Visibility.Collapsed; }
    private async void Search_Click(object sender, RoutedEventArgs e) => await DoSearchAsync();
    private async void SearchBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) await DoSearchAsync(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Window.GetWindow(this)?.Close();
}
