using System.Diagnostics;
using EasyMovie.Core.Enums;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;
using EasyMovie.Core.Services;
using EasyMovie.Data;
using EasyMovie.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace EasyMovie.Tests.Core.Tests;

/// <summary>
/// 推荐服务性能基准 + 契约回归网（永久固化 / 离线可跑）。
///
/// 与统计服务同一套方法论（见 StatisticsBenchmarkTests）：
/// 用真实 SQLite 文件 DB 而非 InMemory —— InMemory 测不出 BLOB 读取成本，
/// 而推荐服务的最大开销正是「为挑 20 部推荐，把全部影片海报读进内存」。
///
/// 三重保障：
///   1. 性能基线：290 → 1000 → 2000 部的耗时 / 托管堆分配曲线
///   2. 契约 Oracle：保留优化前算法作为参考实现，逐项比对 (Id, Reason, Score)
///   3. 复杂度护栏：拦截 O(n²) 回归
///
/// 【必须串行】GC.GetTotalAllocatedBytes 是进程级全局计数器，基准测试类彼此并行会
/// 把其他类的分配计入本类测量值。与 MovieBootstrapTests / StatisticsBenchmarkTests
/// 同属 "Benchmark" Collection。
/// </summary>
[Collection("Benchmark")]
public class RecommendationBenchmarkTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly List<string> _tempFiles = new();

    // 实测自 EasyMovie.db：288 张海报 / 290 部，平均 86.4 KB
    private const int AvgPosterBytes = 86 * 1024;

    public RecommendationBenchmarkTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        foreach (var f in _tempFiles.SelectMany(p => new[] { p + "-wal", p + "-shm" }))
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        GC.SuppressFinalize(this);
    }

    private string NewTempDb()
    {
        var dir = Path.Combine(Path.GetTempPath(), "EasyMovieReco");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"reco_{Guid.NewGuid():N}.db");
        _tempFiles.Add(path);
        return path;
    }

    private static MovieDbContext CreateContext(string path)
    {
        var options = new DbContextOptionsBuilder<MovieDbContext>()
            .UseSqlite($"Data Source={path}").Options;
        return new MovieDbContext(options);
    }

    /// <summary>合成与生产同构的库：海报 BLOB、多值人名/国家、分类与标签关联、已看记录。</summary>
    private static void Seed(string path, int movieCount)
    {
        using var ctx = CreateContext(path);
        ctx.Database.EnsureCreated();
        ctx.ChangeTracker.AutoDetectChangesEnabled = false;

        var rng = new Random(42); // 固定种子 → 可复现

        var categories = new[] { "科幻", "剧情", "动作", "喜剧", "悬疑", "动画", "纪录片" }
            .Select(n => new Category { Name = n }).ToList();
        ctx.Categories.AddRange(categories);

        var tags = new[] { "动作", "喜剧", "剧情", "科幻", "恐怖", "爱情", "悬疑", "动画" }
            .Select(n => new Tag { Name = n, Color = "#7C4DFF" }).ToList();
        ctx.Tags.AddRange(tags);
        ctx.SaveChanges();

        var directors = Enumerable.Range(0, 120).Select(i => $"导演{i}").ToArray();
        var countries = new[] { "美国", "中国", "日本", "韩国", "法国", "英国", "印度", "德国" };

        var poster = new byte[AvgPosterBytes];
        rng.NextBytes(poster);

        var movies = new List<Movie>(movieCount);
        for (int i = 0; i < movieCount; i++)
        {
            movies.Add(new Movie
            {
                Title = $"影片{i}",
                Year = 1950 + rng.Next(77),
                Director = directors[rng.Next(directors.Length)],
                Cast = string.Join(", ", Enumerable.Range(0, 3).Select(_ => $"演员{rng.Next(300)}")),
                Country = countries[rng.Next(countries.Length)],
                Runtime = 60 + rng.Next(120),
                Rating = rng.Next(10) < 6 ? rng.Next(1, 11) : null,
                // 约 1/3 已看 —— 提供推荐算法所需的偏好依据
                WatchStatus = rng.Next(3) == 0 ? WatchStatus.Watched : WatchStatus.NotWatched,
                IsFavorite = rng.Next(6) == 0,
                CategoryId = categories[rng.Next(categories.Count)].Id,
                PosterData = poster,
                Synopsis = new string('简', 200),
                SearchIndex = $"yingpian{i}",
                FilePath = $@"D:\Media\film{i}.mkv",
                CreatedAt = DateTime.UtcNow.AddDays(-i)
            });
        }

        for (int i = 0; i < movies.Count; i += 500)
        {
            ctx.Movies.AddRange(movies.Skip(i).Take(500));
            ctx.SaveChanges();
        }

        // 标签关联：每部片 0~2 个标签
        var ids = ctx.Movies.Select(m => m.Id).ToList();
        var movieTags = new List<MovieTag>();
        foreach (var id in ids)
            foreach (var t in tags.OrderBy(_ => rng.Next()).Take(rng.Next(3)))
                movieTags.Add(new MovieTag { MovieId = id, TagId = t.Id });
        ctx.MovieTags.AddRange(movieTags);
        ctx.SaveChanges();
    }

    private sealed record BenchResult(long ElapsedMs, long AllocatedBytes);

    private static async Task<BenchResult> MeasureAsync(Func<Task> action, int runs = 3)
    {
        await action();
        var times = new List<long>();
        var allocs = new List<long>();
        for (int i = 0; i < runs; i++)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var sw = Stopwatch.StartNew();
            await action();
            sw.Stop();
            var after = GC.GetTotalAllocatedBytes(precise: true);
            times.Add(sw.ElapsedMilliseconds);
            allocs.Add(after - before);
        }
        times.Sort();
        allocs.Sort();
        return new BenchResult(times[runs / 2], allocs[runs / 2]);
    }

    [Theory]
    [InlineData(290)]
    [InlineData(1000)]
    [InlineData(2000)]
    public async Task Benchmark_GetRecommendationsAsync(int movieCount)
    {
        var path = NewTempDb();
        Seed(path, movieCount);

        // 必须每次新建 DbContext：复用会命中 identity resolution 跳过物化，严重低估成本
        var cold = await MeasureAsync(async () =>
        {
            using var ctx = CreateContext(path);
            var repo = new MovieRepository(ctx);
            await new RecommendationService(repo).GetRecommendationsAsync(20);
        });

        using var warmCtx = CreateContext(path);
        var warmSvc = new RecommendationService(new MovieRepository(warmCtx));
        var warm = await MeasureAsync(() => warmSvc.GetRecommendationsAsync(20));

        _out.WriteLine($"=== GetRecommendationsAsync(20) @ {movieCount} 部 ===");
        _out.WriteLine($"  [冷] 新 DbContext : {cold.ElapsedMs,7} ms / {cold.AllocatedBytes / 1024.0 / 1024,7:F2} MB");
        _out.WriteLine($"  [热] 复用 Context : {warm.ElapsedMs,7} ms / {warm.AllocatedBytes / 1024.0 / 1024,7:F2} MB");
        _out.WriteLine($"  每部均摊(冷)      : {(double)cold.AllocatedBytes / movieCount / 1024,7:F1} KB/部");

        var budgetMs = 3000 + movieCount * 3;
        Assert.True(cold.ElapsedMs < budgetMs,
            $"推荐耗时 {cold.ElapsedMs}ms 超出预算 {budgetMs}ms（{movieCount} 部）——疑似复杂度退化");
    }

    /// <summary>契约 Oracle：优化后必须与优化前算法给出相同的推荐结果（顺序、理由、分数）。</summary>
    [Theory]
    [InlineData(80)]
    [InlineData(400)]
    public async Task Contract_OptimizedMatchesReference(int movieCount)
    {
        var path = NewTempDb();
        Seed(path, movieCount);

        using var ctx = CreateContext(path);
        var repo = new MovieRepository(ctx);

        // 参考实现：优化前的算法（全量实体 + 原评分逻辑）
        var all = await repo.GetAllAsync();
        var expected = ReferenceRecommendation.Calculate(all, 20);

        // 被测实现
        var actual = await new RecommendationService(repo).GetRecommendationsAsync(20);

        var e = string.Join(" || ", expected.Select(r =>
            $"{r.Movie.Id}|{r.Reason}|{r.Score:F1}"));
        var a = string.Join(" || ", actual.Select(r =>
            $"{r.Movie.Id}|{r.Reason}|{r.Score:F1}"));

        _out.WriteLine($"  期望({expected.Count}): {e[..Math.Min(300, e.Length)]}");
        _out.WriteLine($"  实际({actual.Count}): {a[..Math.Min(300, a.Length)]}");

        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.True(expected[i].Movie.Id == actual[i].Movie.Id,
                $"第 {i} 项影片不同：期望 Id={expected[i].Movie.Id} 实际 Id={actual[i].Movie.Id}");
            Assert.True(Math.Abs(expected[i].Score - actual[i].Score) < 0.05,
                $"第 {i} 项分数不同：期望 {expected[i].Score} 实际 {actual[i].Score}");
            Assert.Equal(expected[i].Reason, actual[i].Reason);
        }
    }
}

/// <summary>
/// 参考实现（Oracle）：完整保留 RecommendationService 优化前的算法语义。
/// 优化后必须与本实现逐项一致；若语义有意变更，应先改本 Oracle 并说明理由，而不是放宽断言。
/// </summary>
public static class ReferenceRecommendation
{
    public static List<RecommendedMovie> Calculate(List<Movie> allMovies, int topN)
    {
        if (allMovies.Count == 0) return new List<RecommendedMovie>();

        var scored = new Dictionary<int, (double score, List<string> reasons)>();

        var watched = allMovies.Where(m =>
            m.WatchStatus == WatchStatus.Watched || m.Rating.HasValue || m.IsFavorite).ToList();
        var candidates = allMovies.Where(m => m.WatchStatus != WatchStatus.Watched).ToList();

        if (watched.Count == 0)
        {
            return allMovies
                .OrderByDescending(m => m.Rating ?? 0)
                .ThenByDescending(m => m.Year)
                .Take(topN)
                .Select(m => new RecommendedMovie
                {
                    Movie = m,
                    Reason = m.Rating >= 7 ? "高分佳片" : (m.Year >= DateTime.UtcNow.Year - 1 ? "近期热门" : "猜你喜欢"),
                    Score = (m.Rating ?? 5) + (m.Year >= DateTime.UtcNow.Year - 1 ? 1 : 0)
                })
                .ToList();
        }

        var watchedDirectors = watched
            .Where(m => !string.IsNullOrWhiteSpace(m.Director))
            .SelectMany(m => m.Director!.Split('/', ','))
            .Select(d => d.Trim()).Where(d => !string.IsNullOrEmpty(d)).ToHashSet();

        foreach (var movie in candidates)
        {
            if (string.IsNullOrWhiteSpace(movie.Director)) continue;
            var directors = movie.Director.Split('/', ',').Select(d => d.Trim()).Where(d => !string.IsNullOrEmpty(d));
            foreach (var director in directors)
            {
                if (watchedDirectors.Contains(director))
                {
                    if (!scored.ContainsKey(movie.Id)) scored[movie.Id] = (0, new List<string>());
                    var entry = scored[movie.Id];
                    entry.score += 3.0;
                    if (!entry.reasons.Any(r => r.Contains(director))) entry.reasons.Add($"同导演: {director}");
                    scored[movie.Id] = entry;
                }
            }
        }

        var watchedCategoryIds = watched.Where(m => m.CategoryId.HasValue)
            .Select(m => m.CategoryId!.Value).ToHashSet();

        foreach (var movie in candidates)
        {
            if (!movie.CategoryId.HasValue || !watchedCategoryIds.Contains(movie.CategoryId.Value)) continue;
            if (!scored.ContainsKey(movie.Id)) scored[movie.Id] = (0, new List<string>());
            var entry = scored[movie.Id];
            entry.score += 2.0;
            var catName = movie.Category?.Name ?? "同类型";
            if (!entry.reasons.Any(r => r.Contains(catName))) entry.reasons.Add($"同类型: {catName}");
            scored[movie.Id] = entry;
        }

        var watchedCountries = watched
            .Where(m => !string.IsNullOrWhiteSpace(m.Country))
            .SelectMany(m => m.Country!.Split('/', ' ', '·', ','))
            .Select(c => c.Trim()).Where(c => !string.IsNullOrEmpty(c)).ToHashSet();

        foreach (var movie in candidates)
        {
            if (string.IsNullOrWhiteSpace(movie.Country)) continue;
            var countries = movie.Country.Split('/', ' ', '·', ',').Select(c => c.Trim()).Where(c => !string.IsNullOrEmpty(c));
            var matchCountries = countries.Where(c => watchedCountries.Contains(c)).ToList();
            if (matchCountries.Count == 0) continue;
            if (!scored.ContainsKey(movie.Id)) scored[movie.Id] = (0, new List<string>());
            var entry = scored[movie.Id];
            entry.score += 1.5;
            var cName = matchCountries.First();
            if (!entry.reasons.Any(r => r.Contains(cName))) entry.reasons.Add($"同地区: {cName}");
            scored[movie.Id] = entry;
        }

        var watchedTagIds = watched.Where(m => m.MovieTags != null)
            .SelectMany(m => m.MovieTags.Select(mt => mt.TagId)).ToHashSet();

        foreach (var movie in candidates)
        {
            if (movie.MovieTags == null) continue;
            var matchTags = movie.MovieTags.Where(mt => watchedTagIds.Contains(mt.TagId)).ToList();
            if (matchTags.Count == 0) continue;
            if (!scored.ContainsKey(movie.Id)) scored[movie.Id] = (0, new List<string>());
            var entry = scored[movie.Id];
            entry.score += matchTags.Count * 1.5;
            var tagNames = matchTags.Select(mt => mt.Tag?.Name).Where(n => n != null).Take(3);
            foreach (var tn in tagNames)
                if (!entry.reasons.Any(r => r.Contains(tn!))) entry.reasons.Add($"同标签: {tn}");
            scored[movie.Id] = entry;
        }

        foreach (var movie in candidates)
        {
            if (!movie.Rating.HasValue) continue;
            if (!scored.ContainsKey(movie.Id)) scored[movie.Id] = (0, new List<string>());
            var entry = scored[movie.Id];
            var ratingBonus = (movie.Rating.Value - 5.0) * 0.5;
            if (ratingBonus > 0) { entry.score += ratingBonus; scored[movie.Id] = entry; }
        }

        var favoriteDirectors = watched
            .Where(m => m.IsFavorite && !string.IsNullOrWhiteSpace(m.Director))
            .SelectMany(m => m.Director!.Split('/', ','))
            .Select(d => d.Trim()).Where(d => !string.IsNullOrEmpty(d)).ToHashSet();

        var favoriteCategoryIds = watched
            .Where(m => m.IsFavorite && m.CategoryId.HasValue)
            .Select(m => m.CategoryId!.Value).ToHashSet();

        foreach (var kvp in scored.ToList())
        {
            var movie = candidates.FirstOrDefault(m => m.Id == kvp.Key);
            if (movie == null) continue;
            var bonus = 0.0;
            if (!string.IsNullOrWhiteSpace(movie.Director))
            {
                var dirs = movie.Director.Split('/', ',').Select(d => d.Trim());
                if (dirs.Any(d => favoriteDirectors.Contains(d))) bonus += 2.0;
            }
            if (movie.CategoryId.HasValue && favoriteCategoryIds.Contains(movie.CategoryId.Value)) bonus += 1.5;
            if (bonus > 0)
            {
                var entry = kvp.Value;
                entry.score += bonus;
                scored[kvp.Key] = entry;
            }
        }

        var result = scored
            .Select(kvp =>
            {
                var movie = candidates.FirstOrDefault(m => m.Id == kvp.Key);
                return new RecommendedMovie
                {
                    Movie = movie!,
                    Reason = string.Join(" | ", kvp.Value.reasons.Take(2)),
                    Score = Math.Round(kvp.Value.score, 1)
                };
            })
            .Where(r => r.Movie != null)
            .OrderByDescending(r => r.Score)
            .Take(topN)
            .ToList();

        if (result.Count < topN)
        {
            var existingIds = result.Select(r => r.Movie.Id).ToHashSet();
            var fillers = candidates
                .Where(m => !existingIds.Contains(m.Id) && m.Rating.HasValue && m.Rating >= 6)
                .OrderByDescending(m => m.Rating)
                .Take(topN - result.Count)
                .Select(m => new RecommendedMovie { Movie = m, Reason = "高分佳片", Score = m.Rating ?? 0 });
            result.AddRange(fillers);
        }

        if (result.Count < topN)
        {
            var existingIds = result.Select(r => r.Movie.Id).ToHashSet();
            var yearFillers = allMovies
                .Where(m => !existingIds.Contains(m.Id) && m.Year > 0)
                .OrderByDescending(m => m.Year)
                .Take(topN - result.Count)
                .Select(m => new RecommendedMovie { Movie = m, Reason = "近期新片", Score = 0 });
            result.AddRange(yearFillers);
        }

        return result;
    }
}
