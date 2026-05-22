using EasyMovie.Core.Enums;

namespace EasyMovie.Core.Models;

/// <summary>
/// 电影实体
/// </summary>
public class Movie
{
    public int Id { get; set; }

    /// <summary>中文片名</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>原始片名</summary>
    public string? OriginalTitle { get; set; }

    /// <summary>上映年份</summary>
    public int Year { get; set; }

    /// <summary>导演</summary>
    public string? Director { get; set; }

    /// <summary>主演（逗号分隔或 JSON 数组）</summary>
    public string? Cast { get; set; }

    /// <summary>国家/地区</summary>
    public string? Country { get; set; }

    /// <summary>语言</summary>
    public string? Language { get; set; }

    /// <summary>片长（分钟）</summary>
    public int? Runtime { get; set; }

    /// <summary>剧情简介</summary>
    public string? Synopsis { get; set; }

    // ── 分类 & 标签 ──
    public int? CategoryId { get; set; }
    public Category? Category { get; set; }
    public ICollection<MovieTag> MovieTags { get; set; } = new List<MovieTag>();

    // ── 评分 & 状态 ──
    /// <summary>个人评分 1-10，null 表示未评分</summary>
    public int? Rating { get; set; }

    /// <summary>观看状态</summary>
    public WatchStatus WatchStatus { get; set; } = WatchStatus.WantToWatch;

    /// <summary>观看日期</summary>
    public DateTime? WatchDate { get; set; }

    /// <summary>简短笔记</summary>
    public string? Notes { get; set; }

    /// <summary>是否收藏</summary>
    public bool IsFavorite { get; set; }

    // ── 封面 ──
    /// <summary>本地封面图片路径</summary>
    public string? CoverImagePath { get; set; }

    /// <summary>在线海报 URL</summary>
    public string? PosterUrl { get; set; }

    /// <summary>海报图片数据（存入数据库）</summary>
    public byte[]? PosterData { get; set; }

    /// <summary>搜索索引（拼音全拼+首字母，用于模糊搜索）</summary>
    public string? SearchIndex { get; set; }

    // ── 外部 ID ──
    /// <summary>豆瓣电影 ID</summary>
    public string? DoubanId { get; set; }

    /// <summary>TMDB 电影 ID</summary>
    public string? TmdbId { get; set; }

    /// <summary>本地视频文件路径</summary>
    public string? FilePath { get; set; }

    // ── 时间戳 ──
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
