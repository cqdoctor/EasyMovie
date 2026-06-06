using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EasyMovie.Core.Models;
using EasyMovie.Data;
using Microsoft.EntityFrameworkCore;

namespace EasyMovie.Client.Views;

public partial class DashboardView : UserControl
{
    private readonly MovieDbContext _context;
    private bool _isInitialized;
    private bool _isLoading;

    public DashboardView()
    {
        InitializeComponent();
        _context = DbHelper.CreateContext();
        Loaded += async (s, e) =>
        {
            try { await InitializeAsync(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Dashboard init error: {ex}"); }
        };
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized) return;
        _isInitialized = true;
        await LoadDataAsync();
    }

    private async Task LoadDataAsync()
    {
        if (_isLoading) return;
        _isLoading = true;
        try
        {
            var now = DateTime.Now;
            var monthStart = new DateTime(now.Year, now.Month, 1);

            // 总电影数
            var totalMovies = await _context.Movies.CountAsync();
            TotalMoviesText.Text = totalMovies.ToString();

            // 本月观影数
            var monthWatched = await _context.WatchLogs
                .CountAsync(w => w.WatchDate >= monthStart);
            MonthWatchedText.Text = monthWatched.ToString();

            // 总观影时长（小时）
            var totalMinutes = await _context.Movies
                .Where(m => m.WatchLogs.Any())
                .SumAsync(m => (int?)m.Runtime ?? 0);
            TotalHoursText.Text = (totalMinutes / 60.0).ToString("F1") + "h";

            // 平均评分
            var avgRating = await _context.Movies
                .Where(m => m.Rating.HasValue)
                .AverageAsync(m => (double?)m.Rating);
            AvgRatingText.Text = avgRating.HasValue ? avgRating.Value.ToString("F1") : "-";

            // 收藏数
            var favorites = await _context.Movies.CountAsync(m => m.IsFavorite);
            FavoritesText.Text = favorites.ToString();

            // 最近添加的10部
            var recentAdded = await _context.Movies
                .OrderByDescending(m => m.CreatedAt)
                .Take(10)
                .ToListAsync();
            RecentAddedList.ItemsSource = recentAdded;

            // 最近观看的10部
            var recentWatched = await _context.WatchLogs
                .Include(w => w.Movie)
                .OrderByDescending(w => w.WatchDate)
                .Select(w => w.Movie!)
                .Distinct()
                .Take(10)
                .ToListAsync();
            RecentWatchedList.ItemsSource = recentWatched;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Dashboard error: {ex.Message}");
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void MovieCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is Movie movie)
        {
            var mainWindow = Application.Current.MainWindow as MainWindow;
            if (mainWindow != null)
            {
                mainWindow.NavigateTo("Movies");
                mainWindow.ShowMovieDetail(movie);
            }
        }
    }
}