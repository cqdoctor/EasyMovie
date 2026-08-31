using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using EasyMovie.Core;
using EasyMovie.Core.Helpers;
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
    // 跨实例、跨导入共享的限流熔断与结果缓存：
    // 避免批量导入时对已被封禁/限流的数据源反复无效请求，并复用同名电影的已查结果。
    private enum CircuitState { Closed, Open, HalfOpen }
    private static readonly ConcurrentDictionary<string, CircuitState> _circuitState = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, DateTime> _circuitOpenUntil = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, int> _consecutiveFails = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, FetchResult> _cache = new(StringComparer.OrdinalIgnoreCase);
    // 海报回填去重：每部电影进程内仅尝试补图一次，避免批量导入时重复请求触发限流。
    private static readonly HashSet<string> _posterBackfilled = new();
    private static readonly object _posterBackfillLock = new();
    // 补图保守限速：补图只是增强项，宁可慢也绝不因密集请求触发豆瓣限流（限流会连累主匹配）。
    private static readonly Random _backfillRng = new();

    /// <summary>
    /// 是否启用「缓存命中但缺海报时的自动批量补图」。默认关闭。
    /// 原因：批量导入整库逐部补图会在短时间内产生数百次请求，即便单请求限速（1500ms）
    /// 也会超过豆瓣 IP 封禁阈值（约连续 11 次），重蹈封号并连累主匹配。
    /// 仅在显式开启时补图；正常导入不做批量补图，尊重已调好的限流设置。
    /// 海报的可靠来源是联网导入未缓存新片时随 Upsert 自然沉淀，或走 TMDB（需代理，不封 IP）。
    /// </summary>
    public static bool EnablePosterBackfill { get; set; } = false;
    // 熔断冷却时长：期满后进入半开状态，允许一次试探；成功则闭合，失败则重新熔断。
    // 这样即便某源在批量导入中途被封，也能在封禁解除后自动恢复，而不是整个导入期都被禁用。
    private static readonly TimeSpan CircuitCooldown = TimeSpan.FromMinutes(2);

    private static bool IsCircuitOpen(string source)
    {
        if (_circuitState.TryGetValue(source, out var st) && st == CircuitState.Open)
        {
            if (DateTime.UtcNow < _circuitOpenUntil.GetValueOrDefault(source))
                return true;                 // 仍在熔断期，直接跳过
            _circuitState[source] = CircuitState.HalfOpen;  // 熔断到期 → 半开，允许一次试探
        }
        return false;
    }
    private static void MarkSuccess(string source)
    {
        _consecutiveFails[source] = 0;
        _circuitState[source] = CircuitState.Closed;
    }
    private static void MarkFail(string source)
    {
        if (_circuitState.TryGetValue(source, out var st) && st == CircuitState.HalfOpen)
        {
            // 半开试探仍失败 → 重新熔断一个完整冷却期
            _circuitState[source] = CircuitState.Open;
            _circuitOpenUntil[source] = DateTime.UtcNow.Add(CircuitCooldown);
            _consecutiveFails[source] = 0;
            return;
        }
        var c = _consecutiveFails.AddOrUpdate(source, 1, (_, v) => v + 1);
        if (c >= 2)
        {
            _circuitState[source] = CircuitState.Open;
            _circuitOpenUntil[source] = DateTime.UtcNow.Add(CircuitCooldown);
            _consecutiveFails[source] = 0;
        }
    }
    private static string CacheKey(string title, int year)
        => Regex.Replace(title ?? "", @"[^\p{L}\p{N}]", "").ToLowerInvariant() + "|" + year;

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

        // 缓存命中直接返回，避免对同名电影重复请求（批量导入多部同类片时显著降低触发限流的概率）
        var cacheKey = CacheKey(movie.Title, movie.Year);
        if (_cache.TryGetValue(cacheKey, out var cached)) return cached;

        // 离线缓存优先：所有网络源不可达（豆瓣封禁 / TMDB·OMDb 被墙 / 百度百科反爬）时，
        // 仍能凭预置的常见片库直接命中，保证「首次导入 85+ 部」不中断。
        var offline = LocalMovieCache.Lookup(movie);
        if (offline != null)
        {
            // 缓存命中但缺海报（IMDb 种子无海报字段）：默认不自动批量补图（见 EnablePosterBackfill 说明，
            // 批量补图会顶穿豆瓣封禁阈值）。显式开启时才补；正常导入不做，避免重蹈封号。
            if (EnablePosterBackfill && string.IsNullOrEmpty(offline.PosterUrl))
                await TryBackfillPosterAsync(movie, offline, ct);
            return new FetchResult { Info = offline, Source = "cache" };
        }

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
                var doubanResult = await TrySourceWithCircuit(douban, searchWords, movie, ct);
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

                    var omdbResult = await TrySourceWithCircuit(omdb, omdbQueries, movie, ct);
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

                    var tmdbResult = await TrySourceWithCircuit(tmdb, tmdbQueries, movie, ct);
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

                var baikeResult = await TrySourceWithCircuit(baike, baikeQueries, movie, ct);
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
                merged.Director = MovieCreditCleaner.CleanDirector(merged.Director);
            if (!string.IsNullOrEmpty(merged.Cast))
                merged.Cast = Regex.Replace(merged.Cast, @"<[^>]+>", "").Trim();

            result.Info = merged;
            _cache[cacheKey] = result;
            // 联网命中结果回流写入离线缓存：用户首次联网匹配后，后续离线也能直接命中（自学习扩充）。
            // 关键修复：写回键必须用「应用查库用的原始标题」(movie.Title)，而非合并后被清洗过的标题(merged.Title)。
            // 旧实现用 merged.Title(规范原名，如豆瓣返回的“奥本海默”)作 NormTitle 主键，但库文件名是带噪声后缀的
            // “奥本海默 中俄字幕”，Lookup(movie) 按原始标题查永远 miss → 离线自学习缓存形同虚设。
            // 改用 UpsertOrMerge：原始标题作主键(NormTitle)，规范原名作别名键(NormOriginal)，两条路径都能命中；
            // 且对已存在的“仅按清洗键写入”的旧脏记录会自动提升为主键，顺带修复存量误键数据。
            var backfill = new MovieSearchResult
            {
                Title = movie.Title,
                OriginalTitle = merged.OriginalTitle,
                Year = merged.Year,
                Director = merged.Director,
                Cast = merged.Cast,
                Country = merged.Country,
                Language = merged.Language,
                PosterUrl = merged.PosterUrl,
                Rating = merged.Rating,
                RatingCount = merged.RatingCount,
            };
            LocalMovieCache.UpsertOrMerge(backfill, result.Source);
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

    /// <summary>带源级熔断的搜索：熔断/冷却中的源直接跳过（冷却不计入失败）；成功则闭合，失败则累加，连续失败触发熔断，熔断到期自动半开恢复。</summary>
    private static async Task<MovieSearchResult?> TrySourceWithCircuit(
        IMovieApiClient client, List<string> queries, Movie movie, CancellationToken ct)
    {
        if (IsCircuitOpen(client.SourceName)) return null;
        if (client.IsThrottled()) return null;   // 客户端自身冷却中（如豆瓣限流冷却）：有意不发请求，不计入熔断失败
        var r = await TrySourceAsync(client, queries, movie, ct);
        if (r != null) MarkSuccess(client.SourceName);
        else MarkFail(client.SourceName);
        return r;
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

                // 选择最佳匹配；无可靠匹配时继续尝试下一个搜索词
                var best = PickBest(sr.Results, movie, q);
                if (best == null) continue;

                var detail = await client.GetDetailAsync(best.ExternalId ?? "", ct);
                // 详情抓取失败或返回空对象时，用搜索结果中的有效字段兜底，
                // 避免“搜索已命中正确影片，却被空详情覆盖”导致最终拿到空/错误数据。
                if (detail == null) return best;
                if (string.IsNullOrEmpty(detail.Title)) detail.Title = best.Title;
                if (string.IsNullOrEmpty(detail.OriginalTitle)) detail.OriginalTitle = best.OriginalTitle;
                if (detail.Year == 0) detail.Year = best.Year;
                if (string.IsNullOrEmpty(detail.Director)) detail.Director = best.Director;
                if (string.IsNullOrEmpty(detail.Cast)) detail.Cast = best.Cast;
                if (string.IsNullOrEmpty(detail.PosterUrl)) detail.PosterUrl = best.PosterUrl;
                if (string.IsNullOrEmpty(detail.Country)) detail.Country = best.Country;
                if (!detail.Runtime.HasValue) detail.Runtime = best.Runtime;
                if (!detail.Rating.HasValue) detail.Rating = best.Rating;
                return detail;
            }
            catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Log.Error(ex, "数据源获取失败，已跳过该源"); }
        }
        return null;
    }

    /// <summary>
    /// 缓存命中但缺海报时的安全补图：仅对「未熔断且未冷却」的可用源各尝试一次搜索+详情（不计熔断失败），
    /// 命中即写回离线缓存并赋给结果返回。每部仅尝试一次；全部失败则静默放弃，调用方仍按离线命中返回。
    /// 复用客户端自带的限速/冷却，不会比正常导入更激进，不会触发反爬。
    /// </summary>
    private static async Task TryBackfillPosterAsync(Movie movie, MovieSearchResult offline, CancellationToken ct)
    {
        var key = CacheKey(movie.Title, movie.Year);
        bool first;
        lock (_posterBackfillLock) first = _posterBackfilled.Add(key);
        if (!first) return;   // 本次进程已尝试过，跳过

        var keyword = DoubanApiClient.ExtractChineseKeyword(movie.Title);
        var engHint = DoubanApiClient.ExtractEnglishHint(movie.Title);
        var words = BuildSearchWords(movie.Title, keyword, engHint);

        foreach (var client in AvailableBackfillSources())
        {
            if (IsCircuitOpen(client.SourceName) || client.IsThrottled()) continue;
            try
            {
                // 直接走 TrySourceAsync（不经过熔断计数），仅用于补图，失败不影响主流程
                var r = await TrySourceAsync(client, words, movie, ct);
                if (r != null && !string.IsNullOrEmpty(r.PosterUrl))
                {
                    offline.PosterUrl = r.PosterUrl;
                    LocalMovieCache.UpdatePoster(offline.Title, offline.OriginalTitle, offline.Year, r.PosterUrl);
                    return;
                }
            }
            catch { /* 静默：补图失败不影响离线命中返回 */ }
            // 保守限速：每部补图尝试后放慢节奏（2~3.5s 抖动），降低触发豆瓣限流的概率；
            // 即便限流发生，上游冷却机制会让后续请求直接跳过，不会加重封禁。
            await Task.Delay(2000 + _backfillRng.Next(0, 1500), ct);
        }
    }

    /// <summary>按本机配置返回可用于补图的客户端（豆瓣需 Cookie / OMDb·TMDB 需 Key）。</summary>
    private static List<IMovieApiClient> AvailableBackfillSources()
    {
        var list = new List<IMovieApiClient>();
        if (!string.IsNullOrEmpty(AppSettings.DoubanCookie)) list.Add(new DoubanApiClient());
        var omdbKey = AppSettings.OmdbApiKey;
        if (!string.IsNullOrEmpty(omdbKey)) list.Add(new OmdbApiClient(omdbKey ?? ""));
        var tmdbKey = AppSettings.TmdbApiKey;
        if (!string.IsNullOrEmpty(tmdbKey)) list.Add(new TmdbApiClient(tmdbKey ?? ""));
        return list;
    }

    /// <summary>从搜索结果中选择最佳匹配；无可靠匹配时返回 null，交由上层换搜索词/数据源</summary>
    private static MovieSearchResult? PickBest(List<MovieSearchResult> results, Movie movie, string query)
    {
        return DoubanApiClient.PickBestMatch(results, movie.Title, movie.Year);
    }

    // 导演黑名单职业标签（中英文）
    
    /// <summary>判断导演字符串是否有效（非空、非日期、非年份、非职业标签）</summary>
    
    /// <summary>判断是否需要继续搜索（导演无效或关键字段缺失）</summary>
    private static bool NeedsMoreData(MovieSearchResult r)
    {
        return !MovieCreditCleaner.IsPlausibleDirector(r.Director);
    }

    /// <summary>合并数据源结果：只补充缺失或无效的字段</summary>
    private static void MergeResult(MovieSearchResult target, MovieSearchResult source)
    {
        // 导演：目标无效时用源数据（源数据也需有效）
        if (!MovieCreditCleaner.IsPlausibleDirector(target.Director) && MovieCreditCleaner.IsPlausibleDirector(source.Director))
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
