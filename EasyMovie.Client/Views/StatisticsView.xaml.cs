using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Controls;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Services;
using EasyMovie.Data;

namespace EasyMovie.Client.Views;

public partial class StatisticsView : UserControl
{
    private readonly MovieDbContext _context;
    private readonly IStatisticsService _statsService;

    // 柱状图最大宽度（像素）
    private const double MaxBarWidth = 500;

    // 分类颜色
    private static readonly string[] CategoryColors =
    {
        "#7C4DFF", "#4CAF50", "#FF9800", "#E91E63", "#00BCD4",
        "#FFC107", "#9C27B0", "#009688", "#F44336", "#3F51B5",
        "#8BC34A", "#FF5722", "#607D8B", "#CDDC39", "#795548"
    };

    public StatisticsView()
    {
        InitializeComponent();
        _context = DbHelper.CreateContext();
        _statsService = new StatisticsService(_context);
        Loaded += async (s, e) => await LoadAsync();
        Unloaded += (s, e) => _context.Dispose();
    }

    private async Task LoadAsync()
    {
        try
        {
            var d = await _statsService.GetStatisticsAsync();
            TotalMoviesText.Text = d.TotalMovies.ToString();
            WatchedText.Text = d.Watched.ToString();
            WantWatchText.Text = d.WantToWatch.ToString();
            AvgRatingText.Text = d.AverageRating.ToString("F1");
            FavoritesText.Text = d.Favorites.ToString();

            // 分类分布 - 水平条形图
            var maxCat = Math.Max(d.CategoryStats.Max(c => (int?)c.Count) ?? 1, 1);
            var catItems = d.CategoryStats.Select((c, i) => new BarItem
            {
                Name = c.Name,
                Count = c.Count,
                BarWidth = Math.Max(4, (double)c.Count / maxCat * MaxBarWidth),
                Color = CategoryColors[i % CategoryColors.Length]
            }).ToList();
            CategoryChart.ItemsSource = catItems;

            // 评分分布 - 水平条形图
            var ratingItems = Enumerable.Range(1, 10).Select(r =>
            {
                var count = d.RatingStats.FirstOrDefault(s => s.Rating == r)?.Count ?? 0;
                return new RatingBarItem
                {
                    Label = r + "分",
                    Count = count,
                    BarWidth = Math.Max(2, (double)count / Math.Max(d.RatingStats.Max(s => (int?)s.Count) ?? 1, 1) * MaxBarWidth)
                };
            }).ToList();
            RatingChart.ItemsSource = ratingItems;

            // 年度趋势 - 双色条形图
            var maxYear = Math.Max(
                d.YearlyStats.Max(y => (int?)y.AddedCount) ?? 1,
                d.YearlyStats.Max(y => (int?)y.WatchedCount) ?? 1);
            var yearlyItems = d.YearlyStats.Select(y => new YearlyBarItem
            {
                YearText = y.Year.ToString(),
                AddedCount = y.AddedCount,
                WatchedCount = y.WatchedCount,
                AddedWidth = Math.Max(2, (double)y.AddedCount / maxYear * MaxBarWidth * 0.5),
                WatchedWidth = Math.Max(2, (double)y.WatchedCount / maxYear * MaxBarWidth * 0.5)
            }).ToList();
            YearlyChart.ItemsSource = yearlyItems;

            // 月度观影 - 水平条形图
            var maxMonth = Math.Max(d.MonthlyStats.Max(m => (int?)m.WatchedCount) ?? 1, 1);
            var monthlyItems = d.MonthlyStats.Select(m => new MonthlyBarItem
            {
                MonthText = m.Month + "月",
                Count = m.WatchedCount,
                BarWidth = Math.Max(2, (double)m.WatchedCount / maxMonth * MaxBarWidth),
                CountText = m.WatchedCount.ToString()
            }).ToList();
            MonthlyChart.ItemsSource = monthlyItems;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"统计加载失败: {ex.Message}\n{ex.StackTrace}", "错误");
        }
    }
}

// 数据模型
public class BarItem : INotifyPropertyChanged
{
    private string _name = "";
    private int _count;
    private double _barWidth;
    private string _color = "#7C4DFF";

    public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }
    public int Count { get => _count; set { _count = value; OnPropertyChanged(); CountText = value + "部"; } }
    public double BarWidth { get => _barWidth; set { _barWidth = value; OnPropertyChanged(); } }
    public string Color { get => _color; set { _color = value; OnPropertyChanged(); } }
    public string CountText { get; private set; } = "0部";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public class RatingBarItem : INotifyPropertyChanged
{
    private string _label = "";
    private int _count;
    private double _barWidth;

    public string Label { get => _label; set { _label = value; OnPropertyChanged(); } }
    public int Count { get => _count; set { _count = value; OnPropertyChanged(); CountText = value + "部"; } }
    public double BarWidth { get => _barWidth; set { _barWidth = value; OnPropertyChanged(); } }
    public string CountText { get; private set; } = "0部";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public class YearlyBarItem : INotifyPropertyChanged
{
    private string _yearText = "";
    private int _addedCount;
    private int _watchedCount;
    private double _addedWidth;
    private double _watchedWidth;

    public string YearText { get => _yearText; set { _yearText = value; OnPropertyChanged(); } }
    public int AddedCount { get => _addedCount; set { _addedCount = value; OnPropertyChanged(); UpdateSummary(); } }
    public int WatchedCount { get => _watchedCount; set { _watchedCount = value; OnPropertyChanged(); UpdateSummary(); } }
    public double AddedWidth { get => _addedWidth; set { _addedWidth = value; OnPropertyChanged(); } }
    public double WatchedWidth { get => _watchedWidth; set { _watchedWidth = value; OnPropertyChanged(); } }
    public string YearSummary { get; private set; } = "";

    private void UpdateSummary() => YearSummary = $"{_addedCount}/{_watchedCount}";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public class MonthlyBarItem : INotifyPropertyChanged
{
    private string _monthText = "";
    private int _count;
    private double _barWidth;
    private string _countText = "0";

    public string MonthText { get => _monthText; set { _monthText = value; OnPropertyChanged(); } }
    public int Count { get => _count; set { _count = value; OnPropertyChanged(); } }
    public double BarWidth { get => _barWidth; set { _barWidth = value; OnPropertyChanged(); } }
    public string CountText { get => _countText; set { _countText = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
