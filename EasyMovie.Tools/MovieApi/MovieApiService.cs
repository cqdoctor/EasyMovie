using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;

namespace EasyMovie.Tools.MovieApi;

/// <summary>
/// 电影 API 编排服务 — 豆瓣优先 → TMDB 备份
/// </summary>
public class MovieApiService
{
    private readonly IMovieApiClient _primary;
    private readonly IMovieApiClient? _fallback;

    public MovieApiService(IMovieApiClient primary, IMovieApiClient? fallback = null)
    {
        _primary = primary;
        _fallback = fallback;
    }

    /// <summary>
    /// 搜索电影，主源失败自动切换备用源
    /// </summary>
    public async Task<MovieSearchResponse> SearchAsync(string keyword, int page = 1, int pageSize = 10, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return new MovieSearchResponse();

        // 尝试主源
        var response = await _primary.SearchAsync(
            new MovieSearchRequest { Keyword = keyword, Page = page, PageSize = pageSize }, ct);

        // 主源无结果 → 尝试备用源
        if (response.Results.Count == 0 && _fallback != null)
        {
            response = await _fallback.SearchAsync(
                new MovieSearchRequest { Keyword = keyword, Page = page, PageSize = pageSize }, ct);
        }

        return response;
    }

    /// <summary>
    /// 获取电影详情，主源失败切换备用源
    /// </summary>
    public async Task<MovieSearchResult?> GetDetailAsync(string externalId, string source, CancellationToken ct = default)
    {
        var client = source == "douban" ? _primary : _fallback ?? _primary;
        return await client.GetDetailAsync(externalId, ct);
    }

    /// <summary>
    /// 将搜索结果映射为 Movie 实体
    /// </summary>
    public static Movie MapToMovie(MovieSearchResult result)
    {
        var movie = new Movie
        {
            Title = result.Title,
            OriginalTitle = result.OriginalTitle,
            Year = result.Year,
            Director = result.Director,
            Cast = result.Cast,
            Country = result.Country,
            Language = result.Language,
            Synopsis = result.Synopsis,
            Runtime = result.Runtime,
            PosterUrl = result.PosterUrl,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // 设置外部 ID
        if (result.Source == "douban")
            movie.DoubanId = result.ExternalId;
        else if (result.Source == "tmdb")
            movie.TmdbId = result.ExternalId;

        return movie;
    }

    /// <summary>
    /// 将搜索结果映射为 Movie 实体，并按国家自动创建分类
    /// </summary>
    public static async Task<Movie> MapToMovieAsync(MovieSearchResult result, ICategoryService? categoryService = null)
    {
        var movie = MapToMovie(result);

        if (categoryService != null && !string.IsNullOrWhiteSpace(result.Country))
        {
            var firstCountry = result.Country.Split('/', ' ', '·').FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim();
            if (!string.IsNullOrEmpty(firstCountry))
            {
                var category = await categoryService.GetOrCreateByNameAsync(firstCountry);
                movie.CategoryId = category.Id;
            }
        }

        return movie;
    }
}
