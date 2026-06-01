using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EasyMovie.Core.Models;
using EasyMovie.Data;

namespace EasyMovie.Client.Views;

public partial class WatchDiaryView : UserControl
{
    private WatchLogService? _svc;
    private int _currentPage;
    private const int PageSize = 50;
    private int _totalCount;
    private List<WatchLog> _allLoadedLogs = new();

    public WatchDiaryView()
    {
        InitializeComponent();
        Loaded += async (_, _) => { await InitializeAsync(); await LoadAsync(); };
        IsVisibleChanged += async (_, e) =>
        {
            if (e.NewValue is true && _svc != null)
                await LoadAsync();
        };
    }

    public async Task InitializeAsync()
    {
        if (_svc != null) return;
        var ctx = DbHelper.CreateContext();
        _svc = new WatchLogService(ctx);
    }

    private async Task LoadAsync(DateTime? filterDate = null)
    {
        if (_svc == null) return;
        DiaryPanel.Children.Clear();
        _allLoadedLogs.Clear();
        _currentPage = 0;
        _totalCount = await _svc.GetCountAsync();

        var logs = await _svc.GetAllWithMovieAsync(0, PageSize);
        _allLoadedLogs.AddRange(logs);
        _currentPage = 1;

        var displayLogs = filterDate.HasValue
            ? logs.Where(l => l.WatchDate.Date == filterDate.Value.Date).ToList()
            : logs;

        RenderLogs(displayLogs, true);

        var displayed = filterDate.HasValue ? displayLogs.Count : _allLoadedLogs.Count;
        LoadMoreBtn.Visibility = !filterDate.HasValue && displayed < _totalCount
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void LoadMore_Click(object sender, RoutedEventArgs e)
    {
        if (_svc == null) return;
        LoadMoreBtn.IsEnabled = false;
        try
        {
            var logs = await _svc.GetAllWithMovieAsync(_currentPage * PageSize, PageSize);
            _allLoadedLogs.AddRange(logs);
            _currentPage++;

            RenderLogs(logs, false);

            LoadMoreBtn.Visibility = _allLoadedLogs.Count < _totalCount
                ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            LoadMoreBtn.IsEnabled = true;
        }
    }

    private void RenderLogs(List<WatchLog> logs, bool isFirstPage)
    {
        if (logs.Count == 0 && isFirstPage)
        {
            DiaryPanel.Children.Add(new TextBlock
            {
                Text = LanguageManager.GetString("WatchLog_DiaryEmpty"),
                FontSize = 14,
                Foreground = SafeFindBrush("MaterialDesignHintForeground", Color.FromRgb(117, 117, 117)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 40, 0, 0)
            });
            return;
        }

        DateTime? lastDate = null;
        foreach (var log in logs)
        {
            if (lastDate != log.WatchDate.Date)
            {
                lastDate = log.WatchDate.Date;
                var isFirst = logs.IndexOf(log) == 0 && isFirstPage;
                var dateHeader = new TextBlock
                {
                    Text = log.WatchDate.ToString("yyyy年MM月dd日 dddd"),
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    Foreground = SafeFindBrush("MaterialDesignBody", Colors.White),
                    Margin = new Thickness(0, isFirst ? 0 : 16, 0, 8)
                };
                DiaryPanel.Children.Add(dateHeader);
            }

            var card = new Border
            {
                Background = SafeFindBrush("MaterialDesignCardBackground", Color.FromRgb(45, 45, 45)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 6)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (log.Movie?.PosterData is { Length: > 0 })
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = new System.IO.MemoryStream(log.Movie.PosterData); bmp.EndInit(); bmp.Freeze();
                    var poster = new Image { Source = bmp, Width = 40, Height = 56, Stretch = Stretch.UniformToFill, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 10, 0) };
                    RenderOptions.SetBitmapScalingMode(poster, BitmapScalingMode.HighQuality);
                    Grid.SetColumn(poster, 0);
                    grid.Children.Add(poster);
                }
                catch { }
            }

            var info = new StackPanel();
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(new TextBlock { Text = log.Movie?.Title ?? "", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = SafeFindBrush("MaterialDesignBody", Colors.White) });
            if (log.Rating.HasValue) titleRow.Children.Add(new TextBlock { Text = $"  ⭐{log.Rating}", FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(255, 215, 0)), VerticalAlignment = VerticalAlignment.Center });
            info.Children.Add(titleRow);

            if (!string.IsNullOrEmpty(log.Location)) info.Children.Add(new TextBlock { Text = "📍 " + log.Location, FontSize = 11, Foreground = SafeFindBrush("MaterialDesignBodyLight", Color.FromRgb(180, 180, 180)) });
            if (!string.IsNullOrEmpty(log.Companion)) info.Children.Add(new TextBlock { Text = "👥 " + log.Companion, FontSize = 11, Foreground = SafeFindBrush("MaterialDesignBodyLight", Color.FromRgb(180, 180, 180)) });
            if (!string.IsNullOrEmpty(log.Notes)) info.Children.Add(new TextBlock { Text = log.Notes, FontSize = 11, Foreground = SafeFindBrush("MaterialDesignBodyLight", Color.FromRgb(180, 180, 180)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });

            Grid.SetColumn(info, 1);
            grid.Children.Add(info);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            var editBtn = new Button
            {
                Style = (Style)Application.Current.FindResource("MaterialDesignIconForegroundButton"),
                Width = 24, Height = 24, Tag = log.Id,
                ToolTip = LanguageManager.GetString("WatchLog_Edit")
            };
            editBtn.Content = new MaterialDesignThemes.Wpf.PackIcon { Kind = MaterialDesignThemes.Wpf.PackIconKind.Pencil, Width = 14, Height = 14 };
            editBtn.Click += EditLog_Click;
            btnPanel.Children.Add(editBtn);

            var delBtn = new Button
            {
                Style = (Style)Application.Current.FindResource("MaterialDesignIconForegroundButton"),
                Width = 24, Height = 24, Tag = log.Id,
                ToolTip = LanguageManager.GetString("WatchLog_Delete"),
                Margin = new Thickness(2, 0, 0, 0)
            };
            delBtn.Content = new MaterialDesignThemes.Wpf.PackIcon { Kind = MaterialDesignThemes.Wpf.PackIconKind.Delete, Width = 14, Height = 14 };
            delBtn.Click += DeleteLog_Click;
            btnPanel.Children.Add(delBtn);

            Grid.SetColumn(btnPanel, 2);
            grid.Children.Add(btnPanel);

            card.Child = grid;
            DiaryPanel.Children.Add(card);
        }
    }

    private async void FilterDatePicker_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
    {
        await LoadAsync(FilterDatePicker.SelectedDate);
    }

    private async void ClearFilter_Click(object sender, RoutedEventArgs e)
    {
        FilterDatePicker.SelectedDate = null;
        await LoadAsync();
    }

    private async void EditLog_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not int logId || _svc == null) return;
        var log = await _svc.GetByIdAsync(logId);
        if (log == null) return;
        var dlg = new WatchLogDialog(log.MovieId, log.Movie?.Title ?? "", log)
        {
            Owner = Window.GetWindow(this)
        };
        if (dlg.ShowDialog() == true)
        {
            log.WatchDate = dlg.WatchDate;
            log.Rating = dlg.Rating;
            log.Location = dlg.LogLocation;
            log.Companion = dlg.LogCompanion;
            log.Notes = dlg.LogNotes;
            await _svc.UpdateAsync(log);
            await LoadAsync(FilterDatePicker.SelectedDate);
        }
    }

    private async void DeleteLog_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not int logId || _svc == null) return;
        if (!AppMessageBox.Confirm(
            LanguageManager.GetString("WatchLog_ConfirmDelete"),
            LanguageManager.GetString("Msg_Confirm"))) return;
        await _svc.DeleteAsync(logId);
        await LoadAsync(FilterDatePicker.SelectedDate);
    }

    private static Brush SafeFindBrush(string resourceKey, Color fallback)
    {
        var brush = Application.Current.TryFindResource(resourceKey) as Brush;
        if (brush != null) return brush;
        var solid = new SolidColorBrush(fallback);
        solid.Freeze();
        return solid;
    }
}
