using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

    // Heat map gradient colors (GitHub contribution style)
    private static readonly Color HeatLevel0 = Color.FromArgb(0x00, 0x4C, 0xAF, 0x50); // transparent
    private static readonly Color HeatLevel1 = Color.FromArgb(0x20, 0x4C, 0xAF, 0x50); // light green
    private static readonly Color HeatLevel2 = Color.FromArgb(0x45, 0x4C, 0xAF, 0x50); // medium green
    private static readonly Color HeatLevel3 = Color.FromArgb(0x70, 0x4C, 0xAF, 0x50); // strong green

    // Weekend tint
    private static readonly Color WeekendTint = Color.FromArgb(0x08, 0x7C, 0x4D, 0xFF);

    // Film icon path data (simplified clapperboard)
    private const string FilmIconData = "M2,6L6,2L7,6L2,6M7,6L8,2L12,2L11,6L7,6M12,2L16,2L16,6L11,6M2,6L2,20L16,20L16,6M5,9L13,9L13,17L5,17Z";

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

        BuildSummaryBar(logs);
        BuildCalendarGrid(logsByDay);
    }

    private void BuildSummaryBar(List<WatchLog> logs)
    {
        SummaryPanel.Children.Clear();

        if (logs.Count == 0)
        {
            SummaryBar.Visibility = Visibility.Collapsed;
            return;
        }

        SummaryBar.Visibility = Visibility.Visible;

        var bodyBrush = (Brush)FindResource("MaterialDesignBody");
        var hintBrush = new SolidColorBrush(Color.FromRgb(0x89, 0x89, 0x89));

        // Total movies
        var moviesLabel = LanguageManager.GetString("Calendar_SummaryMovies");
        var moviesValue = string.Format(LanguageManager.GetString("Calendar_SummaryMoviesCount"), logs.Count);
        SummaryPanel.Children.Add(CreateSummaryItem(moviesLabel, moviesValue, bodyBrush, hintBrush));

        // Separator
        SummaryPanel.Children.Add(CreateSummarySeparator());

        // Total watch hours
        var totalMinutes = logs
            .Where(l => l.Movie?.Runtime.HasValue == true)
            .Sum(l => l.Movie!.Runtime!.Value);
        var hours = totalMinutes / 60.0;
        var hoursLabel = LanguageManager.GetString("Calendar_SummaryHours");
        var hoursValue = string.Format(LanguageManager.GetString("Calendar_SummaryHoursValue"), Math.Round(hours, 1));
        SummaryPanel.Children.Add(CreateSummaryItem(hoursLabel, hoursValue, bodyBrush, hintBrush));

        // Separator
        SummaryPanel.Children.Add(CreateSummarySeparator());

        // Most watched genre
        var genreLabel = LanguageManager.GetString("Calendar_SummaryGenre");
        var genreGroups = logs
            .Where(l => l.Movie?.Category != null)
            .GroupBy(l => l.Movie!.Category!.Name)
            .OrderByDescending(g => g.Count())
            .ToList();
        var genreValue = genreGroups.Count > 0
            ? $"{genreGroups.First().Key} ({genreGroups.First().Count()})"
            : LanguageManager.GetString("Calendar_NoGenre");
        SummaryPanel.Children.Add(CreateSummaryItem(genreLabel, genreValue, bodyBrush, hintBrush));
    }

    private StackPanel CreateSummaryItem(string label, string value, Brush bodyBrush, Brush hintBrush)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
        sp.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = hintBrush
        });
        sp.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = bodyBrush,
            Margin = new Thickness(0, 2, 0, 0)
        });
        return sp;
    }

    private Rectangle CreateSummarySeparator()
    {
        return new Rectangle
        {
            Width = 1,
            Height = 30,
            Fill = new SolidColorBrush(Color.FromArgb(0x30, 0x89, 0x89, 0x89)),
            Margin = new Thickness(0, 0, 24, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
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
            bool isWeekend = c >= 5;
            var headerText = new TextBlock
            {
                Text = LanguageManager.GetString(weekHeaders[c]),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = isWeekend
                    ? new SolidColorBrush(Color.FromRgb(0x7C, 0x4D, 0xFF))
                    : (Brush)FindResource("MaterialDesignBody"),
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
                MinHeight = 90
            });

            for (int col = 0; col < 7; col++)
            {
                int cellIndex = row * 7 + col;
                int day = cellIndex - startOffset + 1;

                var cell = CreateDayCell(day, day >= 1 && day <= daysInMonth, logsByDay, today, col);
                Grid.SetColumn(cell, col);
                Grid.SetRow(cell, row + 1); // +1 for header row
                CalendarGrid.Children.Add(cell);
            }
        }
    }

    private Border CreateDayCell(int day, bool isValid, Dictionary<int, List<WatchLog>> logsByDay, DateTime today, int column)
    {
        var border = new Border
        {
            Margin = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 4, 6, 4),
            Background = (Brush)FindResource("MaterialDesignCardBackground")
        };

        if (!isValid)
        {
            border.Background = Brushes.Transparent;
            return border;
        }

        bool isToday = today.Year == _displayYear && today.Month == _displayMonth && today.Day == day;
        bool isWeekend = column >= 5;
        bool hasWatches = logsByDay.ContainsKey(day);
        int movieCount = hasWatches ? logsByDay[day].Count : 0;

        // Weekend highlight tint
        if (isWeekend && !hasWatches)
        {
            border.Background = new SolidColorBrush(WeekendTint);
        }

        // Heat map background based on movie count
        if (hasWatches)
        {
            var heatColor = movieCount switch
            {
                1 => HeatLevel1,
                2 => HeatLevel2,
                _ => HeatLevel3
            };

            if (isWeekend)
            {
                // Blend weekend tint with heat color
                var blended = BlendColors(WeekendTint, heatColor);
                border.Background = new SolidColorBrush(blended);
            }
            else
            {
                border.Background = new SolidColorBrush(heatColor);
            }
        }

        // Today styling with pulse border
        if (isToday)
        {
            var todayBorder = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x7C, 0x4D, 0xFF)),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(8),
                Opacity = 1.0
            };

            // Pulse animation for today
            var pulseStoryboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
            var opacityAnim = new DoubleAnimationUsingKeyFrames();
            opacityAnim.BeginTime = TimeSpan.Zero;
            opacityAnim.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = 1.0 });
            opacityAnim.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.2)), Value = 0.4 });
            opacityAnim.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.4)), Value = 1.0 });
            Storyboard.SetTarget(opacityAnim, todayBorder);
            Storyboard.SetTargetProperty(opacityAnim, new PropertyPath("(Border.Opacity)"));
            pulseStoryboard.Children.Add(opacityAnim);

            todayBorder.Loaded += (s, e) => pulseStoryboard.Begin();

            var innerStack = new StackPanel();
            PopulateCellContent(innerStack, day, hasWatches, logsByDay, isToday, movieCount);
            todayBorder.Child = innerStack;

            border.Child = todayBorder;
            border.Padding = new Thickness(0);
        }
        else
        {
            var stack = new StackPanel();
            PopulateCellContent(stack, day, hasWatches, logsByDay, isToday, movieCount);
            border.Child = stack;
        }

        // Hover effect
        if (isValid)
        {
            border.MouseEnter += (s, e) =>
            {
                border.RenderTransform = new ScaleTransform(1.02, 1.02);
                border.RenderTransformOrigin = new Point(0.5, 0.5);
                if (hasWatches)
                {
                    border.Margin = new Thickness(1);
                    border.Padding = new Thickness(7, 5, 7, 5);
                }
            };
            border.MouseLeave += (s, e) =>
            {
                border.RenderTransform = Transform.Identity;
                border.Margin = new Thickness(2);
                border.Padding = new Thickness(6, 4, 6, 4);
            };
            border.Cursor = hasWatches ? Cursors.Hand : Cursors.Arrow;
        }

        return border;
    }

    private void PopulateCellContent(StackPanel stack, int day, bool hasWatches,
        Dictionary<int, List<WatchLog>> logsByDay, bool isToday, int movieCount)
    {
        // Day number row with count badge
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
            // Count badge instead of simple dot
            var countBadge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x4C, 0xAF, 0x50)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            countBadge.Child = new TextBlock
            {
                Text = movieCount.ToString(),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            dayRow.Children.Add(countBadge);
        }

        stack.Children.Add(dayRow);

        // Movie entries
        if (hasWatches)
        {
            var movies = logsByDay[day];
            var moviesWatchedText = LanguageManager.GetString("Calendar_MoviesWatched");
            var ratingStar = LanguageManager.GetString("Calendar_RatingStar");

            foreach (var log in movies.Take(3))
            {
                var movieRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 2, 0, 0)
                };

                // Poster thumbnail (circular, 20x20)
                if (log.Movie?.PosterData is { Length: > 0 })
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.StreamSource = new MemoryStream(log.Movie.PosterData);
                        bmp.EndInit();
                        bmp.Freeze();

                        var posterClip = new Border
                        {
                            Width = 20,
                            Height = 20,
                            CornerRadius = new CornerRadius(10),
                            ClipToBounds = true,
                            Margin = new Thickness(0, 0, 4, 0),
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        posterClip.Child = new Image
                        {
                            Source = bmp,
                            Stretch = Stretch.UniformToFill,
                            Width = 20,
                            Height = 20
                        };
                        movieRow.Children.Add(posterClip);
                    }
                    catch
                    {
                        // If poster fails to load, skip thumbnail
                    }
                }

                // Title
                var titleBlock = new TextBlock
                {
                    Text = log.Movie?.Title ?? "—",
                    FontSize = 11,
                    Foreground = (Brush)FindResource("MaterialDesignBody"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
                movieRow.Children.Add(titleBlock);

                // Rating star
                if (log.Rating.HasValue)
                {
                    var ratingText = new TextBlock
                    {
                        Text = $"{ratingStar}{log.Rating}",
                        FontSize = 9,
                        Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(3, 0, 0, 0)
                    };
                    movieRow.Children.Add(ratingText);
                }

                // Tooltip with full info
                var tooltipText = log.Movie?.Title ?? "—";
                if (log.Rating.HasValue) tooltipText += $" ({log.Rating}分)";
                ToolTipService.SetToolTip(movieRow, tooltipText);

                stack.Children.Add(movieRow);
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
        else
        {
            // Empty state - film icon
            var iconPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 4, 0, 0),
                Opacity = 0.2
            };
            var filmIcon = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(FilmIconData),
                Fill = (Brush)FindResource("MaterialDesignBody"),
                Width = 14,
                Height = 14,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center
            };
            iconPanel.Children.Add(filmIcon);
            stack.Children.Add(iconPanel);
        }
    }

    private static Color BlendColors(Color c1, Color c2)
    {
        // Simple alpha blending: c2 over c1
        double a1 = c1.A / 255.0;
        double a2 = c2.A / 255.0;
        double aOut = a1 + a2 * (1 - a1);
        if (aOut == 0) return Colors.Transparent;
        double r = (c1.R * a1 + c2.R * a2 * (1 - a1)) / aOut;
        double g = (c1.G * a1 + c2.G * a2 * (1 - a1)) / aOut;
        double b = (c1.B * a1 + c2.B * a2 * (1 - a1)) / aOut;
        return Color.FromArgb((byte)(aOut * 255), (byte)r, (byte)g, (byte)b);
    }
}
