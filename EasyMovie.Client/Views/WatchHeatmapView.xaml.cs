using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EasyMovie.Data;
using Microsoft.EntityFrameworkCore;

namespace EasyMovie.Client.Views;

public partial class WatchHeatmapView : UserControl
{
    private const int CellSize = 14;
    private const int CellGap = 2;
    private const int WeeksToShow = 53;

    private static string L(string key) => LanguageManager.GetString(key);

    private static string LocDay(int i) => i switch
    {
        0 => "Heatmap_DayMon", 1 => "Heatmap_DayTue", 2 => "Heatmap_DayWed",
        3 => "Heatmap_DayThu", 4 => "Heatmap_DayFri", 5 => "Heatmap_DaySat",
        6 => "Heatmap_DaySun", _ => "Heatmap_DayMon"
    };

    public WatchHeatmapView()
    {
        InitializeComponent();
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await Task.Delay(50);
            await LoadHeatmapAsync();
        }
        catch (Exception ex)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                SummaryText.Text = $"加载失败: {ex.Message}";
            }));
        }
    }

    private async Task LoadHeatmapAsync()
    {
        using var ctx = DbHelper.CreateContext();
        var endDate = DateTime.Today;
        var startDate = endDate.AddDays(-(WeeksToShow * 7 - 1));

        var daysSinceMonday = (int)startDate.DayOfWeek - 1;
        if (daysSinceMonday < 0) daysSinceMonday += 7;
        startDate = startDate.AddDays(-daysSinceMonday);

        var watchLogs = await ctx.WatchLogs
            .Include(w => w.Movie)
            .Where(w => w.WatchDate >= startDate && w.WatchDate <= endDate)
            .ToListAsync();

        var dailyCounts = watchLogs
            .GroupBy(w => w.WatchDate.Date)
            .ToDictionary(g => g.Key, g => g.ToList());

        var totalDays = dailyCounts.Count;
        var totalMovies = watchLogs.Count;
        var maxPerDay = dailyCounts.Any() ? dailyCounts.Max(d => d.Value.Count) : 0;
        var longestStreak = CalcLongestStreak(dailyCounts, startDate, endDate);

        SummaryText.Text = $"{startDate:yyyy-MM-dd} - {endDate:yyyy-MM-dd} | " +
                           string.Format(L("Heatmap_Summary"), totalMovies, totalDays, longestStreak);

        BuildDayLabels();

        BuildMonthLabels(startDate, endDate);
        BuildStatsCards(totalMovies, totalDays, maxPerDay, longestStreak);
        BuildHeatmapCells(startDate, endDate, dailyCounts, maxPerDay);
        BuildTopDaysList(watchLogs);
        ApplyLegendColors(IsDarkTheme());
    }

    private static int CalcLongestStreak(Dictionary<DateTime, List<EasyMovie.Core.Models.WatchLog>> dailyCounts,
        DateTime startDate, DateTime endDate)
    {
        var longest = 0;
        var current = 0;
        for (var d = startDate; d <= endDate; d = d.AddDays(1))
        {
            if (dailyCounts.ContainsKey(d))
            {
                current++;
                if (current > longest) longest = current;
            }
            else current = 0;
        }
        return longest;
    }

    private Brush GetBodyLightBrush()
    {
        return TryFindResource("MaterialDesignBodyLight") as Brush
               ?? Application.Current?.TryFindResource("MaterialDesignBodyLight") as Brush
               ?? new SolidColorBrush(Color.FromRgb(117, 117, 117));
    }

    private void BuildDayLabels()
    {
        DayLabelsPanel.Children.Clear();
        var brush = GetBodyLightBrush();
        for (var i = 0; i < 7; i++)
        {
            var tb = new TextBlock
            {
                Text = L(LocDay(i)),
                FontSize = 10,
                Height = CellSize + CellGap,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = brush
            };
            DayLabelsPanel.Children.Add(tb);
        }
    }

    private void BuildMonthLabels(DateTime start, DateTime end)
    {
        MonthLabelsGrid.Children.Clear();
        MonthLabelsGrid.ColumnDefinitions.Clear();

        var totalDays = (end - start).Days + 1;
        var totalWeeks = (int)Math.Ceiling(totalDays / 7.0);

        for (var w = 0; w < totalWeeks; w++)
            MonthLabelsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CellSize + CellGap) });

        var brush = GetBodyLightBrush();
        var prevMonth = -1;
        for (var w = 0; w < totalWeeks; w++)
        {
            var date = start.AddDays(w * 7);
            var midDate = date.AddDays(3);
            if (midDate.Month != prevMonth && midDate <= end)
            {
                var tb = new TextBlock
                {
                    Text = midDate.ToString("MMM", CultureInfo.CurrentCulture),
                    FontSize = 10,
                    Foreground = brush
                };
                Grid.SetColumn(tb, w);
                MonthLabelsGrid.Children.Add(tb);
                prevMonth = midDate.Month;
            }
        }
    }

    private void BuildStatsCards(int totalMovies, int totalDays, int maxPerDay, int longestStreak)
    {
        StatsPanel.Children.Clear();
        AddStatCard(L("Heatmap_TotalMovies"), string.Format(L("Heatmap_WatchedCount"), totalMovies), "#7C4DFF", "#B388FF");
        AddStatCard(L("Heatmap_WatchedDays"), string.Format(L("Heatmap_DayCount"), totalDays), "#00BFA5", "#64FFDA");
        AddStatCard(L("Heatmap_MaxPerDay"), string.Format(L("Heatmap_WatchedCount"), maxPerDay), "#FF6D00", "#FFD180");
        AddStatCard(L("Heatmap_LongestStreak"), string.Format(L("Heatmap_DayCount"), longestStreak), "#D500F9", "#EA80FC");
    }

    private void AddStatCard(string label, string value, string color1, string color2)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(12),
            Width = 155,
            Padding = new Thickness(16, 14, 16, 14),
            Margin = new Thickness(0, 0, 10, 10),
            Background = new LinearGradientBrush(
                (Color)ColorConverter.ConvertFromString(color1)!,
                (Color)ColorConverter.ConvertFromString(color2)!,
                new Point(0, 0), new Point(1, 1)),
        };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = label, FontSize = 11,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFFCC")!)
        });
        sp.Children.Add(new TextBlock
        {
            Text = value, FontSize = 18, FontWeight = FontWeights.Bold,
            Foreground = Brushes.White
        });
        border.Child = sp;
        StatsPanel.Children.Add(border);
    }

    private void BuildHeatmapCells(DateTime start, DateTime end,
        Dictionary<DateTime, List<EasyMovie.Core.Models.WatchLog>> dailyCounts, int maxPerDay)
    {
        HeatmapGrid.Children.Clear();
        HeatmapGrid.ColumnDefinitions.Clear();
        HeatmapGrid.RowDefinitions.Clear();

        var totalDays = (end - start).Days + 1;
        var totalWeeks = (int)Math.Ceiling(totalDays / 7.0);

        for (var w = 0; w < totalWeeks; w++)
            HeatmapGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CellSize + CellGap) });
        for (var r = 0; r < 7; r++)
            HeatmapGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CellSize + CellGap) });

        var isDark = IsDarkTheme();

        for (var w = 0; w < totalWeeks; w++)
        {
            for (var d = 0; d < 7; d++)
            {
                var date = start.AddDays(w * 7 + d);
                if (date > end) continue;

                var count = dailyCounts.TryGetValue(date, out var logs) ? logs.Count : 0;
                var color = GetHeatColor(count, maxPerDay, isDark);

                var cell = new Border
                {
                    Width = CellSize,
                    Height = CellSize,
                    CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush(color),
                    Tag = date,
                    ToolTip = BuildTooltip(date, count, logs)
                };

                Grid.SetColumn(cell, w);
                Grid.SetRow(cell, d);
                HeatmapGrid.Children.Add(cell);
            }
        }
    }

    private static object BuildTooltip(DateTime date, int count, List<EasyMovie.Core.Models.WatchLog>? logs)
    {
        var sp = new StackPanel();
        var dayIdx = (int)date.DayOfWeek == 0 ? 6 : (int)date.DayOfWeek - 1;
        sp.Children.Add(new TextBlock
        {
            Text = $"{date:yyyy-MM-dd} ({L("Heatmap_Week")}{L(LocDay(dayIdx))})",
            FontWeight = FontWeights.Bold,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 4)
        });
        sp.Children.Add(new TextBlock
        {
            Text = count == 0 ? L("Heatmap_NoRecord") : string.Format(L("Heatmap_WatchedCount"), count),
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 4)
        });

        if (logs != null && logs.Count > 0)
        {
            foreach (var log in logs.Take(5))
                sp.Children.Add(new TextBlock
                {
                    Text = $"  - {log.Movie?.Title ?? L("Heatmap_Unknown")}",
                    FontSize = 11,
                    Foreground = Brushes.Gray
                });
            if (logs.Count > 5)
                sp.Children.Add(new TextBlock
                {
                    Text = $"  ... {string.Format(L("Heatmap_MoreMovies"), logs.Count - 5)}",
                    FontSize = 11,
                    Foreground = Brushes.Gray
                });
        }

        return sp;
    }

    private static Color GetHeatColor(int count, int maxPerDay, bool isDark)
    {
        if (count == 0)
            return isDark ? Color.FromRgb(30, 30, 35) : Color.FromRgb(235, 237, 240);

        var levels = new[]
        {
            Color.FromRgb(155, 233, 168),
            Color.FromRgb(64, 196, 99),
            Color.FromRgb(48, 161, 78),
            Color.FromRgb(33, 110, 57),
        };

        if (maxPerDay <= 0) return levels[0];
        var ratio = (double)count / maxPerDay;
        var idx = ratio switch
        {
            <= 0.25 => 0,
            <= 0.5 => 1,
            <= 0.75 => 2,
            _ => 3
        };
        return levels[idx];
    }

    private void ApplyLegendColors(bool isDark)
    {
        var legendBorders = new[] { Legend0, Legend1, Legend2, Legend3, Legend4 };
        for (var i = 0; i < 5; i++)
        {
            legendBorders[i].Background = new SolidColorBrush(i == 0
                ? (isDark ? Color.FromRgb(30, 30, 35) : Color.FromRgb(235, 237, 240))
                : GetHeatColor(i, 4, isDark));
        }
    }

    private void BuildTopDaysList(List<EasyMovie.Core.Models.WatchLog> watchLogs)
    {
        TopDaysList.Items.Clear();

        var top = watchLogs
            .GroupBy(w => w.WatchDate.Date)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => new
            {
                DateStr = g.Key.ToString("yyyy-MM-dd"),
                Count = g.Count(),
                Titles = string.Join(" / ", g.Select(w => w.Movie?.Title ?? "未知").Take(4))
            })
            .ToList();

        var bodyBrush = GetBodyLightBrush();

        foreach (var item in top)
        {
            var grid = new Grid { Margin = new Thickness(0, 3, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var dateTb = new TextBlock { Text = item.DateStr, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(dateTb, 0);

            var countTb = new TextBlock
            {
                Text = string.Format(L("Heatmap_WatchedCount"), item.Count),
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(countTb, 1);

            var titlesTb = new TextBlock
            {
                Text = item.Titles,
                FontSize = 12,
                Foreground = bodyBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(titlesTb, 2);

            grid.Children.Add(dateTb);
            grid.Children.Add(countTb);
            grid.Children.Add(titlesTb);
            TopDaysList.Items.Add(grid);
        }
    }

    private static bool IsDarkTheme()
    {
        return EasyMovie.Core.AppSettings.IsDarkTheme;
    }
}