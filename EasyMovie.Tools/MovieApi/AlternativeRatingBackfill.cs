using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EasyMovie.Core;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;
using EasyMovie.Data;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace EasyMovie.Tools.MovieApi;

/// <summary>
/// 替代源评分补全报告（TMDB / OMDb）。
/// </summary>
public class AlternativeRatingReport
{
    public int Total { get; set; }
    public int Filled { get; set; }      // 成功取得评分>0 并写回主库
    public int Skipped { get; set; }     // 两源均无可靠评分
    public int Errors { get; set; }      // 网络/解析异常（已降级跳过，不阻塞）
    public int TmdbFilled { get; set; }
    public int OmdbFilled { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// 替代源（TMDB / OMDb）外部评分补全。
///
/// <para><b>动机</b>：豆瓣慢速补全对约 20 部影片已彻底达稳态——其中 ≈13 部是「豆瓣有条目但 rating=0
/// （评分人数不足/未上映/冷门）」、≈7 部是脏标题在豆瓣搜不到。这两类在豆瓣链路永远拿不到评分。
/// 但 TMDB（user score）与 OMDb（IMDb rating）对同一批影片往往有分。本服务作为豆瓣阶段的<b>兜底补充</b>，
/// 仅对已确认豆瓣补不上的影片试用替代源。</para>
///
/// <para><b>写回策略（关键修复）</b>：评分>0 时<b>直接按电影 Id 标脏回写主库</b>（与
/// <see cref="RatingBackfill"/> 同一套列标脏技术，绝不加载 PosterData 等大 BLOB），保证评分一定落地；
/// 同时仍写 cache.db（双键）供离线 Lookup 复用。之所以不单纯依赖 cache.db + Sync 的归一化标题匹配，
/// 是因为当某片的豆瓣清洗标题已存在于别名键（NormOriginal）时，<see cref="LocalMovieCache.UpsertOrMerge"/>
/// 会把新评分合并进别名记录、而不是新建主库脏标题键（NormTitle）记录，导致 Sync 按主库标题匹配时漏掉——
/// 这正是「假如我们可以」等片曾补上却回写失败的根因。直接按 Id 写主库彻底绕开该脆弱点。</para>
///
/// <para><b>纯工程、不代理</b>：TMDB 走 <c>www.themoviedb.org</c> 网页爬取（国内直连可达，无需代理）；
/// OMDb 走 <c>omdbapi.com</c>（settings 已配 OmdbApiKey）。两者均不依赖代理，符合项目「正面解决、不加代理」原则。</para>
///
/// <para><b>节奏与容错</b>：每次网络请求前按 <see cref="AltGapSeconds"/> 间隔（替代源风控远松于豆瓣，
/// 但仍礼貌限速避免触发 TMDB 突发限流）；任何源异常即降级跳过该影片，绝不抛异常中断整轮。</para>
///
/// <para><b>幂等</b>：仅写评分>0；主库已评分且与本次一致则跳过；重复运行安全。</para>
/// </summary>
public static class AlternativeRatingBackfill
{
    /// <summary>每次网络请求最小间隔（秒）。替代源风控较松，4s 足够礼貌且不会拖垮整轮。</summary>
    public static int AltGapSeconds = 4;

    public static async Task<AlternativeRatingReport> RunAsync(
        IEnumerable<(int Id, string Title, int? Year)> queue,
        DbContextOptions<MovieDbContext> mainOptions,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var items = queue as List<(int, string, int?)> ?? queue.ToList();
        var rep = new AlternativeRatingReport { Total = items.Count };

        if (items.Count == 0) { progress?.Report("替代源：无待补全影片。"); return rep; }

        // 捕获到局部量：AppSettings.TmdbApiKey 是静态属性，可被其它线程重新赋值，
        // 编译器不会用 IsNullOrWhiteSpace 的结果去窄化后续的属性访问（属性可重新求值），
        // 因此必须先落到局部量才能安全地把「已判非空」传递给构造函数（否则 CS8604）。
        var tmdbKey = AppSettings.TmdbApiKey;
        var omdbKey = AppSettings.OmdbApiKey;
        var haveTmdb = !string.IsNullOrWhiteSpace(tmdbKey);
        var haveOmdb = !string.IsNullOrWhiteSpace(omdbKey);

        // 延迟构造，避免无 key 时也建立 HttpClient
        TmdbApiClient? tmdb = haveTmdb ? new TmdbApiClient(tmdbKey!) : null;
        OmdbApiClient? omdb = haveOmdb ? new OmdbApiClient(omdbKey!) : null;

        foreach (var (id, title, year) in items)
        {
            if (ct.IsCancellationRequested) { rep.Error = "已取消。"; break; }

            // 候选搜索词：英文名优先（TMDB/OMDb 以英文条目为主，纯中文搜索常漏匹配），
            // 其次中文核心，再次完整清洗标题。去重去空。
            // 例：「断卡风暴 Fire Storm EAC3 Atmos」→ ① "Fire Storm" ② "断卡风暴" ③ "断卡风暴 Fire Storm"。
            // 旧逻辑只用 CleanSearchTitle（中文优先），导致 TMDB 搜「断卡风暴」漏掉 Firestorm 等英文条目。
            var cleaned = DoubanApiClient.CleanSearchTitle(title);
            var cands = new List<string>();
            void AddCand(string? k)
            {
                var s = (k ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(s) && !cands.Contains(s, StringComparer.OrdinalIgnoreCase))
                    cands.Add(s);
            }
            AddCand(DoubanApiClient.ExtractEnglishHint(cleaned));    // 英文原名（去标签/年份后）
            AddCand(DoubanApiClient.ExtractChineseKeyword(cleaned)); // 中文核心
            AddCand(cleaned);                                        // 完整清洗标题

            bool filled = false;
            MovieSearchResult? best = null;
            string? bestSource = null;
            void OnFilled(MovieSearchResult d, string s) { best = d; bestSource = s; filled = true; }

            // 源顺序：TMDB(英文优先) → OMDb(英文兜底)。
            // 1905 电影网：实证探测（_probe_1905_final.js）显示其搜索接口现返回 HTTP 403（与猫眼同列被反爬拦截），
            // 且此前可达时详情页为 SPA、静态 HTML 无 scrapeable 评分、搜索模糊易错配——接入只会徒增延迟与误写风险，故不接入。
            // 猫眼(maoyan)沙箱 403 不接入（避免无效请求 + 违背不加代理原则）。
            // 关键修正：PickBestMatch 以「实际搜索词 kw」为匹配基准，而非完整库文件名——
            // 否则库文件名带英文后缀（如「功夫雄狮Kung Fu Lion」）时，纯中文结果「功夫雄狮」会因
            // 反向包含长度比<0.5 被误判为不匹配，导致中文结果永远补不上。
            if (tmdb != null) await TrySourceAsync(tmdb, "tmdb", cands, year, title, ct, progress, rep, OnFilled);
            if (!filled && omdb != null) await TrySourceAsync(omdb, "omdb", cands, year, title, ct, progress, rep, OnFilled);

            if (filled && best != null && bestSource != null)
            {
                // 双写：cache.db（离线复用）+ 主库（按 Id 标脏，保证落地）
                WriteCache(best, title, year, bestSource);
                WriteMainRating(mainOptions, id, best.Rating!.Value, bestSource);
                rep.Filled++;
                if (bestSource == "tmdb") rep.TmdbFilled++;
                else if (bestSource == "omdb") rep.OmdbFilled++;
            }
            else
            {
                rep.Skipped++;
                progress?.Report($"[替代源·跳过] 三源均无可靠评分：{title}");
            }
        }

        return rep;
    }

        /// <summary>
        /// 通用替代源补全：对候选搜索词逐个尝试，首个取得评分&gt;0 的命中即采用并回传。
        /// 统一处理搜索→匹配→详情→评分校验，三源（TMDB/OMDb/1905）共用，避免三份重复代码。
        /// <para>匹配基准用「实际搜索词 kw」而非完整库文件名（修正中文结果被反向包含护栏误杀）；
        /// 命中后做模板占位符/空身份护栏，避免把脏身份评分写进主库。</para>
        /// </summary>
        private static async Task TrySourceAsync(
            IMovieApiClient client, string sourceName, List<string> cands, int? year,
            string title, CancellationToken ct, IProgress<string>? progress,
            AlternativeRatingReport rep, Action<MovieSearchResult, string> onFilled)
        {
            foreach (var kw in cands)
            {
                try
                {
                    await PaceAsync(ct);
                    var resp = await client.SearchAsync(new MovieSearchRequest { Keyword = kw, PageSize = 8 }, ct);
                    var match = resp.Results.Count > 0 ? DoubanApiClient.PickBestMatch(resp.Results, kw, year) : null;
                    // 安全护栏：匹配结果的标题/原名若为模板占位符（TMDB 静态页未渲染，形如 "#= data.original_title #"）
                    // 或为空，视为不可信，丢弃该候选换下一搜索词，避免把脏身份对应的评分写进主库。
                    if (match != null && (string.IsNullOrWhiteSpace(match.Title)
                        || DoubanApiClient.IsTemplateOrLabel(match.Title)
                        || DoubanApiClient.IsTemplateOrLabel(match.OriginalTitle)))
                        match = null;
                    if (match != null && !string.IsNullOrEmpty(match.ExternalId))
                    {
                        await PaceAsync(ct);
                        var detail = await client.GetDetailAsync(match.ExternalId, ct);
                        if (detail?.Rating != null && detail.Rating.Value > 0)
                        {
                            progress?.Report($"[替代源·{sourceName}] {title} ←「{kw}」评分={detail.Rating.Value:0.0}");
                            onFilled(detail, sourceName);
                            return; // 本源已命中，停止本源候选循环
                        }
                    }
                }
                catch (Exception ex) { Log.Warning(ex, "替代源：{Source} 补全异常 {Title}", sourceName, title); rep.Errors++; }
            }
        }

        /// <summary>落 cache.db（双键）：① 清洗标题键（通用缓存/去重）；② 主库脏标题键（离线 Lookup 复用）。</summary>
        private static void WriteCache(MovieSearchResult r, string mainTitle, int? mainYear, string source)
    {
        if (r == null) return;
        LocalMovieCache.UpsertOrMerge(r, source);                       // ① 清洗键
        var mainKeyed = new MovieSearchResult
        {
            Title = mainTitle,
            OriginalTitle = r.OriginalTitle ?? r.Title,
            Year = r.Year > 0 ? r.Year : mainYear ?? 0,
            Rating = r.Rating,
            RatingCount = r.RatingCount,
            Director = r.Director,
            Cast = r.Cast,
            Country = r.Country,
            PosterUrl = r.PosterUrl,
            Source = source,
            ExternalId = r.ExternalId,
        };
        LocalMovieCache.UpsertOrMerge(mainKeyed, source);              // ② 主库脏标题键
    }

    /// <summary>
    /// 按电影 Id 列标脏回写主库 ExternalRating / RatingSource（复用 RatingBackfill 的技术，零 BLOB 加载）。
    /// 直接从源头保证评分落地，绕开 cache.db 归一化键匹配的脆弱点。
    /// </summary>
    private static void WriteMainRating(DbContextOptions<MovieDbContext> mainOptions, int id, double rating, string source)
    {
        try
        {
            using var ctx = new MovieDbContext(mainOptions);
            var mv = ctx.Movies.FirstOrDefault(x => x.Id == id);
            if (mv == null) return;
            if (mv.ExternalRating.HasValue
                && Math.Abs(mv.ExternalRating.Value - rating) < 1e-9
                && string.Equals(mv.RatingSource, source, StringComparison.Ordinal))
                return; // 幂等
            ctx.Attach(mv);
            ctx.Entry(mv).Property(x => x.ExternalRating).CurrentValue = rating;
            ctx.Entry(mv).Property(x => x.ExternalRating).IsModified = true;
            ctx.Entry(mv).Property(x => x.RatingSource).CurrentValue = source;
            ctx.Entry(mv).Property(x => x.RatingSource).IsModified = true;
            ctx.SaveChanges();
        }
        catch (Exception ex) { Log.Error(ex, "替代源：主库回写失败 Id={Id}", id); }
    }

    /// <summary>礼貌限速：确保距上一次网络请求已过去 <see cref="AltGapSeconds"/>。</summary>
    private static DateTime _lastReq = DateTime.MinValue;
    private static async Task PaceAsync(CancellationToken ct)
    {
        var since = DateTime.UtcNow - _lastReq;
        var need = TimeSpan.FromSeconds(AltGapSeconds) - since;
        if (need > TimeSpan.Zero)
            await Task.Delay(need, ct);
        _lastReq = DateTime.UtcNow;
    }
}
