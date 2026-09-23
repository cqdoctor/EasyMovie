using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EasyMovie.Core;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;
using Serilog;

namespace EasyMovie.Tools.MovieApi;

/// <summary>
/// 慢速补全报告
/// </summary>
public class DoubanBackfillReport
{
    public int Total { get; set; }
    public int Done { get; set; }
    public int Filled { get; set; }     // 成功写入/补缺
    public int Skipped { get; set; }    // 无匹配/被冷却跳过
    public string? Error { get; set; }
    public bool StoppedByThrottle { get; set; }
}

/// <summary>
/// 慢速、防封控的豆瓣 2020+ 元数据补全服务。
///
/// 设计原则（严格不触碰已有的 MinIntervalMs=1500 与冷却公式）：
///  - 外层慢速节奏：每两次豆瓣请求之间至少间隔 <see cref="BackfillGapSeconds"/>（+ 抖动），
///    远低于“单 IP 批量 ~10-12 次触发 need_login 全局风控”的阈值。
///  - 每日上限 <see cref="BackfillDailyCap"/>，跨天自动重置；长任务自然摊到多日。
///  - 尊重豆瓣客户端既有冷却：一旦 <see cref="DoubanApiClient.IsThrottled"/>（含新增的 need_login 信号），
///    立即安全停止，绝不重试加重封禁。
///  - 仅写入离线缓存 cache.db，绝不修改用户个人影片库（与 MetadataSyncService 一致）。
///  - 可取消。
///
/// 用法：由调用方（如设置页）先从用户片库筛出 Year>=2020 且 cache.db 字段缺失的影片，
/// 组装成队列传入 <see cref="RunAsync"/>。本服务只负责“慢速、安全地从豆瓣取回并落库”。
/// </summary>
public static class DoubanBackfillService
{
    /// <summary>每次豆瓣请求最小间隔（秒）。可调小/调大以改变“慢”的程度。默认 12s。</summary>
    public static int BackfillGapSeconds = 12;

    /// <summary>每日请求上限（防止任何单日累积触发风控）。默认 50。</summary>
    public static int BackfillDailyCap = 50;

    private static DateTime _dayWindowStartUtc = DateTime.UtcNow.Date;
    private static int _dayCount = 0;

    /// <summary>
    /// 慢速补全。
    /// </summary>
    /// <param name="queue">待补全影片 (标题, 年份)。</param>
    /// <param name="progress">进度回调（人类可读文本）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="clientFactory">豆瓣客户端工厂（测试可注入假客户端；默认 new DoubanApiClient()）。</param>
    /// <param name="writeAction">落库动作（默认 LocalMovieCache.UpsertOrMerge；测试可替换为收集器/空操作）。</param>
    public static async Task<DoubanBackfillReport> RunAsync(
        IEnumerable<(string Title, int? Year)> queue,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        Func<IMovieApiClient>? clientFactory = null,
        Action<MovieSearchResult>? writeAction = null)
    {
        var items = queue as List<(string, int?)> ?? queue.ToList();
        var rep = new DoubanBackfillReport { Total = items.Count };
        clientFactory ??= () => new DoubanApiClient();

        if (items.Count == 0) { progress?.Report("没有需要补全的 2020+ 影片。"); return rep; }

        var client = clientFactory();

        // 按影片重试：need_login 是约 50% 概率的瞬时拒绝（非真封禁），同一影片重试仍保持 12s 节奏，
        // 不会密集探测加重风控；硬封禁（302 风控页/验证码）才立即停 run。
        const int maxSearchAttempts = 3;
        // 连续整窗被墙守卫：若连续多部影片搜索都拿不到任何数据（非“有结果但匹配不上”），
        // 说明该 IP 在本窗口已被限流，及时停 run 以免空转浪费每日配额；下次调度自动继续。
        const int wallStopThreshold = 5;
        var consecutiveEmpty = 0;

        foreach (var (title, year) in items)
        {
            if (ct.IsCancellationRequested) { rep.Error = "已取消。"; break; }

            // 每日窗口重置
            if (DateTime.UtcNow.Date != _dayWindowStartUtc) { _dayWindowStartUtc = DateTime.UtcNow.Date; _dayCount = 0; }
            if (_dayCount >= BackfillDailyCap)
            {
                rep.Error = $"已达每日上限 {BackfillDailyCap} 次，明日自动继续。";
                progress?.Report(rep.Error);
                break;
            }

            // 硬封禁（302 风控页/验证码）：立即安全停止
            if (client.IsThrottled())
            {
                rep.StoppedByThrottle = true;
                rep.Error = "触发豆瓣硬封控，已安全停止补全。可稍后重试。";
                progress?.Report(rep.Error);
                break;
            }

            // 外层慢速节奏：确保距“任何一次”豆瓣请求已过去 Gap + 抖动
            await PaceAsync(ct);

            // 1) 标题搜索（按影片重试：soft 限流为概率性，重试仍保持节奏，不密集）
            MovieSearchResponse? resp = null;
            var attempts = 0;
            while (attempts < maxSearchAttempts)
            {
                try
                {
                    resp = await client.SearchAsync(new MovieSearchRequest { Keyword = title, Page = 1, PageSize = 5 }, ct);
                }
                catch (Exception ex) { Log.Error(ex, "补全：搜索异常 {Title}", title); resp = null; break; }

                // 硬封禁（302 风控页/验证码/禁止访问）：立即停 run，不重试
                if (client.IsThrottled())
                {
                    rep.StoppedByThrottle = true;
                    rep.Error = "触发豆瓣硬封控（need_login/风控页），已安全停止补全。";
                    progress?.Report(rep.Error);
                    break;
                }
                if (resp != null && resp.Results.Count > 0) break;   // 拿到候选，无需重试
                attempts++;
                if (attempts < maxSearchAttempts) await PaceAsync(ct);  // 同影片重试仍按节奏等待
            }
            if (rep.StoppedByThrottle) break;   // 硬封禁已设置，退出循环（rep 为局部 new，恒非 null）

            if (resp == null || resp.Results.Count == 0)
            {
                // 配额软限流（HTTP 200 + error_info="搜索访问太频繁" + items=[]）：
                // 这不是「豆瓣没收录这部片」，而是本窗口额度用尽。立即停 run，
                // 否则剩余影片会被一路误报成「无可靠匹配」，白白耗尽配额且掩盖真实原因。
                if (DoubanApiClient.LastSearchQuotaExceeded)
                {
                    rep.StoppedByThrottle = true;
                    rep.Error = "豆瓣搜索配额已用尽（搜索访问太频繁），已安全停止（下次调度继续）。";
                    progress?.Report(rep.Error);
                    break;
                }
                // 该影片多次搜索均无数据（概率性 need_login 或豆瓣确实无收录）
                rep.Skipped++;
                progress?.Report($"[跳过] 无可靠匹配：{title}");
                if (++consecutiveEmpty >= wallStopThreshold)
                {
                    rep.StoppedByThrottle = true;
                    rep.Error = "连续多部无数据，疑似整窗口被墙，已安全停止（下次调度继续）。";
                    progress?.Report(rep.Error);
                    break;
                }
                continue;
            }
            consecutiveEmpty = 0;   // 有候选：IP 响应正常，重置“被墙”计数

            var match = DoubanApiClient.PickBestMatch(resp.Results, title, year);
            if (match == null) { rep.Skipped++; progress?.Report($"[跳过] 无可靠匹配：{title}"); continue; }

            // 豆瓣条目存在、但评分人数不足时 rating.value = 0。这类条目没有任何可回写的评分，
            // 旧实现只看 Rating.HasValue 就判定「补全成功」——于是报告写着「已补全 4」而主库回写 0 部，
            // 还会把 Rating=0 的行写进 cache.db 占位。必须在此拦掉：既不计入 Filled，也不写缓存。
            if (!match.Rating.HasValue || match.Rating.Value <= 0)
            {
                rep.Skipped++;
                progress?.Report($"[跳过] 豆瓣条目暂无评分：{title}");
                continue;
            }

            // rexxar 搜索结果的 card_subtitle 已含 评分/年份/导演/主演/海报。若关键字段齐全，
            // 直接落库、省掉详情请求——同一配额窗口内可覆盖约 2 倍影片，也减少触发豆瓣
            // need_login 配额挑战的次数（当前该 IP 匿名额度已降到 ~5 请求/窗口）。
            var searchSuffices = match.Year > 0 && match.Rating.HasValue && match.Rating.Value > 0 &&
                !string.IsNullOrEmpty(match.Director) && !string.IsNullOrEmpty(match.Cast) &&
                !string.IsNullOrEmpty(match.PosterUrl);
            if (searchSuffices)
            {
                WriteBackfill(match, title, year, "douban");
                writeAction?.Invoke(match);
                rep.Filled++;
                Interlocked.Increment(ref _dayCount);
                rep.Done++;
                progress?.Report($"[已补全] {title}（{match.Year}）评分={match.Rating?.ToString() ?? "—"} 进度 {rep.Done}/{rep.Total}");
                continue;
            }

            // 2) 搜索结果字段不足，拉详情补齐
            MovieSearchResult? detail = null;
            try
            {
                detail = await client.GetDetailAsync(match.ExternalId ?? "", ct);
            }
            catch (Exception ex) { Log.Error(ex, "补全：详情异常 {Title}", title); rep.Skipped++; continue; }

            if (client.IsThrottled())
            {
                rep.StoppedByThrottle = true;
                rep.Error = "触发豆瓣封控（need_login），已安全停止补全。";
                progress?.Report(rep.Error);
                break;
            }
            if (detail == null) { rep.Skipped++; continue; }
            if (!detail.Rating.HasValue || detail.Rating.Value <= 0)
            {
                rep.Skipped++;
                progress?.Report($"[跳过] 豆瓣条目暂无评分：{title}");
                continue;
            }

            // 3) 合并落库（只补 cache.db，不碰个人库）
            WriteBackfill(detail, title, year, "douban");
            writeAction?.Invoke(detail);
            rep.Filled++;
            Interlocked.Increment(ref _dayCount);
            rep.Done++;
            progress?.Report($"[已补全] {title}（{detail.Year}）评分={detail.Rating?.ToString() ?? "—"} 进度 {rep.Done}/{rep.Total}");
        }

        return rep;
    }

    /// <summary>
    /// 慢速节奏等待：确保距“任何一次”豆瓣请求已过去 <see cref="BackfillGapSeconds"/> + 抖动，
    /// 避免密集探测加重风控。请求速率恒定，重试与首查共用此节奏。
    /// </summary>
    private static async Task PaceAsync(CancellationToken ct)
    {
        var since = DateTime.UtcNow - DoubanApiClient.LastRequestUtc;
        var need = TimeSpan.FromSeconds(BackfillGapSeconds) - since;
        if (need > TimeSpan.Zero)
        {
            var jitter = (int)(need.TotalMilliseconds * 0.3);
            var extra = jitter > 0 ? new Random().Next(0, jitter) : 0;
            await Task.Delay(need + TimeSpan.FromMilliseconds(extra), ct);
        }
    }

    /// <summary>
    /// 落库（双键）：把一次成功的豆瓣匹配结果同时写进两条缓存记录——
    /// ① 按豆瓣清洗标题键（通用缓存 / 去重，供其它影片按干净片名命中）；
    /// ② 按<b>主库原始标题键</b>（队列传入的影片文件名，常带编码/别名后缀，如「白象@危城悍将 White Elephant AC3」）。
    ///
    /// <para><b>这是历史「豆瓣一直补不上」的真正根因修复</b>：旧代码只写 ①（豆瓣清洗键），而
    /// <see cref="RatingBackfill.Sync"/> 与主库读取路径 <see cref="LocalMovieCache.Lookup"/> 都是用
    /// <b>主库脏标题</b>去 cache.db 匹配的。两边键空间永不相交，导致每一个成功匹配到的评分都被永久
    /// 孤立在缓存里、永远到不了主库。补上 ② 后，Sync 即可用主库标题直接命中并回写。</para>
    /// </summary>
    private static void WriteBackfill(MovieSearchResult r, string mainTitle, int? mainYear, string source)
    {
        if (r == null) return;
        // ① 豆瓣清洗键（保留通用缓存 / 去重）
        LocalMovieCache.UpsertOrMerge(r, source);
        // ② 主库原始标题键：让 Sync / Lookup 能用主库脏标题命中本次豆瓣评分
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
            Language = r.Language,
            PosterUrl = r.PosterUrl,
            Source = source,
            ExternalId = r.ExternalId,
        };
        LocalMovieCache.UpsertOrMerge(mainKeyed, source);
    }
}
