using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EasyMovie.Core.Enums;
using EasyMovie.Core.Models;
using EasyMovie.Core.Services;
using EasyMovie.Data;
using EasyMovie.Data.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace EasyMovie.Tests.Core.Tests;

/// <summary>
/// 锁定三个"孤儿"推荐方法（GetBySameDirector/GetBySameCategory/GetHighRatedUnwatched）
/// 的行为与性能契约：它们当前无调用方，但一旦启用绝不能重新引入"全库海报入内存"。
/// 因此必须走与 GetRecommendationsAsync 相同的两阶段窄投影——先算分（不读 PosterData），
/// 再只对 topN 加载完整实体。本测试同时证明：带海报的库上调用时托管堆分配远低于全量读。
/// </summary>
[Collection("Benchmark")]
public class RecommendationOrphanTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly List<string> _tempFiles = new();
    private const int PosterBytes = 64 * 1024;

    public RecommendationOrphanTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        foreach (var f in _tempFiles.SelectMany(p => new[] { p + "-wal", p + "-shm" }))
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        GC.SuppressFinalize(this);
    }

    private (MovieDbContext ctx, RecommendationService svc) NewService()
    {
        var dir = Path.Combine(Path.GetTempPath(), "EasyMovieOrphan");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"orphan_{Guid.NewGuid():N}.db");
        _tempFiles.Add(path);
        var options = new DbContextOptionsBuilder<MovieDbContext>().UseSqlite($"Data Source={path}").Options;
        var ctx = new MovieDbContext(options);
        ctx.Database.EnsureCreated();
        return (ctx, new RecommendationService(new MovieRepository(ctx)));
    }

    private static byte[] Poster() => new byte[PosterBytes];

    [Fact]
    public async Task GetBySameDirectorAsync_ReturnsUnwatchedSharingWatchedDirector()
    {
        var (ctx, svc) = NewService();
        var cat = new Category { Name = "剧情" };
        ctx.Categories.Add(cat); ctx.SaveChanges();

        ctx.Movies.Add(new Movie { Title = "A", Director = "张艺谋", WatchStatus = WatchStatus.Watched, CategoryId = cat.Id, PosterData = Poster(), SearchIndex = "a", FilePath = "a.mkv", CreatedAt = DateTime.UtcNow });
        ctx.Movies.Add(new Movie { Title = "B", Director = "张艺谋", IsFavorite = true, CategoryId = cat.Id, PosterData = Poster(), SearchIndex = "b", FilePath = "b.mkv", CreatedAt = DateTime.UtcNow });
        ctx.Movies.Add(new Movie { Title = "C", Director = "张艺谋", WatchStatus = WatchStatus.NotWatched, Rating = 8, CategoryId = cat.Id, PosterData = Poster(), SearchIndex = "c", FilePath = "c.mkv", CreatedAt = DateTime.UtcNow });
        ctx.Movies.Add(new Movie { Title = "D", Director = "张艺谋", WatchStatus = WatchStatus.NotWatched, Rating = 5, CategoryId = cat.Id, PosterData = Poster(), SearchIndex = "d", FilePath = "d.mkv", CreatedAt = DateTime.UtcNow });
        ctx.Movies.Add(new Movie { Title = "E", Director = "陈凯歌", WatchStatus = WatchStatus.NotWatched, Rating = 9, CategoryId = cat.Id, PosterData = Poster(), SearchIndex = "e", FilePath = "e.mkv", CreatedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();

        var result = await svc.GetBySameDirectorAsync(10);
        var titles = result.Select(r => r.Movie.Title).ToList();

        titles.Should().Contain("C");
        titles.Should().Contain("D");
        // 未看+收藏(B)仍在候选池（方法只排除 Watched），且同导演 → 应被推荐
        titles.Should().Contain("B");
        titles.Should().NotContain("E", "不同导演不应被推荐");
        titles.Should().NotContain("A", "已看不应被推荐");
        // 同导演匹配数相同(2)时按评分降序：C(8) 先于 D(5) 先于 B(无评分)
        result.FindIndex(r => r.Movie.Title == "C").Should().BeLessThan(result.FindIndex(r => r.Movie.Title == "D"));
        result.FindIndex(r => r.Movie.Title == "D").Should().BeLessThan(result.FindIndex(r => r.Movie.Title == "B"));
        result[0].Reason.Should().StartWith("同导演");
    }

    [Fact]
    public async Task GetBySameCategoryAsync_ReturnsUnwatchedInWatchedCategory()
    {
        var (ctx, svc) = NewService();
        var cat1 = new Category { Name = "科幻" };
        var cat2 = new Category { Name = "喜剧" };
        ctx.Categories.AddRange(cat1, cat2); ctx.SaveChanges();

        ctx.Movies.Add(new Movie { Title = "A", WatchStatus = WatchStatus.Watched, CategoryId = cat1.Id, PosterData = Poster(), SearchIndex = "a", FilePath = "a.mkv", CreatedAt = DateTime.UtcNow });
        ctx.Movies.Add(new Movie { Title = "B", WatchStatus = WatchStatus.NotWatched, CategoryId = cat1.Id, Rating = 7, PosterData = Poster(), SearchIndex = "b", FilePath = "b.mkv", CreatedAt = DateTime.UtcNow });
        ctx.Movies.Add(new Movie { Title = "C", WatchStatus = WatchStatus.NotWatched, CategoryId = cat2.Id, Rating = 9, PosterData = Poster(), SearchIndex = "c", FilePath = "c.mkv", CreatedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();

        var result = await svc.GetBySameCategoryAsync(10);
        var titles = result.Select(r => r.Movie.Title).ToList();
        titles.Should().Contain("B");
        titles.Should().NotContain("C", "不同分类不应被推荐");
        titles.Should().NotContain("A", "已看不应被推荐");
        result[0].Reason.Should().StartWith("同类型");
    }

    [Fact]
    public async Task GetHighRatedUnwatchedAsync_ReturnsUnwatchedRatedAtLeast7()
    {
        var (ctx, svc) = NewService();
        var cat = new Category { Name = "x" }; ctx.Categories.Add(cat); ctx.SaveChanges();
        ctx.Movies.Add(new Movie { Title = "A", WatchStatus = WatchStatus.NotWatched, Rating = 9, CategoryId = cat.Id, PosterData = Poster(), SearchIndex = "a", FilePath = "a.mkv", CreatedAt = DateTime.UtcNow });
        ctx.Movies.Add(new Movie { Title = "B", WatchStatus = WatchStatus.NotWatched, Rating = 7, CategoryId = cat.Id, PosterData = Poster(), SearchIndex = "b", FilePath = "b.mkv", CreatedAt = DateTime.UtcNow });
        ctx.Movies.Add(new Movie { Title = "C", WatchStatus = WatchStatus.NotWatched, Rating = 6, CategoryId = cat.Id, PosterData = Poster(), SearchIndex = "c", FilePath = "c.mkv", CreatedAt = DateTime.UtcNow });
        ctx.Movies.Add(new Movie { Title = "D", WatchStatus = WatchStatus.Watched, Rating = 10, CategoryId = cat.Id, PosterData = Poster(), SearchIndex = "d", FilePath = "d.mkv", CreatedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();

        var result = await svc.GetHighRatedUnwatchedAsync(10);
        var titles = result.Select(r => r.Movie.Title).ToList();
        titles.Should().Contain("A");
        titles.Should().Contain("B");
        titles.Should().NotContain("C", "评分 6 < 7 不应被推荐");
        titles.Should().NotContain("D", "已看不应被推荐");
        result[0].Movie.Title.Should().Be("A", "评分降序");
        result[0].Reason.Should().Be("高分佳片");
    }

    [Fact]
    public async Task OrphanMethods_DoNotLoadFullPosterBlob()
    {
        // 60 部全部带 64KB 海报。若退回 GetAllAsync，扫描阶段会分配 ~3.84MB。
        // 窄投影只算分，仅 topN(10) 部经 MaterializeAsync 加载完整实体（含海报）。
        var (ctx, svc) = NewService();
        var cat = new Category { Name = "x" }; ctx.Categories.Add(cat); ctx.SaveChanges();
        var rng = new Random(7);
        for (int i = 0; i < 60; i++)
        {
            ctx.Movies.Add(new Movie
            {
                Title = $"M{i}",
                Director = $"导演{i % 5}",
                WatchStatus = i % 3 == 0 ? WatchStatus.Watched : WatchStatus.NotWatched,
                IsFavorite = i % 7 == 0,
                CategoryId = cat.Id,
                Rating = rng.Next(10) < 6 ? rng.Next(1, 11) : null,
                PosterData = Poster(),
                SearchIndex = $"m{i}",
                FilePath = $"m{i}.mkv",
                CreatedAt = DateTime.UtcNow.AddDays(-i)
            });
        }
        await ctx.SaveChangesAsync();

        // 预热：先跑一次让 EF 编译查询计划（一次性开销，不计入海报体积），
        // 否则首跑的查询计划编译会污染分配测量。
        await svc.GetBySameDirectorAsync(10);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // 取多次最小值，抵消 GC/测量噪声（与 Statistics/Recommendation 基准同方法论）
        var allocated = long.MaxValue;
        for (int k = 0; k < 3; k++)
        {
            var before = GC.GetTotalAllocatedBytes(true);
            var _ = await svc.GetBySameDirectorAsync(10);
            var after = GC.GetTotalAllocatedBytes(true);
            allocated = Math.Min(allocated, after - before);
        }

        var fullLoadBytes = 60L * PosterBytes;                 // 全量读海报 ≈ 3.75MB
        var maxAllowed = (10L + 5) * PosterBytes + 2_000_000;   // topN 实体 + 余量
        _out.WriteLine($"分配 {allocated / 1024.0 / 1024.0:F2} MB；上限 {maxAllowed / 1024.0 / 1024.0:F2} MB；全量读 {fullLoadBytes / 1024.0 / 1024.0:F2} MB");
        allocated.Should().BeLessThan(maxAllowed, "窄投影不应把全库海报读进内存");
    }
}
