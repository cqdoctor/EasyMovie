using System.Text.RegularExpressions;
using EasyMovie.Core;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;
using Serilog;

namespace EasyMovie.Tools.MovieApi;

/// <summary>
/// 统一电影信息获取器：按优先级级联搜索多个数据源，合并结果。
/// 优先级：豆瓣 → TMDB → OMDb → 百度百科
/// 每个数据源失败或导演无效时自动进入下一个，字段只补充不覆盖。
/// </summary>
public class MovieInfoFetcher
{
    /// <summary>获取结果</summary>
    public class FetchResult
    {
        public MovieSearchResult? Info { get; set; }
        public string Source { get; set; } = "";
        public bool Success => Info != null;
    }

    /// <summary>进度回调</summary>
    public IProgress<string>? Progress { get; set; }

    /// <summary>是否启用手动搜索兜底（全部失败时回调）</summary>
    public Func<string, Task<string?>>? ManualSearchCallback { get; set; }

    /// <summary>
    /// 统一获取电影信息：豆瓣 → TMDB → OMDb → 百度百科 → 手动搜索
    /// </summary>
    public async Task<FetchResult> FetchAsync(Movie movie, CancellationToken ct = default)
    {
        var result = new FetchResult();
        var merged = new MovieSearchResult();
        bool foundAny = false;

        var keyword = DoubanApiClient.ExtractChineseKeyword(movie.Title);
        var engHint = DoubanApiClient.ExtractEnglishHint(movie.Title);

        // 构建搜索词列表（通用）
        var searchWords = BuildSearchWords(movie.Title, keyword, engHint);

        // 1. 豆瓣（需 Cookie）
        var cookie = AppSettings.DoubanCookie;
        if (!string.IsNullOrEmpty(cookie))
        {
            Progress?.Report("豆瓣搜索: " + (string.IsNullOrEmpty(keyword) ? (engHint ?? movie.Title) : keyword) + "...");
            try
            {
                var douban = new DoubanApiClient();
                var doubanResult = await TrySourceAsync(douban, searchWords, movie, ct);
                if (doubanResult != null)
                {
                    MergeResult(merged, doubanResult);
                    result.Source = "douban";
                    foundAny = true;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Error(ex, "数据源获取失败，已跳过该源"); }
        }

        // 2. OMDb（需 API Key，国内可直连无需代理）—— 提前到 TMDB 之前
        if (NeedsMoreData(merged))
        {
            var omdbKey = AppSettings.OmdbApiKey;
            if (!string.IsNullOrEmpty(omdbKey))
            {
                Progress?.Report("OMDb搜索: " + (engHint ?? movie.Title) + "...");
                try
                {
                    var omdb = new OmdbApiClient(omdbKey ?? "");
                    // OMDb 用英文标题搜索效果最好
                    var omdbQueries = new List<string>();
                    if (!string.IsNullOrEmpty(engHint)) omdbQueries.Add(engHint);
                    if (!string.IsNullOrEmpty(keyword) && keyword != engHint) omdbQueries.Add(keyword);
                    if (omdbQueries.Count == 0) omdbQueries.Add(movie.Title);

                    var omdbResult = await TrySourceAsync(omdb, omdbQueries, movie, ct);
                    if (omdbResult != null)
                    {
                        MergeResult(merged, omdbResult);
                        result.Source = string.IsNullOrEmpty(result.Source) ? "omdb" : result.Source + "+omdb";
                        foundAny = true;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Log.Error(ex, "数据源获取失败，已跳过该源"); }
            }
        }

        // 3. TMDB（需 API Key，网站爬取无需代理）—— 仍然缺数据时继续
        if (NeedsMoreData(merged))
        {
            var tmdbKey = AppSettings.TmdbApiKey;
            if (!string.IsNullOrEmpty(tmdbKey))
            {
                Progress?.Report("TMDB搜索: " + (engHint ?? keyword ?? movie.Title) + "...");
                try
                {
                    var tmdb = new TmdbApiClient(tmdbKey ?? "");
                    var tmdbQueries = new List<string>();
                    if (!string.IsNullOrEmpty(engHint)) tmdbQueries.Add(engHint);
                    if (!string.IsNullOrEmpty(keyword) && keyword != engHint) tmdbQueries.Add(keyword);
                    if (tmdbQueries.Count == 0) tmdbQueries.Add(movie.Title);

                    var tmdbResult = await TrySourceAsync(tmdb, tmdbQueries, movie, ct);
                    if (tmdbResult != null)
                    {
                        MergeResult(merged, tmdbResult);
                        result.Source = string.IsNullOrEmpty(result.Source) ? "tmdb" : result.Source + "+tmdb";
                        foundAny = true;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Log.Error(ex, "数据源获取失败，已跳过该源"); }
            }
        }

        // 4. 百度百科（无需配置）—— 最后兜底
        if (NeedsMoreData(merged))
        {
            Progress?.Report("百度百科搜索: " + (string.IsNullOrEmpty(keyword) ? movie.Title : keyword) + "...");
            try
            {
                var baike = new BaiduBaikeApiClient();
                // 百度百科用中文标题搜索效果最好
                var baikeQueries = new List<string>();
                if (!string.IsNullOrEmpty(keyword)) baikeQueries.Add(keyword);
                if (!string.IsNullOrEmpty(engHint) && !baikeQueries.Contains(engHint)) baikeQueries.Add(engHint);
                if (baikeQueries.Count == 0) baikeQueries.Add(movie.Title);

                var baikeResult = await TrySourceAsync(baike, baikeQueries, movie, ct);
                if (baikeResult != null)
                {
                    MergeResult(merged, baikeResult);
                    result.Source = string.IsNullOrEmpty(result.Source) ? "baike" : result.Source + "+baike";
                    foundAny = true;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Error(ex, "数据源获取失败，已跳过该源"); }
        }

        // 5. 手动搜索兜底 —— 全部失败时让用户输入关键词
        if (!foundAny && ManualSearchCallback != null)
        {
            var userInput = await ManualSearchCallback(movie.Title);
            if (!string.IsNullOrWhiteSpace(userInput) && userInput != movie.Title)
            {
                Progress?.Report("用关键词重新搜索: " + userInput + "...");
                // 用新关键词创建临时 Movie 对象重新搜索
                var tempMovie = new Movie { Title = userInput, Year = movie.Year };
                var retryResult = await FetchAsync(tempMovie, ct);
                if (retryResult.Success)
                {
                    return retryResult;
                }
            }
        }

        if (foundAny)
        {
            // 最终清理
            if (!string.IsNullOrEmpty(merged.Synopsis))
                merged.Synopsis = Regex.Replace(merged.Synopsis, @"<[^>]+>", "").Trim();
            if (!string.IsNullOrEmpty(merged.Director))
                merged.Director = Regex.Replace(merged.Director, @"<[^>]+>", "").Trim();
            if (!string.IsNullOrEmpty(merged.Cast))
                merged.Cast = Regex.Replace(merged.Cast, @"<[^>]+>", "").Trim();

            result.Info = merged;
        }

        return result;
    }

    /// <summary>构建通用搜索词列表</summary>
    private static List<string> BuildSearchWords(string title, string keyword, string? engHint)
    {
        var words = new List<string>();
        if (!string.IsNullOrWhiteSpace(keyword)) words.Add(keyword);
        if (!string.IsNullOrWhiteSpace(keyword) && keyword.Length > 8) words.Add(keyword.Substring(0, 8));
        if (!string.IsNullOrWhiteSpace(engHint) && !words.Contains(engHint)) words.Add(engHint);
        if (!words.Contains(title)) words.Add(title);
        return words;
    }

    /// <summary>用指定数据源按关键词列表依次搜索</summary>
    private static async Task<MovieSearchResult?> TrySourceAsync(
        IMovieApiClient client, List<string> queries, Movie movie, CancellationToken ct)
    {
        foreach (var q in queries)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var sr = await client.SearchAsync(new MovieSearchRequest { Keyword = q, Page = 1, PageSize = 5 }, ct);
                if (sr.Results.Count == 0) continue;

                // 选择最佳匹配
                var best = PickBest(sr.Results, movie, q);
                var detail = await client.GetDetailAsync(best.ExternalId ?? "", ct);
                return detail ?? best;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Error(ex, "数据源获取失败，已跳过该源"); }
        }
        return null;
    }

    /// <summary>从搜索结果中选择最佳匹配</summary>
    private static MovieSearchResult PickBest(List<MovieSearchResult> results, Movie movie, string query)
    {
        MovieSearchResult? best = null;
        var engHint = DoubanApiClient.ExtractEnglishHint(movie.Title);

        // 优先英文标题匹配
        if (!string.IsNullOrEmpty(engHint))
            foreach (var r in results)
                if (!string.IsNullOrEmpty(r.OriginalTitle) && r.OriginalTitle.Contains(engHint, StringComparison.OrdinalIgnoreCase))
                { best = r; break; }

        // 其次年份匹配
        if (best == null && movie.Year > 0)
            best = results.FirstOrDefault(r => r.Year == movie.Year);

        // 兜底取第一个
        best ??= results[0];
        return best;
    }

    // 导演黑名单职业标签（中英文）
    private static readonly HashSet<string> DirectorBlacklistTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "screenplay", "story", "characters", "writer", "novel", "based on", "book",
        "director of photography", "editor", "producer", "executive producer",
        "music", "composer", "sound", "visual effects",
        "编剧", "原著", "角色", "制片人", "制片", "摄影", "剪辑", "音乐", "视觉效果", "艺术指导", "服装设计"
    };

    /// <summary>判断导演字符串是否有效（非空、非日期、非年份、非职业标签）</summary>
    private static bool IsDirectorValid(string? director)
    {
        if (string.IsNullOrWhiteSpace(director)) return false;
        if (Regex.IsMatch(director, @"^\d{4}-\d{2}-\d{2}$")) return false;  // 日期
        if (Regex.IsMatch(director, @"^\d{4}$")) return false;              // 纯年份
        if (DirectorBlacklistTerms.Any(b => director.Contains(b, StringComparison.OrdinalIgnoreCase))) return false;
        if (director.Length < 2 || director.Length > 60) return false;
        return true;
    }

    /// <summary>判断是否需要继续搜索（导演无效或关键字段缺失）</summary>
    private static bool NeedsMoreData(MovieSearchResult r)
    {
        return !IsDirectorValid(r.Director);
    }

    /// <summary>合并数据源结果：只补充缺失或无效的字段</summary>
    private static void MergeResult(MovieSearchResult target, MovieSearchResult source)
    {
        // 导演：目标无效时用源数据（源数据也需有效）
        if (!IsDirectorValid(target.Director) && IsDirectorValid(source.Director))
            target.Director = source.Director;

        // 其他字段：目标为空时用源数据
        if (string.IsNullOrEmpty(target.Title) && !string.IsNullOrEmpty(source.Title))
            target.Title = source.Title;
        if (string.IsNullOrEmpty(target.OriginalTitle) && !string.IsNullOrEmpty(source.OriginalTitle))
            target.OriginalTitle = source.OriginalTitle;
        if (string.IsNullOrEmpty(target.Cast) && !string.IsNullOrEmpty(source.Cast))
            target.Cast = source.Cast;
        if (string.IsNullOrEmpty(target.Country) && !string.IsNullOrEmpty(source.Country))
            target.Country = source.Country;
        if (string.IsNullOrEmpty(target.Synopsis) && !string.IsNullOrEmpty(source.Synopsis))
            target.Synopsis = source.Synopsis;
        if (string.IsNullOrEmpty(target.Language) && !string.IsNullOrEmpty(source.Language))
            target.Language = source.Language;
        if (string.IsNullOrEmpty(target.PosterUrl) && !string.IsNullOrEmpty(source.PosterUrl))
            target.PosterUrl = source.PosterUrl;
        if (!target.Runtime.HasValue && source.Runtime.HasValue)
            target.Runtime = source.Runtime;
        if (target.Year == 0 && source.Year > 0)
            target.Year = source.Year;
        if (!target.Rating.HasValue && source.Rating.HasValue)
            target.Rating = source.Rating;
        if (string.IsNullOrEmpty(target.ExternalId) && !string.IsNullOrEmpty(source.ExternalId))
        {
            target.ExternalId = source.ExternalId;
            target.Source = source.Source;
        }
    }
}
