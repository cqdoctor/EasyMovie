using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using EasyMovie.Core.Models;
using EasyMovie.Data;

namespace EasyMovie.Client.Views;

public partial class WatchCalendarView : UserControl
{
    private readonly MovieDbContext _context;
    private readonly WatchLogService _watchLogService;
    private int _displayYear;
    private int _displayMonth;
    private bool _isInitialized;

    public WatchCalendarView()
    {
        InitializeComponent();
        _context = DbHelper.CreateContext();
        _watchLogService = new WatchLogService(_context);

        var now = DateTime.Today;
        _displayYear = now.Year;
        _displayMonth = now.Month;

        Loaded += async (s, e) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        if (_isInitialized) return;
        _isInitialized = true;
        await LoadCalendarAsync();
    }

    private async void PrevMonthBtn_Click(object sender, RoutedEventArgs e)
    {
        _displayMonth--;
        if (_displayMonth < 1) { _displayMonth = 12; _displayYear--; }
        await LoadCalendarAsync();
    }

    private async void NextMonthBtn_Click(object sender, RoutedEventArgs e)
    {
        _displayMonth++;
        if (_displayMonth > 12) { _displayMonth = 1; _displayYear++; }
        await LoadCalendarAsync();
    }

    private async Task LoadCalendarAsync()
    {
        var yearSuffix = LanguageManager.GetString("Msg_YearSuffix");
        MonthYearText.Text = $"{_displayYear}{yearSuffix}{_displayMonth}月";

        var logs = await _watchLogService.GetByMonthAsync(_displayYear, _displayMonth);

        var logsByDay = logs
            .GroupBy(w => w.WatchDate.Day)
            .ToDictionary(g => g.Key, g => g.ToList());

        BuildCalendarGrid(logsByDay);
    }

    private void BuildCalendarGrid(Dictionary<int, List<WatchLog>> logsByDay)
    {
        CalendarGrid.Children.Clear();
        CalendarGrid.ColumnDefinitions.Clear();
        CalendarGrid.RowDefinitions.Clear();

        // 7 columns
        for (int c = 0; c < 7; c++)
            CalendarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Weekday headers
        var weekHeaders = new[]
        {
            "Calendar_Mon", "Calendar_Tue", "Calendar_Wed",
            "Calendar_Thu", "Calendar_Fri", "Calendar_Sat", "Calendar_Sun"
        };

        CalendarGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int c = 0; c < 7; c++)
        {
            var headerText = new TextBlock
            {
                Text = LanguageManager.GetString(weekHeaders[c]),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("MaterialDesignBody"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 8)
            };
            Grid.SetColumn(headerText, c);
            Grid.SetRow(headerText, 0);
            CalendarGrid.Children.Add(headerText);
        }

        // Calculate calendar layout
        var firstDay = new DateTime(_displayYear, _displayMonth, 1);
        int daysInMonth = DateTime.DaysInMonth(_displayYear, _displayMonth);

        // Monday=0 ... Sunday=6
        int startOffset = ((int)firstDay.DayOfWeek + 6) % 7;

        int totalCells = startOffset + daysInMonth;
        int totalRows = (totalCells + 6) / 7;

        var today = DateTime.Today;

        for (int row = 0; row < totalRows; row++)
        {
            CalendarGrid.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(1, GridUnitType.Star),
                MinHeight = 80
            });

            for (int col = 0; col < 7; col++)
            {
                int cellIndex = row * 7 + col;
                int day = cellIndex - startOffset + 1;

                var cell = CreateDayCell(day, day >= 1 && day <= daysInMonth, logsByDay, today);
                Grid.SetColumn(cell, col);
                Grid.SetRow(cell, row + 1); // +1 for header row
                CalendarGrid.Children.Add(cell);
            }
        }
    }

    private Border CreateDayCell(int day, bool isValid, Dictionary<int, List<WatchLog>> logsByDay, DateTime today)
    {
        var border = new Border
        {
            Margin = new Thickness(2),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 4, 6, 4),
            Background = (Brush)FindResource("MaterialDesignCardBackground")
        };

        if (!isValid)
        {
            border.Background = Brushes.Transparent;
            return border;
        }

        bool isToday = today.Year == _displayYear && today.Month == _displayMonth && today.Day == day;
        bool hasWatches = logsByDay.ContainsKey(day);

        if (isToday)
        {
            border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x7C, 0x4D, 0xFF));
            border.BorderThickness = new Thickness(2);
        }

        if (hasWatches)
        {
            var greenBg = new SolidColorBrush(Color.FromArgb(0x18, 0x4C, 0xAF, 0x50));
            border.Background = isToday
                ? new SolidColorBrush(Color.FromArgb(0x20, 0x7C, 0x4D, 0xFF))
                : greenBg;
        }

        var stack = new StackPanel();

        // Day number row with green dot indicator
        var dayRow = new StackPanel { Orientation = Orientation.Horizontal };

        var dayText = new TextBlock
        {
            Text = day.ToString(),
            FontSize = 14,
            FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
            Foreground = isToday
                ? new SolidColorBrush(Color.FromRgb(0x7C, 0x4D, 0xFF))
                : (Brush)FindResource("MaterialDesignBody"),
            VerticalAlignment = VerticalAlignment.Center
        };
        dayRow.Children.Add(dayText);

        if (hasWatches)
        {
            var dot = new Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)),
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            dayRow.Children.Add(dot);
        }

        stack.Children.Add(dayRow);

        // Movie titles
        if (hasWatches)
        {
            var movies = logsByDay[day];
            var moviesWatchedText = LanguageManager.GetString("Calendar_MoviesWatched");

            foreach (var log in movies.Take(3))
            {
                var titleBlock = new TextBlock
                {
                    Text = log.Movie?.Title ?? "—",
                    FontSize = 11,
                    Foreground = (Brush)FindResource("MaterialDesignBody"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 1, 0, 0),
                    ToolTip = $"{log.Movie?.Title ?? "—"}{(log.Rating.HasValue ? $" ({log.Rating}分)" : "")}"
                };
                stack.Children.Add(titleBlock);
            }

            if (movies.Count > 3)
            {
                var moreText = new TextBlock
                {
                    Text = $"+{movies.Count - 3} {moviesWatchedText}",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x89, 0x89, 0x89)),
                    Margin = new Thickness(0, 1, 0, 0)
                };
                stack.Children.Add(moreText);
            }
        }
        else if (isValid)
        {
            var noRecords = new TextBlock
            {
                Text = LanguageManager.GetString("Calendar_NoRecords"),
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x89, 0x89, 0x89)),
                Margin = new Thickness(0, 2, 0, 0)
            };
            stack.Children.Add(noRecords);
        }

        border.Child = stack;
        return border;
    }
}
