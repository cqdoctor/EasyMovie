using CsvHelper.Configuration.Attributes;

namespace EasyMovie.Tools.ImportExport;

/// <summary>
/// CSV 电影记录映射
/// </summary>
public class CsvMovieRecord
{
    [Name("片名")]
    [Index(0)]
    public string Title { get; set; } = string.Empty;

    [Name("原始片名")]
    [Index(1)]
    public string? OriginalTitle { get; set; }

    [Name("年份")]
    [Index(2)]
    public int? Year { get; set; }

    [Name("导演")]
    [Index(3)]
    public string? Director { get; set; }

    [Name("主演")]
    [Index(4)]
    public string? Cast { get; set; }

    [Name("国家")]
    [Index(5)]
    public string? Country { get; set; }

    [Name("语言")]
    [Index(6)]
    public string? Language { get; set; }

    [Name("片长")]
    [Index(7)]
    public int? Runtime { get; set; }

    [Name("简介")]
    [Index(8)]
    public string? Synopsis { get; set; }

    [Name("评分")]
    [Index(9)]
    public int? Rating { get; set; }

    [Name("观看状态")]
    [Index(10)]
    public string? WatchStatusStr { get; set; }

    [Name("观看日期")]
    [Index(11)]
    public string? WatchDateStr { get; set; }

    [Name("笔记")]
    [Index(12)]
    public string? Notes { get; set; }

    [Name("收藏")]
    [Index(13)]
    public string? IsFavoriteStr { get; set; }
}
