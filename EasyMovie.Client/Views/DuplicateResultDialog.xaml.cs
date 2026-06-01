using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using EasyMovie.Core.Enums;
using EasyMovie.Core.Models;
using EasyMovie.Core.Services;
using EasyMovie.Data;
using Microsoft.EntityFrameworkCore;

namespace EasyMovie.Client.Views;

public partial class DuplicateResultDialog : Window
{
    private readonly ObservableCollection<DuplicateGroupViewModel> _groups = new();

    public DuplicateResultDialog()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RunDetectionAsync();
    }

    private async Task RunDetectionAsync()
    {
        ProgressPanel.Visibility = Visibility.Visible;
        ResultPanel.Visibility = Visibility.Collapsed;
        MergeAllBtn.Visibility = Visibility.Collapsed;

        var service = new DuplicateDetectionService();
        service.ProgressChanged += (scanned, total) =>
        {
            Dispatcher.Invoke(() =>
            {
                var fmt = LanguageManager.GetString("Dup_ScanProgress");
                ProgressText.Text = string.Format(fmt, scanned, total);
            });
        };

        try
        {
            List<Movie> movies;
            using (var ctx = DbHelper.CreateContext())
            {
                movies = await ctx.Movies.Include(m => m.MovieTags).ToListAsync();
            }

            var results = await Task.Run(() => service.Detect(movies));

            _groups.Clear();
            foreach (var g in results)
            {
                _groups.Add(new DuplicateGroupViewModel(g));
            }

            var fmt2 = LanguageManager.GetString("Dup_ResultTitle");
            ResultTitle.Text = string.Format(fmt2, _groups.Count);

            GroupItems.ItemsSource = _groups;

            ProgressPanel.Visibility = Visibility.Collapsed;
            ResultPanel.Visibility = Visibility.Visible;
            MergeAllBtn.Visibility = _groups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (_groups.Count == 0)
            {
                ResultTitle.Text = LanguageManager.GetString("Dup_NoDuplicates");
            }
        }
        catch (Exception ex)
        {
            AppMessageBox.ShowError(ex.Message, LanguageManager.GetString("Msg_Hint"));
            Close();
        }
    }

    private async void MergeGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not DuplicateGroupViewModel vm) return;

        var confirm = LanguageManager.GetString("Dup_MergeConfirm");
        if (!AppMessageBox.Confirm(confirm, LanguageManager.GetString("Msg_Confirm"))) return;

        try
        {
            await MergeGroupAsync(vm.ToModel());
            _groups.Remove(vm);
            UpdateResultTitle();
        }
        catch (Exception ex)
        {
            AppMessageBox.ShowError(ex.Message, LanguageManager.GetString("Msg_Hint"));
        }
    }

    private void KeepAll_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not DuplicateGroupViewModel vm) return;
        _groups.Remove(vm);
        UpdateResultTitle();
    }

    private async void MergeAll_Click(object sender, RoutedEventArgs e)
    {
        var confirm = LanguageManager.GetString("Dup_MergeAllConfirm");
        if (!AppMessageBox.Confirm(confirm, LanguageManager.GetString("Msg_Confirm"))) return;

        var toMerge = _groups.ToList();
        try
        {
            foreach (var vm in toMerge)
            {
                await MergeGroupAsync(vm.ToModel());
                _groups.Remove(vm);
            }
            UpdateResultTitle();
        }
        catch (Exception ex)
        {
            AppMessageBox.ShowError(ex.Message, LanguageManager.GetString("Msg_Hint"));
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateResultTitle()
    {
        if (_groups.Count == 0)
        {
            ResultTitle.Text = LanguageManager.GetString("Dup_NoDuplicates");
            MergeAllBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            var fmt = LanguageManager.GetString("Dup_ResultTitle");
            ResultTitle.Text = string.Format(fmt, _groups.Count);
        }
    }

    /// <summary>
    /// 合并重复组：保留主记录，删除其他记录，合并标签等数据
    /// </summary>
    private static async Task MergeGroupAsync(DuplicateGroup group)
    {
        using var context = DbHelper.CreateContext();

        var primary = await context.Movies
            .Include(m => m.MovieTags)
            .FirstOrDefaultAsync(m => m.Id == group.SelectedPrimaryId);

        if (primary == null) return;

        var toDeleteIds = group.Movies
            .Where(m => m.MovieId != group.SelectedPrimaryId)
            .Select(m => m.MovieId)
            .ToList();

        foreach (var deleteId in toDeleteIds)
        {
            var duplicate = await context.Movies
                .Include(m => m.MovieTags)
                .FirstOrDefaultAsync(m => m.Id == deleteId);

            if (duplicate == null) continue;

            // 合并字段：优先取非空值
            primary.DoubanId ??= duplicate.DoubanId;
            primary.TmdbId ??= duplicate.TmdbId;
            primary.OriginalTitle ??= duplicate.OriginalTitle;
            primary.Director ??= duplicate.Director;
            primary.Cast ??= duplicate.Cast;
            primary.Country ??= duplicate.Country;
            primary.Language ??= duplicate.Language;
            primary.Runtime ??= duplicate.Runtime;
            primary.Synopsis ??= duplicate.Synopsis;
            primary.FilePath ??= duplicate.FilePath;
            primary.CoverImagePath ??= duplicate.CoverImagePath;
            primary.PosterUrl ??= duplicate.PosterUrl;
            primary.CategoryId ??= duplicate.CategoryId;

            if (!primary.Rating.HasValue && duplicate.Rating.HasValue)
                primary.Rating = duplicate.Rating;

            if (duplicate.WatchStatus > primary.WatchStatus)
            {
                primary.WatchStatus = duplicate.WatchStatus;
                primary.WatchDate ??= duplicate.WatchDate;
            }

            if (!primary.WatchDate.HasValue && duplicate.WatchDate.HasValue)
                primary.WatchDate = duplicate.WatchDate;

            if (!string.IsNullOrEmpty(duplicate.Notes))
            {
                primary.Notes = string.IsNullOrEmpty(primary.Notes)
                    ? duplicate.Notes
                    : $"{primary.Notes}\n{duplicate.Notes}";
            }

            if (duplicate.IsFavorite) primary.IsFavorite = true;

            if ((primary.PosterData == null || primary.PosterData.Length == 0) &&
                duplicate.PosterData != null && duplicate.PosterData.Length > 0)
            {
                primary.PosterData = duplicate.PosterData;
            }

            // 合并标签（去重）
            var existingTagIds = primary.MovieTags.Select(mt => mt.TagId).ToHashSet();
            foreach (var mt in duplicate.MovieTags)
            {
                if (!existingTagIds.Contains(mt.TagId))
                {
                    primary.MovieTags.Add(new MovieTag { MovieId = primary.Id, TagId = mt.TagId });
                    existingTagIds.Add(mt.TagId);
                }
            }

            context.Movies.Remove(duplicate);
        }

        primary.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();
    }
}

/// <summary>
/// 重复组 ViewModel（用于 UI 绑定）
/// </summary>
public class DuplicateGroupViewModel : INotifyPropertyChanged
{
    private readonly DuplicateGroup _model;

    public DuplicateGroupViewModel(DuplicateGroup model)
    {
        _model = model;
        Movies = model.Movies.Select(m => new DuplicateMovieItemViewModel(m, model)).ToList();
    }

    public string GroupKey => _model.GroupKey;
    public string MatchTypeText => _model.MatchType switch
    {
        DuplicateMatchType.Exact => LanguageManager.GetString("Dup_ExactMatch"),
        DuplicateMatchType.Fuzzy => LanguageManager.GetString("Dup_FuzzyMatch"),
        DuplicateMatchType.ExternalId => LanguageManager.GetString("Dup_ExternalIdMatch"),
        _ => ""
    };
    public List<DuplicateMovieItemViewModel> Movies { get; }

    public DuplicateGroup ToModel()
    {
        _model.SelectedPrimaryId = Movies.FirstOrDefault(m => m.IsPrimary)?.MovieId
            ?? Movies.First().MovieId;
        return _model;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 重复电影项 ViewModel
/// </summary>
public class DuplicateMovieItemViewModel : INotifyPropertyChanged
{
    private readonly DuplicateMovieItem _model;
    private readonly DuplicateGroup _group;
    private bool _isPrimary;

    public DuplicateMovieItemViewModel(DuplicateMovieItem model, DuplicateGroup group)
    {
        _model = model;
        _group = group;
        _isPrimary = model.MovieId == group.SelectedPrimaryId;
    }

    public int MovieId => _model.MovieId;
    public string Title => _model.Title;
    public string YearText => _model.Year > 0 ? $"({_model.Year})" : "";

    public string RatingText => _model.Rating.HasValue ? $"⭐{_model.Rating}" : "";

    public string StatusText => _model.WatchStatus switch
    {
        WatchStatus.Watched => LanguageManager.GetString("WatchStatus_Watched"),
        WatchStatus.Watching => LanguageManager.GetString("WatchStatus_Watching"),
        _ => LanguageManager.GetString("WatchStatus_WantToWatch")
    };

    public string InfoText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(_model.Director)) parts.Add(_model.Director);
            if (_model.HasNotes) parts.Add(LanguageManager.GetString("Dup_HasNotes"));
            if (_model.TagCount > 0) parts.Add($"{_model.TagCount}{LanguageManager.GetString("Dup_TagsSuffix")}");
            if (_model.IsFavorite) parts.Add("❤");
            return string.Join(" | ", parts);
        }
    }

    public string GroupName => $"G_{_group.GroupKey.GetHashCode():X}";

    public bool IsPrimary
    {
        get => _isPrimary;
        set { _isPrimary = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
