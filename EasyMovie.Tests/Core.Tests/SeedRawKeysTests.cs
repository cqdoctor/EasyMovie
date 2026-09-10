using System.IO;
using System.Linq;
using EasyMovie.Core.Models;
using EasyMovie.Data;
using EasyMovie.Tools.MovieApi;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EasyMovie.Tests.Core.Tests;

/// <summary>
/// 回归测试：补全链路的「键空间对齐」——
/// 历史上豆瓣补全把评分写进 cache.db 的<b>豆瓣清洗键</b>，而主库影片名是带编码/别名后缀的<b>脏标题</b>，
/// 两边键空间永不相交，导致每一个成功匹配的评分都被永久孤立、到不了主库（「豆瓣一直补不上」的真正根因）。
/// 修复后：① 补全服务会额外写一条主库脏标题键；② SeedRawKeysFromLibrary 用中文关键词双向子串
/// 把 cache.db 既有 donor 映射到主库脏标题键。本测试锁定这两条路径。
/// </summary>
public class SeedRawKeysTests
{
    private static DbContextOptions<MovieDbContext> MainOptions(string p)
        => new DbContextOptionsBuilder<MovieDbContext>().UseSqlite($"Data Source={p}").Options;

    private static DbContextOptions<CacheDbContext> CacheOptions(string p)
        => new DbContextOptionsBuilder<CacheDbContext>().UseSqlite($"Data Source={p}").Options;

    [Fact]
    public void SeedRawKeys_ShouldMapDonorByChineseSubstring_ThenSyncTransfers()
    {
        var mainPath = Path.Combine(Path.GetTempPath(), $"em_seed_main_{Path.GetRandomFileName()}.db");
        var cachePath = Path.Combine(Path.GetTempPath(), $"em_seed_cache_{Path.GetRandomFileName()}.db");
        try
        {
            // 主库：脏标题影片「白象@危城悍将 White Elephant AC3」（2022）
            using (var m = new MovieDbContext(MainOptions(mainPath)))
            {
                m.Database.EnsureCreated();
                m.Movies.Add(new Movie { Title = "白象@危城悍将 White Elephant AC3", Year = 2022 });
                m.SaveChanges();
            }
            // cache.db：既有干净 donor「白象」（2022, 4.7）—— 模拟历史已抓回、但键不匹配的评分
            using (var c = new CacheDbContext(CacheOptions(cachePath)))
            {
                c.Database.EnsureCreated();
                c.CachedMovies.Add(new CachedMovie
                {
                    Title = "白象",
                    NormTitle = "白象",
                    Year = 2022,
                    Rating = 4.7,
                    Source = "douban"
                });
                c.SaveChanges();
            }

            // 用中文双向子串 + 放宽年份容差把 donor 映射到主库脏标题键
            using (var c = new CacheDbContext(CacheOptions(cachePath)))
            {
                var seeded = LocalMovieCache.SeedRawKeysFromLibrary(
                    new[] { ("白象@危城悍将 White Elephant AC3", (int?)2022) },
                    yearTolerance: 8, externalCtx: c);
                seeded.Should().Be(1);
            }

            // Sync 必须能用主库脏标题命中并回写
            var changed = RatingBackfill.Sync(MainOptions(mainPath), CacheOptions(cachePath));
            changed.Should().Be(1);
            using var verify = new MovieDbContext(MainOptions(mainPath));
            var mv = verify.Movies.Single();
            mv.ExternalRating.Should().BeApproximately(4.7, 1e-9);
            mv.RatingSource.Should().Be("douban");
        }
        finally
        {
            foreach (var f in new[] { mainPath, cachePath, mainPath + "-wal", mainPath + "-shm", cachePath + "-wal", cachePath + "-shm" })
                try { if (File.Exists(f)) File.Delete(f); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void SeedRawKeys_ShouldRespectYearTolerance_NotMergeDistantYears()
    {
        var mainPath = Path.Combine(Path.GetTempPath(), $"em_seed_main_{Path.GetRandomFileName()}.db");
        var cachePath = Path.Combine(Path.GetTempPath(), $"em_seed_cache_{Path.GetRandomFileName()}.db");
        try
        {
            using (var m = new MovieDbContext(MainOptions(mainPath)))
            {
                m.Database.EnsureCreated();
                // 2025 年的「奇遇 奇遇大富翁」，与 1985 年的 donor「奇遇」仅中文子串相同，年份相差 40
                m.Movies.Add(new Movie { Title = "奇遇 奇遇大富翁", Year = 2025 });
                m.SaveChanges();
            }
            using (var c = new CacheDbContext(CacheOptions(cachePath)))
            {
                c.Database.EnsureCreated();
                c.CachedMovies.Add(new CachedMovie
                {
                    Title = "奇遇",
                    NormTitle = "奇遇",
                    Year = 1985,
                    Rating = 7.3,
                    Source = "douban"
                });
                c.SaveChanges();
            }

            using (var c = new CacheDbContext(CacheOptions(cachePath)))
            {
                var seeded = LocalMovieCache.SeedRawKeysFromLibrary(
                    new[] { ("奇遇 奇遇大富翁", (int?)2025) },
                    yearTolerance: 8, externalCtx: c);
                seeded.Should().Be(0, "年份相差 40 远超容差，不得误并");
            }

            RatingBackfill.Sync(MainOptions(mainPath), CacheOptions(cachePath)).Should().Be(0);
            using var verify = new MovieDbContext(MainOptions(mainPath));
            verify.Movies.Single().ExternalRating.Should().NotHaveValue();
        }
        finally
        {
            foreach (var f in new[] { mainPath, cachePath, mainPath + "-wal", mainPath + "-shm", cachePath + "-wal", cachePath + "-shm" })
                try { if (File.Exists(f)) File.Delete(f); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Sync_ShouldTransferMainKeyedCacheRow_DirtyFilenameTitle()
    {
        // 直接锁定「补全服务写入主库脏标题键」这一契约：cache 里若存在一条以主库脏标题为键、
        // 带评分的记录，Sync 必须能命中并回写（修复前这类记录根本不会产生，故 Sync 永远 0 命中）。
        var mainPath = Path.Combine(Path.GetTempPath(), $"em_sync_main_{Path.GetRandomFileName()}.db");
        var cachePath = Path.Combine(Path.GetTempPath(), $"em_sync_cache_{Path.GetRandomFileName()}.db");
        try
        {
            using (var m = new MovieDbContext(MainOptions(mainPath)))
            {
                m.Database.EnsureCreated();
                m.Movies.Add(new Movie { Title = "困兽Death Stranding EAC3", Year = 2023 });
                m.SaveChanges();
            }
            using (var c = new CacheDbContext(CacheOptions(cachePath)))
            {
                c.Database.EnsureCreated();
                // 以主库脏标题为键（这正是 WriteBackfill 现在写入的形态）
                c.CachedMovies.Add(new CachedMovie
                {
                    Title = "困兽Death Stranding EAC3",
                    NormTitle = "困兽deathstrandingeac3",
                    Year = 2023,
                    Rating = 4.3,
                    Source = "douban"
                });
                c.SaveChanges();
            }

            RatingBackfill.Sync(MainOptions(mainPath), CacheOptions(cachePath)).Should().Be(1);
            using var verify = new MovieDbContext(MainOptions(mainPath));
            var mv = verify.Movies.Single();
            mv.ExternalRating.Should().BeApproximately(4.3, 1e-9);
            mv.RatingSource.Should().Be("douban");
        }
        finally
        {
            foreach (var f in new[] { mainPath, cachePath, mainPath + "-wal", mainPath + "-shm", cachePath + "-wal", cachePath + "-shm" })
                try { if (File.Exists(f)) File.Delete(f); } catch { /* ignore */ }
        }
    }
}
