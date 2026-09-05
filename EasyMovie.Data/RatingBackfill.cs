using System;
using System.IO;
using System.Text.RegularExpressions;
using EasyMovie.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace EasyMovie.Data;

/// <summary>
/// 一次性外部评分回填：把本地缓存库（cache.db 的 CachedMovies）里已有的外部评分（豆瓣/TMDB/1905…），
/// 按归一化片名匹配回写到业务主库 Movies 的 <see cref="Movie.ExternalRating"/> / <see cref="Movie.RatingSource"/>。
///
/// <para><b>动机（B1）</b>：统计页原本只用个人评分 <see cref="Movie.Rating"/>（int? 1-10），而个人评分全库几乎都为 null，
/// 导致「评分区整块空白」「平均评分 0.0」。外部评分早已持久化在本地 cache.db（实测 290 部主库命中 254、
/// 其中 227 部有评分），<b>零网络依赖</b>——所以「老是获取不到」不成立。回填后统计页改读外部评分即可填满空白。</para>
///
/// <para><b>字段分离</b>：外部评分（0-10 小数）与个人评分（int? 星级）是两套不同口径，必须分字段存储、不可复用，
/// 否则会互相覆盖。effective rating 的规则（个人优先、缺失回退外部）在 StatisticsService 中统一处理。</para>
///
/// <para><b>匹配口径</b>：与主库查询路径 LocalMovieCache.Lookup 一致——按归一化标题（NormTitle）或归一化原名
/// （NormOriginal）命中，年份接近（±1）且评分最高者优先。归一化函数与 LocalMovieCache.NormalizeKey 完全相同
/// （去标点空白、保留字母数字与中日韩、转小写），不依赖 EasyMovie.Tools，避免循环引用。</para>
///
/// <para><b>写回方式</b>：仅附载主键的桩实体按列标脏回写 ExternalRating / RatingSource，绝不加载整行
/// （含 PosterData 大 BLOB），与 TextCleanupMigration 同一原则。幂等：flag 文件存在即跳过；重复运行写回相同值。</para>
/// </summary>
public static class RatingBackfill
{
    private static readonly Regex _norm = new(@"[^\p{L}\p{N}]", RegexOptions.Compiled);

    /// <summary>归一化键：与 LocalMovieCache.NormalizeKey 完全一致。</summary>
    private static string NormalizeKey(string s) => _norm.Replace(s ?? "", "").ToLowerInvariant();

    /// <summary>
    /// 执行回填。flag 文件已存在时直接返回 0（只跑一次）。
    /// </summary>
    /// <param name="mainOptions">业务主库选项（DbHelper.CreateOptions()）</param>
    /// <param name="cacheOptions">缓存库选项（CacheDbContext.CreateOptions()）</param>
    /// <param name="flagPath">完成标志文件路径</param>
    /// <returns>被改写的行数；跳过或无需改写时返回 0</returns>
    public static int Run(
        DbContextOptions<MovieDbContext> mainOptions,
        DbContextOptions<CacheDbContext> cacheOptions,
        string flagPath)
    {
        if (string.IsNullOrEmpty(flagPath)) throw new ArgumentException("回填标志文件路径不能为空", nameof(flagPath));
        if (File.Exists(flagPath)) return 0;

        // 读缓存库：只取匹配与回填所需的字段，避免多余加载
        var cacheEntries = ReadCacheRatings(cacheOptions);
        if (cacheEntries.Count == 0)
        {
            // 没有缓存可回填，仍写 flag，避免每次启动都空跑
            File.WriteAllText(flagPath, DateTime.UtcNow.ToString("O"));
            return 0;
        }

        using var ctx = new MovieDbContext(mainOptions);

        // 只投影匹配所需字段（Id/Title/Year），不读 PosterData 等大 BLOB
        var movies = ctx.Movies
            .Select(m => new { m.Id, m.Title, m.Year, m.ExternalRating, m.RatingSource })
            .AsNoTracking()
            .ToList();

        var changed = 0;
        foreach (var mv in movies)
        {
            var match = Match(cacheEntries, mv.Title, mv.Year);
            if (match is null) continue;

            // 已回填且值相同则跳过（幂等，省一次写入）
            if (mv.ExternalRating.HasValue && Math.Abs(mv.ExternalRating.Value - match.Value.Rating) < 1e-9
                && string.Equals(mv.RatingSource, match.Value.Source, StringComparison.Ordinal))
                continue;

            var tracked = new Movie { Id = mv.Id };
            ctx.Attach(tracked);
            ctx.Entry(tracked).Property(x => x.ExternalRating).CurrentValue = match.Value.Rating;
            ctx.Entry(tracked).Property(x => x.ExternalRating).IsModified = true;
            ctx.Entry(tracked).Property(x => x.RatingSource).CurrentValue = match.Value.Source;
            ctx.Entry(tracked).Property(x => x.RatingSource).IsModified = true;
            changed++;
        }

        if (changed > 0) ctx.SaveChanges();

        File.WriteAllText(flagPath, DateTime.UtcNow.ToString("O"));
        return changed;
    }

    private sealed record CacheEntry(string NormTitle, string? NormOriginal, double Rating, string Source, int Year);

    private static List<CacheEntry> ReadCacheRatings(DbContextOptions<CacheDbContext> cacheOptions)
    {
        using var cache = new CacheDbContext(cacheOptions);
        cache.Database.EnsureCreated();
        return cache.CachedMovies
            .Where(c => c.Rating.HasValue && c.Rating.Value > 0)
            .Select(c => new CacheEntry(c.NormTitle, c.NormOriginal, c.Rating!.Value, c.Source, c.Year))
            .AsNoTracking()
            .ToList();
    }

    /// <summary>按归一化标题/原名命中，年份接近（±1）且评分最高者优先；无年份接近者退回全部中评分最高者。</summary>
    private static (double Rating, string Source)? Match(List<CacheEntry> entries, string title, int year)
    {
        var q = NormalizeKey(title);
        if (string.IsNullOrEmpty(q)) return null;

        var candidates = entries
            .Where(c => (c.NormTitle == q) || (!string.IsNullOrEmpty(c.NormOriginal) && c.NormOriginal == q))
            .ToList();
        if (candidates.Count == 0) return null;

        var near = candidates
            .Where(c => year > 0 && c.Year > 0 && Math.Abs(c.Year - year) <= 1)
            .ToList();
        var pool = near.Count > 0 ? near : candidates;

        var best = pool.OrderByDescending(c => c.Rating).First();
        return (best.Rating, best.Source);
    }
}
