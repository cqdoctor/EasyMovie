using System.IO;
using System.Linq;
using EasyMovie.Core.Enums;
using EasyMovie.Core.Models;
using EasyMovie.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EasyMovie.Tests.Core.Tests;

/// <summary>
/// RatingBackfill 契约测试：用临时 SQLite 主库 + 临时 cache.db 真实回放，覆盖匹配 / 写回 / 幂等，
/// 不触碰用户真实数据（遵循 TextCleanupMigrationTests 的「可测试」原则）。
/// </summary>
public class RatingBackfillTests
{
    private static DbContextOptions<MovieDbContext> MainOptions(string path)
        => new DbContextOptionsBuilder<MovieDbContext>().UseSqlite($"Data Source={path}").Options;

    private static DbContextOptions<CacheDbContext> CacheOptions(string path)
        => new DbContextOptionsBuilder<CacheDbContext>().UseSqlite($"Data Source={path}").Options;

    [Fact]
    public void Run_ShouldBackfillExternalRating_ByNormalizedTitle()
    {
        var mainPath = Path.Combine(Path.GetTempPath(), $"em_backfill_main_{Path.GetRandomFileName()}.db");
        var cachePath = Path.Combine(Path.GetTempPath(), $"em_backfill_cache_{Path.GetRandomFileName()}.db");
        var flag = Path.Combine(Path.GetTempPath(), $"em_backfill_flag_{Path.GetRandomFileName()}");
        try
        {
            // 主库：一部无个人评分的影片
            using (var m = new MovieDbContext(MainOptions(mainPath)))
            {
                m.Database.EnsureCreated();
                m.Movies.Add(new Movie { Title = "盗梦空间", Year = 2010 });
                m.SaveChanges();
            }
            // 缓存库：一条带评分的缓存（NormTitle 与主库归一化标题一致）
            using (var c = new CacheDbContext(CacheOptions(cachePath)))
            {
                c.Database.EnsureCreated();
                c.CachedMovies.Add(new CachedMovie
                {
                    Title = "盗梦空间",
                    NormTitle = "盗梦空间",
                    Year = 2010,
                    Rating = 7.8,
                    Source = "douban"
                });
                c.SaveChanges();
            }

            var changed = RatingBackfill.Run(MainOptions(mainPath), CacheOptions(cachePath), flag);

            changed.Should().Be(1);
            using var verify = new MovieDbContext(MainOptions(mainPath));
            var movie = verify.Movies.Single();
            movie.ExternalRating.Should().BeApproximately(7.8, 1e-9);
            movie.RatingSource.Should().Be("douban");
        }
        finally
        {
            // SQLite WAL 模式会残留 -wal/-shm 且可能短暂持锁，清理失败不影响测试结论
            foreach (var f in new[] { mainPath, cachePath, flag, mainPath + "-wal", mainPath + "-shm", cachePath + "-wal", cachePath + "-shm" })
                try { if (File.Exists(f)) File.Delete(f); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Run_ShouldPreferYearProximity_AndSkipWhenNoMatch()
    {
        var mainPath = Path.Combine(Path.GetTempPath(), $"em_backfill_main_{Path.GetRandomFileName()}.db");
        var cachePath = Path.Combine(Path.GetTempPath(), $"em_backfill_cache_{Path.GetRandomFileName()}.db");
        var flag = Path.Combine(Path.GetTempPath(), $"em_backfill_flag_{Path.GetRandomFileName()}");
        try
        {
            using (var m = new MovieDbContext(MainOptions(mainPath)))
            {
                m.Database.EnsureCreated();
                m.Movies.Add(new Movie { Title = "星际穿越", Year = 2014 }); // 有匹配
                m.Movies.Add(new Movie { Title = " completely unrelated xyz", Year = 2020 }); // 无匹配
                m.SaveChanges();
            }
            using (var c = new CacheDbContext(CacheOptions(cachePath)))
            {
                c.Database.EnsureCreated();
                // 同归一化标题但年份远离（2014 vs 2000）+ 一条年份接近
                c.CachedMovies.Add(new CachedMovie { Title = "星际穿越", NormTitle = "星际穿越", Year = 2000, Rating = 6.0, Source = "tmdb" });
                c.CachedMovies.Add(new CachedMovie { Title = "星际穿越", NormTitle = "星际穿越", Year = 2014, Rating = 9.1, Source = "douban" });
                c.SaveChanges();
            }

            var changed = RatingBackfill.Run(MainOptions(mainPath), CacheOptions(cachePath), flag);
            changed.Should().Be(1);

            using var verify = new MovieDbContext(MainOptions(mainPath));
            var matched = verify.Movies.Single(m => m.Title == "星际穿越");
            matched.ExternalRating.Should().BeApproximately(9.1, 1e-9); // 年份接近优先
            matched.RatingSource.Should().Be("douban");
            verify.Movies.Single(m => m.Title.Contains("unrelated")).ExternalRating.Should().NotHaveValue();
        }
        finally
        {
            // SQLite WAL 模式会残留 -wal/-shm 且可能短暂持锁，清理失败不影响测试结论
            foreach (var f in new[] { mainPath, cachePath, flag, mainPath + "-wal", mainPath + "-shm", cachePath + "-wal", cachePath + "-shm" })
                try { if (File.Exists(f)) File.Delete(f); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Run_ShouldBeIdempotent_AndSkipWhenFlagExists()
    {
        var mainPath = Path.Combine(Path.GetTempPath(), $"em_backfill_main_{Path.GetRandomFileName()}.db");
        var cachePath = Path.Combine(Path.GetTempPath(), $"em_backfill_cache_{Path.GetRandomFileName()}.db");
        var flag = Path.Combine(Path.GetTempPath(), $"em_backfill_flag_{Path.GetRandomFileName()}");
        try
        {
            using (var m = new MovieDbContext(MainOptions(mainPath)))
            {
                m.Database.EnsureCreated();
                m.Movies.Add(new Movie { Title = "盗梦空间", Year = 2010 });
                m.SaveChanges();
            }
            using (var c = new CacheDbContext(CacheOptions(cachePath)))
            {
                c.Database.EnsureCreated();
                c.CachedMovies.Add(new CachedMovie { Title = "盗梦空间", NormTitle = "盗梦空间", Year = 2010, Rating = 7.8, Source = "douban" });
                c.SaveChanges();
            }

            var first = RatingBackfill.Run(MainOptions(mainPath), CacheOptions(cachePath), flag);
            first.Should().Be(1);

            // flag 已写：第二次应直接跳过（0 改写），且不改数据
            var second = RatingBackfill.Run(MainOptions(mainPath), CacheOptions(cachePath), flag);
            second.Should().Be(0);

            using var verify = new MovieDbContext(MainOptions(mainPath));
            var movie = verify.Movies.Single();
            movie.ExternalRating.Should().BeApproximately(7.8, 1e-9);
        }
        finally
        {
            // SQLite WAL 模式会残留 -wal/-shm 且可能短暂持锁，清理失败不影响测试结论
            foreach (var f in new[] { mainPath, cachePath, flag, mainPath + "-wal", mainPath + "-shm", cachePath + "-wal", cachePath + "-shm" })
                try { if (File.Exists(f)) File.Delete(f); } catch { /* ignore */ }
        }
    }
}
