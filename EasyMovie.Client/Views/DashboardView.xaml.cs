using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EasyMovie.Core.Models;
using EasyMovie.Data;
using Microsoft.EntityFrameworkCore;

namespace EasyMovie.Client.Views;

public partial class DashboardView : UserControl
{
    private readonly MovieDbContext _context;
    private bool _isInitialized;
    private bool _isLoading;

    private static readonly Color[] BarColors = new[]
    {
        Color.FromRgb(124, 77, 255),   // purple
        Color.FromRgb(76, 175, 80),    // green
        Color.FromRgb(255, 152, 0),    // orange
        Color.FromRgb(233, 30, 99),    // pink
        Color.FromRgb(0, 188, 212),    // cyan
        Color.FromRgb(156, 39, 176),   // deep purple
        Color.FromRgb(255, 193, 7),    // amber
        Color.FromRgb(63, 81, 181),    // indigo
        Color.FromRgb(244, 67, 54),    // red
        Color.FromRgb(0, 150, 136),    // teal
    };

    public DashboardView()
    {
        InitializeComponent();
        _context = DbHelper.CreateContext();
        SetGreeting();
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

    private void SetGreeting()
    {
        var hour = DateTime.Now.Hour;
        string greeting;
        string emoji;
        if (hour < 12)
        {
            greeting = LanguageManager.GetString("Dashboard_GreetingMorning");
            emoji = "☀️";
        }
        else if (hour < 18)
        {
            greeting = LanguageManager.GetString("Dashboard_GreetingAfternoon");
            emoji = "🌤️";
        }
        else
        {
            greeting = LanguageManager.GetString("Dashboard_GreetingEvening");
            emoji = "🌙";
        }
        GreetingText.Text = greeting;
        GreetingEmoji.Text = emoji;
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

            // 类型分布
            await LoadGenreDistributionAsync();

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

    private async Task LoadGenreDistributionAsync()
    {
        GenreChartPanel.Children.Clear();

        try
        {
            // 统计有电影的顶级分类（ParentId == null）+ 电影的分布
            var genreData = await _context.Categories
                .Where(c => c.Movies.Any() && c.ParentId == null)
                .Select(c => new { c.Name, Count = c.Movies.Count })
                .OrderByDescending(x => x.Count)
                .Take(10)
                .ToListAsync();

            // 同时统计没有分类的电影
            var uncategorized = await _context.Movies.CountAsync(m => m.CategoryId == null);

            if (genreData.Count == 0 && uncategorized == 0)
            {
                GenreChartPanel.Children.Add(new TextBlock
                {
                    Text = LanguageManager.GetString("Heatmap_NoRecord"),
                    FontSize = 13,
                    Foreground = TryFindBrush("MaterialDesignHintForeground", Color.FromRgb(117, 117, 117))
                });
                return;
            }

            var max = Math.Max(genreData.Max(x => x.Count), uncategorized);

            int idx = 0;
            foreach (var item in genreData)
            {
                var bar = CreateGenreBar(item.Name, item.Count, max, BarColors[idx % BarColors.Length]);
                GenreChartPanel.Children.Add(bar);
                idx++;
            }

            // 未分类
            if (uncategorized > 0)
            {
                var bar = CreateGenreBar(
                    LanguageManager.GetString("MovieLib_Uncategorized"),
                    uncategorized, max, Color.FromRgb(158, 158, 158));
                GenreChartPanel.Children.Add(bar);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Genre chart error: {ex}");
        }
    }

    private static FrameworkElement CreateGenreBar(string name, int count, int max, Color color)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2), Height = 18 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(35) });

        // 类型名称
        var nameTb = new TextBlock
        {
            Text = name,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Application.Current.TryFindResource("MaterialDesignBody") as Brush ?? Brushes.White
        };
        Grid.SetColumn(nameTb, 0);
        row.Children.Add(nameTb);

        // 比例进度条：用 Grid 列宽实现真正的百分比填充
        var pct = max > 0 ? (double)count / max : 0;
        var pctClamped = Math.Max(pct, 0.02); // 最小 2%，保证可见
        var barContainer = new Border
        {
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(8, 0, 4, 0),
            Background = Application.Current.TryFindResource("MaterialDesignDivider") as Brush
                ?? new SolidColorBrush(Color.FromRgb(80, 80, 80)),
            ClipToBounds = true
        };
        var barGrid = new Grid();
        barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(pctClamped, GridUnitType.Star) });
        barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - pctClamped, GridUnitType.Star) });
        var filledBar = new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(color),
            MinWidth = 4
        };
        Grid.SetColumn(filledBar, 0);
        barGrid.Children.Add(filledBar);
        barContainer.Child = barGrid;
        Grid.SetColumn(barContainer, 1);
        row.Children.Add(barContainer);

        // 数量
        var countTb = new TextBlock
        {
            Text = count.ToString(),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(4, 0, 0, 0),
            Foreground = Application.Current.TryFindResource("MaterialDesignBody") as Brush ?? Brushes.White
        };
        Grid.SetColumn(countTb, 2);
        row.Children.Add(countTb);

        return row;
    }

    private static Brush TryFindBrush(string key, Color fallback)
    {
        var brush = Application.Current.TryFindResource(key) as Brush;
        if (brush != null) return brush;
        var solid = new SolidColorBrush(fallback);
        solid.Freeze();
        return solid;
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

    private async void RandomPickBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var count = await _context.Movies.CountAsync();
            if (count == 0)
            {
                AppMessageBox.ShowInfo(
                    LanguageManager.GetString("Msg_NoRecommendData"),
                    LanguageManager.GetString("Msg_Hint"));
                return;
            }

            var rand = new Random();
            var skip = rand.Next(count);
            var movie = await _context.Movies
                .OrderBy(m => m.Id)
                .Skip(skip)
                .FirstOrDefaultAsync();

            if (movie != null)
            {
                var mainWindow = Application.Current.MainWindow as MainWindow;
                mainWindow?.NavigateTo("Movies");
                mainWindow?.ShowMovieDetail(movie);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Random pick error: {ex.Message}");
        }
    }
}