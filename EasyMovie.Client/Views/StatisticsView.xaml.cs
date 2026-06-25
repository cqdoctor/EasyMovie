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
    private const double MaxBarWidth = 200;

    // 分类颜色
    private static readonly string[] CategoryColors =
    {
        "#7C4DFF", "#4CAF50", "#FF9800", "#E91E63", "#00BCD4",
        "#FFC107", "#9C27B0", "#009688", "#F44336", "#3F51B5",
        "#8BC34A", "#FF5722", "#607D8B", "#CDDC39", "#795548"
    };

    private bool _isInitialized;

    public StatisticsView()
    {
        InitializeComponent();
        _context = DbHelper.CreateContext();
        _statsService = new StatisticsService(_context);
        Loaded += async (s, e) => await InitializeAsync();
        IsVisibleChanged += async (_, e) =>
        {
            if (e.NewValue is true && _isInitialized)
                await LoadAsync();
        };
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized) return;
        _isInitialized = true;
        await LoadAsync();
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

            var hours = d.TotalRuntimeMinutes / 60;
            var mins = d.TotalRuntimeMinutes % 60;
            TotalRuntimeText.Text = string.Format(LanguageManager.GetString("Stats_HourFormat"), hours, mins);

            // 观影完成率
            CompletionRateText.Text = d.CompletionRate + "%";
            CompletionRateBar.Width = d.CompletionRate * 1.18; // Max width ~118px (card width 150 - padding 32)

            // 今年观影
            var thisYearHours = d.ThisYearWatchedRuntimeMinutes / 60;
            var thisYearMins = d.ThisYearWatchedRuntimeMinutes % 60;
            ThisYearWatchedText.Text = $"{d.ThisYearWatchedCount}{LanguageManager.GetString("Stats_ThisYearMovies")} · {string.Format(LanguageManager.GetString("Stats_HourFormat"), thisYearHours, thisYearMins)}";

            // 评分分布
            var ratingItems = Enumerable.Range(1, 10).Reverse().Select(r =>
            {
                var count = d.RatingStats.FirstOrDefault(s => s.Rating == r)?.Count ?? 0;
                return new RatingBarItem
                {
                    Label = r + LanguageManager.GetString("Stats_RatingUnit"),
                    Count = count,
                    BarWidth = Math.Max(2, (double)count / Math.Max(d.RatingStats.Max(s => (int?)s.Count) ?? 1, 1) * MaxBarWidth),
                    CountText = count.ToString()
                };
            }).ToList();
            RatingChart.ItemsSource = ratingItems;

            var maxYear = Math.Max(
                d.YearlyStats.Max(y => (int?)y.AddedCount) ?? 1,
                d.YearlyStats.Max(y => (int?)y.WatchedCount) ?? 1);
            var yearlyItems = d.YearlyStats
                .OrderByDescending(y => y.Year)
                .Take(10)
                .Select(y => new YearlyBarItem
                {
                    YearText = y.Year.ToString(),
                    AddedCount = y.AddedCount,
                    WatchedCount = y.WatchedCount,
                    AddedWidth = Math.Max(2, (double)y.AddedCount / maxYear * MaxBarWidth * 0.5),
                    WatchedWidth = Math.Max(2, (double)y.WatchedCount / maxYear * MaxBarWidth * 0.5)
                }).ToList();
            YearlyChart.ItemsSource = yearlyItems;

            var maxMonth = Math.Max(d.MonthlyStats.Max(m => (int?)m.WatchedCount) ?? 1, 1);
            var monthlyItems = d.MonthlyStats.Select(m => new MonthlyBarItem
            {
                MonthText = m.Month + "月",
                Count = m.WatchedCount,
                BarWidth = Math.Max(2, (double)m.WatchedCount / maxMonth * MaxBarWidth),
                CountText = m.WatchedCount.ToString()
            }).ToList();
            MonthlyChart.ItemsSource = monthlyItems;

            var maxDir = Math.Max(d.DirectorStats.Max(p => (int?)p.Count) ?? 1, 1);
            var directorItems = d.DirectorStats.Select((p, i) => new PersonBarItem
            {
                Rank = (i + 1).ToString(),
                Name = p.Name,
                Count = p.Count,
                BarWidth = Math.Max(4, (double)p.Count / maxDir * 70),
                CountText = p.Count + LanguageManager.GetString("Stats_MoviesUnit")
            }).ToList();
            DirectorChart.ItemsSource = directorItems;

            var maxCast = Math.Max(d.CastStats.Max(p => (int?)p.Count) ?? 1, 1);
            var castItems = d.CastStats.Select((p, i) => new PersonBarItem
            {
                Rank = (i + 1).ToString(),
                Name = p.Name,
                Count = p.Count,
                BarWidth = Math.Max(4, (double)p.Count / maxCast * 70),
                CountText = p.Count + LanguageManager.GetString("Stats_MoviesUnit")
            }).ToList();
            CastChart.ItemsSource = castItems;

            var maxCountry = Math.Max(d.CountryStats.Max(c => (int?)c.Count) ?? 1, 1);
            var countryItems = d.CountryStats.Take(12).Select(c => new BarItem
            {
                Name = c.Name,
                Count = c.Count,
                BarWidth = Math.Max(4, (double)c.Count / maxCountry * MaxBarWidth),
                Color = "#00BCD4"
            }).ToList();
            CountryChart.ItemsSource = countryItems;

            var maxRuntime = Math.Max(d.RuntimeStats.Max(r => (int?)r.Count) ?? 1, 1);
            var runtimeItems = d.RuntimeStats.Select(r => new RuntimeBarItem
            {
                Label = r.Label + LanguageManager.GetString("Stats_MinutesUnit"),
                Count = r.Count,
                BarWidth = Math.Max(4, (double)r.Count / maxRuntime * MaxBarWidth),
                CountText = r.Count + LanguageManager.GetString("Stats_MoviesUnit")
            }).ToList();
            RuntimeChart.ItemsSource = runtimeItems;

            // 类型分布
            var maxGenre = Math.Max(d.GenreStats.Max(g => (int?)g.Count) ?? 1, 1);
            var genreItems = d.GenreStats.Take(12).Select(g => new BarItem
            {
                Name = g.Name,
                Count = g.Count,
                BarWidth = Math.Max(4, (double)g.Count / maxGenre * MaxBarWidth),
                Color = "#9C27B0"
            }).ToList();
            GenreChart.ItemsSource = genreItems;

            // 最活跃星期
            var dayOfWeekNames = new[]
            {
                LanguageManager.GetString("Stats_Mon"),
                LanguageManager.GetString("Stats_Tue"),
                LanguageManager.GetString("Stats_Wed"),
                LanguageManager.GetString("Stats_Thu"),
                LanguageManager.GetString("Stats_Fri"),
                LanguageManager.GetString("Stats_Sat"),
                LanguageManager.GetString("Stats_Sun")
            };
            var maxDayOfWeek = Math.Max(d.DayOfWeekStats.Max(s => (int?)s.Count) ?? 1, 1);
            var dayOfWeekItems = d.DayOfWeekStats.Select(s => new DayOfWeekBarItem
            {
                DayName = dayOfWeekNames[s.DayOfWeek],
                Count = s.Count,
                BarWidth = Math.Max(2, (double)s.Count / maxDayOfWeek * MaxBarWidth),
                CountText = s.Count.ToString()
            }).ToList();
            DayOfWeekChart.ItemsSource = dayOfWeekItems;

            // 最长连续观影
            LongestStreakText.Text = d.LongestWatchStreak.ToString();
        }
        catch (Exception ex)
        {
            AppMessageBox.ShowError($"{LanguageManager.GetString("Stats_LoadFailed")}: {ex.Message}");
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
    public int Count { get => _count; set { _count = value; OnPropertyChanged(); CountText = value + LanguageManager.GetString("Msg_MoviesUnit"); } }
    public double BarWidth { get => _barWidth; set { _barWidth = value; OnPropertyChanged(); } }
    public string Color { get => _color; set { _color = value; OnPropertyChanged(); } }
    public string CountText { get; private set; } = "0" + LanguageManager.GetString("Msg_MoviesUnit");

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public class RatingBarItem : INotifyPropertyChanged
{
    private string _label = "";
    private int _count;
    private double _barWidth;
    private string _countText = "";

    public string Label { get => _label; set { _label = value; OnPropertyChanged(); } }
    public int Count { get => _count; set { _count = value; OnPropertyChanged(); } }
    public double BarWidth { get => _barWidth; set { _barWidth = value; OnPropertyChanged(); } }
    public string CountText { get => _countText; set { _countText = value; OnPropertyChanged(); } }

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

public class PersonBarItem : INotifyPropertyChanged
{
    private string _rank = "";
    private string _name = "";
    private int _count;
    private double _barWidth;
    private string _countText = "";

    public string Rank { get => _rank; set { _rank = value; OnPropertyChanged(); } }
    public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }
    public int Count { get => _count; set { _count = value; OnPropertyChanged(); } }
    public double BarWidth { get => _barWidth; set { _barWidth = value; OnPropertyChanged(); } }
    public string CountText { get => _countText; set { _countText = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public class RuntimeBarItem : INotifyPropertyChanged
{
    private string _label = "";
    private int _count;
    private double _barWidth;
    private string _countText = "";

    public string Label { get => _label; set { _label = value; OnPropertyChanged(); } }
    public int Count { get => _count; set { _count = value; OnPropertyChanged(); } }
    public double BarWidth { get => _barWidth; set { _barWidth = value; OnPropertyChanged(); } }
    public string CountText { get => _countText; set { _countText = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public class DayOfWeekBarItem : INotifyPropertyChanged
{
    private string _dayName = "";
    private int _count;
    private double _barWidth;
    private string _countText = "0";

    public string DayName { get => _dayName; set { _dayName = value; OnPropertyChanged(); } }
    public int Count { get => _count; set { _count = value; OnPropertyChanged(); } }
    public double BarWidth { get => _barWidth; set { _barWidth = value; OnPropertyChanged(); } }
    public string CountText { get => _countText; set { _countText = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
