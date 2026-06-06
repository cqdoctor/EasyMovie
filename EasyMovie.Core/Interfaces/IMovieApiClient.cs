namespace EasyMovie.Core.Interfaces;

/// <summary>
/// 电影搜索结果 DTO
/// </summary>
public class MovieSearchResult
{
    public string Title { get; set; } = string.Empty;
    public string? OriginalTitle { get; set; }
    public int Year { get; set; }
    public string? Director { get; set; }
    public string? Cast { get; set; }
    public string? Country { get; set; }
    public string? Language { get; set; }
    public string? Synopsis { get; set; }
    public string? PosterUrl { get; set; }
    public int? Runtime { get; set; }
    public double? Rating { get; set; }        // 豆瓣/TMDB 评分
    public int? RatingCount { get; set; }      // 评分人数
    public string? ExternalId { get; set; }     // 外部 ID（豆瓣或 TMDB）
    public string Source { get; set; } = string.Empty; // "douban" / "tmdb"
}

/// <summary>
/// 在线搜索请求
/// </summary>
public class MovieSearchRequest
{
    public string Keyword { get; set; } = string.Empty;
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 10;
}

/// <summary>
/// 在线搜索结果
/// </summary>
public class MovieSearchResponse
{
    public List<MovieSearchResult> Results { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

/// <summary>
/// 影视资讯条目
/// </summary>
public class MovieNewsItem
{
    public string Title { get; set; } = string.Empty;
    public string? OriginalTitle { get; set; }
    public int Year { get; set; }
    public string? Director { get; set; }
    public string? Cast { get; set; }
    public string? Country { get; set; }
    public string? Synopsis { get; set; }
    public string? PosterUrl { get; set; }
    public int? Runtime { get; set; }
    public double? Rating { get; set; }
    public string? ExternalId { get; set; }
    public string Source { get; set; } = string.Empty;   // "douban" / "maoyan"
    public string Category { get; set; } = string.Empty;  // "coming" / "nowplaying" / "top250" / "hot"
    public string? ReleaseDate { get; set; }
    public int? Rank { get; set; }
    public string? BoxOffice { get; set; }
}

/// <summary>
/// 电影 API 客户端接口
/// </summary>
public interface IMovieApiClient
{
    /// <summary>数据源名称</summary>
    string SourceName { get; }

    /// <summary>搜索电影</summary>
    Task<MovieSearchResponse> SearchAsync(MovieSearchRequest request, CancellationToken ct = default);

    /// <summary>获取电影详情</summary>
    Task<MovieSearchResult?> GetDetailAsync(string externalId, CancellationToken ct = default);
}
