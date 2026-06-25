using EasyMovie.Core.Enums;

namespace EasyMovie.Core.Models;

/// <summary>
/// 重复电影检测结果 — 一组疑似重复的电影
/// </summary>
public class DuplicateGroup
{
    /// <summary>分组标识</summary>
    public string GroupKey { get; set; } = string.Empty;

    /// <summary>匹配类型</summary>
    public DuplicateMatchType MatchType { get; set; }

    /// <summary>组内疑似重复的电影列表</summary>
    public List<DuplicateMovieItem> Movies { get; set; } = new();

    /// <summary>用户选择的主记录ID（合并时保留）</summary>
    public int SelectedPrimaryId { get; set; }

    /// <summary>是否已被用户处理（合并或保留）</summary>
    public bool IsHandled { get; set; }
}

/// <summary>
/// 重复组内的电影项
/// </summary>
public class DuplicateMovieItem
{
    public int MovieId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? OriginalTitle { get; set; }
    public int Year { get; set; }
    public string? Director { get; set; }
    public int? Rating { get; set; }
    public WatchStatus WatchStatus { get; set; }
    public byte[]? PosterData { get; set; }
    public bool HasNotes { get; set; }
    public int TagCount { get; set; }
    public bool IsFavorite { get; set; }
    public string? FilePath { get; set; }

    /// <summary>信息完整度评分（用于自动选择主记录）</summary>
    public int CompletenessScore =>
        (HasNotes ? 2 : 0) +
        (Rating.HasValue ? 1 : 0) +
        (WatchStatus == WatchStatus.Watched ? 2 : 0) +
        TagCount +
        (IsFavorite ? 1 : 0) +
        (!string.IsNullOrEmpty(FilePath) ? 1 : 0) +
        (PosterData != null && PosterData.Length > 0 ? 1 : 0);
}
