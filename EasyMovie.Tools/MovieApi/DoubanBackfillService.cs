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
        writeAction ??= r => LocalMovieCache.UpsertOrMerge(r, "douban");
        clientFactory ??= () => new DoubanApiClient();

        if (items.Count == 0) { progress?.Report("没有需要补全的 2020+ 影片。"); return rep; }

        var client = clientFactory();

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

            // 既有的豆瓣冷却/封控：立即安全停止（不等待、不重试，避免加重风控）
            if (client.IsThrottled())
            {
                rep.StoppedByThrottle = true;
                rep.Error = "触发豆瓣冷却/封控，已安全停止补全（不会重试）。可稍后重试。";
                progress?.Report(rep.Error);
                break;
            }

            // 外层慢速节奏：确保距“任何一次”豆瓣请求已过去 Gap + 抖动
            var since = DateTime.UtcNow - DoubanApiClient.LastRequestUtc;
            var need = TimeSpan.FromSeconds(BackfillGapSeconds) - since;
            if (need > TimeSpan.Zero)
            {
                var jitter = (int)(need.TotalMilliseconds * 0.3);
                var extra = jitter > 0 ? new Random().Next(0, jitter) : 0;
                await Task.Delay(need + TimeSpan.FromMilliseconds(extra), ct);
            }

            // 1) 标题搜索
            MovieSearchResponse? resp = null;
            try
            {
                resp = await client.SearchAsync(new MovieSearchRequest { Keyword = title, Page = 1, PageSize = 5 }, ct);
            }
            catch (Exception ex) { Log.Error(ex, "补全：搜索异常 {Title}", title); rep.Skipped++; continue; }

            // 搜索后即检查封控（任何请求后都可能被限）
            if (client.IsThrottled())
            {
                rep.StoppedByThrottle = true;
                rep.Error = "触发豆瓣封控（need_login），已安全停止补全。";
                progress?.Report(rep.Error);
                break;
            }

            var match = DoubanApiClient.PickBestMatch(resp?.Results ?? new(), title, year);
            if (match == null) { rep.Skipped++; progress?.Report($"[跳过] 无可靠匹配：{title}"); continue; }

            // rexxar 搜索结果的 card_subtitle 已含 评分/年份/导演/主演/海报。若关键字段齐全，
            // 直接落库、省掉详情请求——同一配额窗口内可覆盖约 2 倍影片，也减少触发豆瓣
            // need_login 配额挑战的次数（当前该 IP 匿名额度已降到 ~5 请求/窗口）。
            var searchSuffices = match.Year > 0 && match.Rating.HasValue &&
                !string.IsNullOrEmpty(match.Director) && !string.IsNullOrEmpty(match.Cast) &&
                !string.IsNullOrEmpty(match.PosterUrl);
            if (searchSuffices)
            {
                writeAction(match);
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

            // 3) 合并落库（只补 cache.db，不碰个人库）
            writeAction(detail);
            rep.Filled++;
            Interlocked.Increment(ref _dayCount);
            rep.Done++;
            progress?.Report($"[已补全] {title}（{detail.Year}）评分={detail.Rating?.ToString() ?? "—"} 进度 {rep.Done}/{rep.Total}");
        }

        return rep;
    }
}
