using System.Net;
using System.Text.RegularExpressions;
using EasyMovie.Core.Helpers;
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
            // Movie.Title 为非空 string，而 StripHtml 对空/模板占位符输入会返回 null —— 显式兜成空串，
            // 既满足非空契约，也避免把 null 塞进实体后引发下游 NRE。
            Title = StripHtml(result.Title) ?? string.Empty,
            OriginalTitle = StripHtml(result.OriginalTitle),
            Year = result.Year,
            Director = SanitizePersonName(result.Director),
            Cast = SanitizePersonName(result.Cast),
            Country = StripHtml(result.Country),
            Language = StripHtml(result.Language),
            Synopsis = StripHtml(result.Synopsis),
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
    /// 将搜索结果映射为 Movie 实体，并按国家自动创建分类、按类型自动挂标签。
    /// </summary>
    /// <param name="tagService">
    /// 可选。提供时会把 <see cref="MovieSearchResult.Genres"/> 幂等地转成 Tag 并挂到
    /// <see cref="Movie.MovieTags"/>。这是 MovieTags 关联的唯一自动来源——此前 genres
    /// 整体被丢弃，导致统计页「类型分布」、推荐算法标签权重、关系图谱、AI 摘要、
    /// 去重完整度打分五处全部空转（库里 290 部影片 0 条标签关联）。
    /// 影片入库保存（拿到真实 Id）后需再调 MovieService.SetTagsAsync 落库，
    /// 因为 MovieTag.MovieId 必须指向已存在的影片。
    /// </param>
    public static async Task<Movie> MapToMovieAsync(
        MovieSearchResult result,
        ICategoryService? categoryService = null,
        ITagService? tagService = null)
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

        if (tagService != null && result.Genres is { Count: > 0 })
        {
            foreach (var genre in result.Genres)
            {
                if (string.IsNullOrWhiteSpace(genre)) continue;
                try
                {
                    var tag = await tagService.GetOrCreateByNameAsync(genre);
                    // TagId 先行挂上，MovieId 由 SetTagsAsync 在入库后补齐。
                    if (!movie.MovieTags.Any(mt => mt.TagId == tag.Id))
                        movie.MovieTags.Add(new MovieTag { TagId = tag.Id });
                }
                catch (ArgumentException)
                {
                    // 非法类型名（空白/纯数字/超长）——跳过即可，不该让整部影片导入失败
                }
            }
        }

        return movie;
    }

    private static string? StripHtml(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var result = Regex.Replace(input, @"<[^>]+>", "");
        result = WebUtility.HtmlDecode(result);
        result = Regex.Replace(result, @"\s+", " ").Trim();
        result = StripTemplateVariables(result);
        return string.IsNullOrEmpty(result) ? null : result;
    }

    internal static string? StripTemplateVariables(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        if (Regex.IsMatch(input, @"\$\{.*?\}|\$\(data\.\w+\)|\{\{.*?\}\}|<%.*?%>"))
            return null;
        return input;
    }

    internal static string? SanitizePersonName(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var cleaned = StripHtml(input);
        if (cleaned == null) return null;
        if (MovieCreditCleaner.InvalidPersonLabels.Contains(cleaned)) return null;
        return cleaned;
    }
}
