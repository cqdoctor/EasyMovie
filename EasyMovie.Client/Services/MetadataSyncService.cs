using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EasyMovie.Client.Helpers;
using Microsoft.EntityFrameworkCore;
using EasyMovie.Core;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;
using EasyMovie.Tools.MovieApi;
using Serilog;

namespace EasyMovie.Client.Services;

/// <summary>
/// 在线元数据同步服务：对已经绑定了豆瓣/TMDB 外部 ID 的电影，按外部 ID 直接拉取最新详情并刷新本地元数据。
/// 仅更新“外部来源”字段（导演/演员/国家/语言/简介/海报/Runtime/年份），绝不触碰个人数据
/// （个人评分 Rating、观看状态、笔记、收藏、片名等），与 MovieListView 既有的刷新逻辑保持一致，安全可回退。
/// 未配置外部 ID 的电影会被跳过，不会按片名重新搜索。
/// </summary>
public static class MetadataSyncService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly SemaphoreSlim Gate = new(4);

    /// <summary>同步单部电影（依据其 DoubanId/TmdbId）。返回是否有字段被更新。</summary>
    public static async Task<bool> SyncMovieByExternalIdAsync(Movie m, CancellationToken ct = default)
    {
        if (m == null) return false;

        IMovieApiClient? client = null;
        string? extId = null;
        if (!string.IsNullOrEmpty(m.DoubanId)) { client = new DoubanApiClient(); extId = m.DoubanId; }
        else if (!string.IsNullOrEmpty(m.TmdbId)) { client = new TmdbApiClient(AppSettings.TmdbApiKey ?? ""); extId = m.TmdbId; }
        else return false; // 无外部 ID，无法按 ID 同步

        MovieSearchResult? info = null;
        try { info = await client.GetDetailAsync(extId, ct); }
        catch (Exception ex) { Log.Error(ex, "同步电影元数据失败: {Title}", m.Title); return false; }
        if (info == null) return false;

        var updated = false;

        // 以下更新逻辑与 MovieListView 的刷新元数据保持一致，且刻意不修改个人评分/状态/笔记/片名。
        if (!string.IsNullOrEmpty(info.Director) && info.Director != m.Director) { m.Director = info.Director; updated = true; }
        if (!string.IsNullOrEmpty(info.Cast) && info.Cast != m.Cast) { m.Cast = info.Cast; updated = true; }
        if (!string.IsNullOrEmpty(info.Country) && info.Country != m.Country) { m.Country = info.Country; updated = true; }
        if (!string.IsNullOrEmpty(info.Language) && info.Language != m.Language) { m.Language = info.Language; updated = true; }
        if (!string.IsNullOrEmpty(info.Synopsis) && info.Synopsis != m.Synopsis) { m.Synopsis = info.Synopsis; updated = true; }
        if (info.Runtime.HasValue && info.Runtime != m.Runtime) { m.Runtime = info.Runtime; updated = true; }
        if (info.Year > 0 && info.Year != m.Year) { m.Year = info.Year; updated = true; }

        // 海报：仅在 URL 变化或本地缺失时重新下载（去重由 PosterCache 兜底）
        var needPoster = (info.PosterUrl != m.PosterUrl) ||
                         (m.PosterData == null && !PosterCache.Exists(m.Id));
        if (needPoster && !string.IsNullOrEmpty(info.PosterUrl))
        {
            try
            {
                var posterBytes = await DownloadPosterAsync(m.Id, info.PosterUrl, ct);
                if (posterBytes != null)
                {
                    m.PosterData = posterBytes;
                    m.PosterUrl = info.PosterUrl;
                    updated = true;
                }
            }
            catch (Exception ex) { Log.Error(ex, "同步海报下载失败(已忽略): {Title}", m.Title); }
        }

        return updated;
    }

    /// <summary>
    /// 同步所有已绑定外部 ID 的电影。带并发限流与进度上报；整体异常被吞，不影响主流程。
    /// </summary>
    public static async Task SyncAllAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        List<Movie> movies;
        try
        {
            using var ctx = DbHelper.CreateContext();
            movies = await ctx.Movies
                .Where(m => !string.IsNullOrEmpty(m.DoubanId) || !string.IsNullOrEmpty(m.TmdbId))
                .ToListAsync(ct);
        }
        catch (Exception ex) { Log.Error(ex, "同步：加载待同步电影失败"); return; }

        if (movies.Count == 0)
        {
            progress?.Report(LanguageManager.GetString("Sync_NoMovies") ?? "没有可同步的电影（需先通过在线搜索绑定豆瓣/TMDB ID）");
            return;
        }

        var done = 0;
        var updatedCount = 0;
        var failed = 0;
        foreach (var m in movies)
        {
            if (ct.IsCancellationRequested) break;
            await Gate.WaitAsync(ct);
            try
            {
                // 为每次保存使用独立上下文，避免长时间事务占用
                using var ctx = DbHelper.CreateContext();
                var tracked = await ctx.Movies.FindAsync(new object[] { m.Id }, ct);
                if (tracked == null) continue;

                var wasUpdated = await SyncMovieByExternalIdAsync(tracked, ct);
                if (wasUpdated)
                {
                    await ctx.SaveChangesAsync(ct);
                    Interlocked.Increment(ref updatedCount);
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                Log.Error(ex, "同步单部电影失败: {Title}", m.Title);
            }
            finally { Gate.Release(); }

            var d = Interlocked.Increment(ref done);
            progress?.Report(string.Format(
                LanguageManager.GetString("Sync_Progress") ?? "已同步 {0}/{1}（更新 {2}，失败 {3}）",
                d, movies.Count, updatedCount, failed));
        }

        progress?.Report(string.Format(
            LanguageManager.GetString("Sync_Done") ?? "同步完成：共 {0} 部，更新 {1} 部，失败 {2} 部",
            movies.Count, updatedCount, failed));
    }

    private static async Task<byte[]?> DownloadPosterAsync(int id, string url, CancellationToken ct)
    {
        // 缓存命中直接返回，避免重复下载
        if (PosterCache.Exists(id)) return PosterCache.LoadBytes(id);
        try
        {
            var bytes = await Http.GetByteArrayAsync(url, ct);
            PosterCache.Save(id, bytes);
            return bytes;
        }
        catch (Exception ex) { Log.Error(ex, "同步：海报下载失败(已忽略) id={Id}", id); }
        return null;
    }
}
