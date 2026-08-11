﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿using System;
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
using EasyMovie.Client.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace EasyMovie.Client.Views;

public partial class OnlineSearchView : UserControl
{
    private readonly MovieDbContext _context;
    private bool _disposed;
    private readonly MovieApiService _apiService;
    private readonly OnlineSearchViewModel _vm;
    public event EventHandler? MovieAdded;

    public OnlineSearchView(string? tmdbApiKey = null)
    {
        InitializeComponent();
        _context = DbHelper.CreateContext();
        // 通过 DI 容器解析 ViewModel；DI 不可用时回退手工创建，行为等价
        _vm = App.Services?.GetService<OnlineSearchViewModel>()
              ?? new OnlineSearchViewModel(
                  new MovieService(new MovieRepository(_context), new TagRepository(_context)),
                  new CategoryService(new CategoryRepository(_context)));
        var douban = new DoubanApiClient();
        var tmdb = new TmdbApiClient(tmdbApiKey ?? "");
        _apiService = new MovieApiService(douban, tmdb);
        SourceLabel.Text = LanguageManager.GetString("OnlineSearch_SourceLabel");
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        _disposed = true;
        Unloaded -= OnUnloaded;
        _context.Dispose();
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
                var movie = await MovieApiService.MapToMovieAsync(r, _vm.CategoryService);
                await _vm.MovieService.AddAsync(movie);
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
