using System.Diagnostics;
using EasyMovie.Core.Enums;
using EasyMovie.Core.Models;
using EasyMovie.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace EasyMovie.Tests.Core.Tests;

/// <summary>
/// AiLibrarySummaryService 契约回归网 + 性能基准。
///
/// 背景：原 PreBuildSystemPromptAsync 第一句就是全量加载
/// （Include(Category) + Include(MovieTags).ThenInclude(Tag)），把全库海报读进内存，
/// 而 prompt 里一个字节的海报都不需要 —— 只用到 4 个计数和几个 Top10。
///
/// 两重保障：
///   1. 契约 Oracle：保留优化前的全量实现，与新实现逐字段比对（含 Top10 的**顺序**）
///   2. 性能基线 + 护栏：分配量必须降到全量加载的 1/10 以下
///
/// 与所有基准类同属 "Benchmark" Collection（GC.GetTotalAllocatedBytes 是全局计数器）。
/// </summary>
[Collection("Benchmark")]
public class AiLibrarySummaryTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly List<string> _tempFiles = new();

    private const int AvgPosterBytes = 86 * 1024;

    public AiLibrarySummaryTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        foreach (var f in _tempFiles.SelectMany(p => new[] { p, p + "-wal", p + "-shm" }))
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { /* 清理失败不影响测试结果 */ }
        }
        GC.SuppressFinalize(this);
    }

    private string NewTempDb()
    {
        var dir = Path.Combine(Path.GetTempPath(), "EasyMovieAiBench");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"ai_{Guid.NewGuid():N}.db");
        _tempFiles.Add(path);
        return path;
    }

    private static MovieDbContext CreateContext(string path)
    {
        var options = new DbContextOptionsBuilder<MovieDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        return new MovieDbContext(options);
    }

    /// <summary>
    /// 种子覆盖：三种观看状态、部分无评分、收藏标记、分类、多值导演（'/' 与 ',' 两种分隔）、
    /// 标签多对多、海报 BLOB。标签关联按 (MovieId, TagId) 升序插入，使并列排序可复现。
    /// </summary>
    private static void Seed(string path, int movieCount)
    {
        using var ctx = CreateContext(path);
        ctx.Database.EnsureCreated();
        ctx.ChangeTracker.AutoDetectChangesEnabled = false;

        var rng = new Random(42);

        var cats = new[] { "科幻", "剧情", "动作", "喜剧", "悬疑" }
            .Select(n => new Category { Name = n, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow })
            .ToList();
        ctx.Categories.AddRange(cats);

        var tags = new[] { "动作", "喜剧", "剧情", "科幻", "恐怖", "爱情", "悬疑" }
            .Select(n => new Tag { Name = n, Color = "#7C4DFF" })
            .ToList();
        ctx.Tags.AddRange(tags);
        ctx.SaveChanges();

        var catIds = ctx.Categories.AsNoTracking().Select(c => c.Id).ToList();
        var tagIds = ctx.Tags.AsNoTracking().Select(t => t.Id).ToList();

        var directors = new[] { "诺兰", "张艺谋", "是枝裕和", "朴赞郁", "斯皮尔伯格" };
        var poster = new byte[AvgPosterBytes];
        rng.NextBytes(poster);

        var movies = new List<Movie>(movieCount);
        var movieTags = new List<MovieTag>();

        for (int i = 0; i < movieCount; i++)
        {
            var status = (i % 3) switch
            {
                0 => WatchStatus.Watched,
                1 => WatchStatus.WantToWatch,
                _ => WatchStatus.NotWatched
            };

            // 多值导演：交替使用 '/' 与 ',' 两种分隔符（原实现 Split('/', ',')）
            var director = i % 4 == 0
                ? $"{directors[i % directors.Length]}/{directors[(i + 1) % directors.Length]}"
                : directors[i % directors.Length];

            movies.Add(new Movie
            {
                Title = $"影片{i}",
                Year = 1990 + (i % 35),
                Director = director,
                CategoryId = i % 7 == 0 ? null : catIds[i % catIds.Count],
                WatchStatus = status,
                Rating = i % 5 == 0 ? null : 1 + (i % 10),   // 部分无评分
                IsFavorite = i % 3 == 0,
                PosterData = poster,
                CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i),
                UpdatedAt = DateTime.UtcNow
            });
        }

        ctx.Movies.AddRange(movies);
        ctx.SaveChanges();

        var movieIds = ctx.Movies.AsNoTracking().OrderBy(m => m.Id).Select(m => m.Id).ToList();
        // 按 (MovieId, TagId) 升序插入 —— 与原实现「按电影遍历 → SelectMany 其标签」的顺序一致
        for (int i = 0; i < movieIds.Count; i++)
        {
            int tagCount = 1 + (i % 3);
            for (int k = 0; k < tagCount; k++)
                movieTags.Add(new MovieTag { MovieId = movieIds[i], TagId = tagIds[(i + k) % tagIds.Count] });
        }

        ctx.MovieTags.AddRange(movieTags);
        ctx.SaveChanges();
    }

    /// <summary>
    /// 优化前的实现，逐行复刻自 AIRecommendationView.PreBuildSystemPromptAsync。
    /// 这是判定新实现是否等价的唯一标准，不得随新实现一起改动。
    /// </summary>
    private static async Task<AiLibrarySummary> LegacyBuildAsync(string path)
    {
        using var ctx = CreateContext(path);

        var movies = await ctx.Movies
            .Include(m => m.Category)
            .Include(m => m.MovieTags).ThenInclude(mt => mt.Tag)
            .ToListAsync();

        var total = movies.Count;
        var watched = movies.Count(m => m.WatchStatus == WatchStatus.Watched);
        var wantToWatch = movies.Count(m => m.WatchStatus == WatchStatus.WantToWatch);
        var favorites = movies.Count(m => m.IsFavorite);

        var categories = movies.Where(m => m.Category != null)
            .GroupBy(m => m.Category!.Name)
            .OrderByDescending(g => g.Count()).Take(10)
            .Select(g => $"{g.Key}({g.Count()}部)")
            .ToList();

        var topDirectors = movies.Where(m => !string.IsNullOrEmpty(m.Director))
            .SelectMany(m => m.Director!.Split('/', ',').Select(d => d.Trim()))
            .GroupBy(d => d)
            .OrderByDescending(g => g.Count()).Take(10)
            .Select(g => $"{g.Key}({g.Count()}部)")
            .ToList();

        var tags = movies.SelectMany(m => m.MovieTags.Select(mt => mt.Tag?.Name))
            .Where(n => n != null)
            .GroupBy(n => n)
            .OrderByDescending(g => g.Count()).Take(10)
            .Select(g => $"{g.Key}({g.Count()}部)")
            .ToList();

        var watchedMovies = movies
            .Where(m => m.WatchStatus == WatchStatus.Watched && m.Rating.HasValue)
            .OrderByDescending(m => m.Rating).Take(15)
            .Select(m => $"- {m.Title} ({m.Year}) ⭐{m.Rating} | {m.Director?.Split('/').FirstOrDefault() ?? ""} | {m.Category?.Name ?? ""}")
            .ToList();

        var wantWatchList = movies.Where(m => m.WatchStatus == WatchStatus.WantToWatch)
            .Take(20)
            .Select(m => $"- {m.Title} ({m.Year}) | {m.Category?.Name ?? ""}")
            .ToList();

        var unwatched = movies
            .Where(m => m.WatchStatus == WatchStatus.NotWatched && m.Rating.HasValue)
            .OrderByDescending(m => m.Rating).Take(20)
            .Select(m => $"- {m.Title} ({m.Year}) ⭐{m.Rating} | {m.Director?.Split('/').FirstOrDefault() ?? ""} | {m.Category?.Name ?? ""}")
            .ToList();

        return new AiLibrarySummary
        {
            Total = total,
            Watched = watched,
            WantToWatch = wantToWatch,
            Favorites = favorites,
            Categories = categories,
            TopDirectors = topDirectors,
            Tags = tags,
            WatchedTop = watchedMovies,
            WantWatchList = wantWatchList,
            UnwatchedHighRated = unwatched
        };
    }

    private static void AssertEquivalent(AiLibrarySummary expected, AiLibrarySummary actual)
    {
        Assert.Equal(expected.Total, actual.Total);
        Assert.Equal(expected.Watched, actual.Watched);
        Assert.Equal(expected.WantToWatch, actual.WantToWatch);
        Assert.Equal(expected.Favorites, actual.Favorites);

        // Top10 / Top15 / Top20 逐项比对（含顺序 —— 顺序即语义）
        Assert.Equal(expected.Categories, actual.Categories);
        Assert.Equal(expected.TopDirectors, actual.TopDirectors);
        Assert.Equal(expected.Tags, actual.Tags);
        Assert.Equal(expected.WatchedTop, actual.WatchedTop);
        Assert.Equal(expected.WantWatchList, actual.WantWatchList);
        Assert.Equal(expected.UnwatchedHighRated, actual.UnwatchedHighRated);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(290)]
    public async Task Oracle_MatchesLegacyImplementation(int movieCount)
    {
        var legacyDb = NewTempDb();
        var newDb = NewTempDb();
        Seed(legacyDb, movieCount);
        Seed(newDb, movieCount);

        var expected = await LegacyBuildAsync(legacyDb);

        using var ctx = CreateContext(newDb);
        var actual = await new AiLibrarySummaryService(ctx).BuildAsync();

        AssertEquivalent(expected, actual);
    }

    [Fact]
    public async Task Oracle_SeedActuallyCoversAllBranches()
    {
        // 护栏：确认种子真的覆盖了各分支，否则上面的 Oracle 是在测空气
        var db = NewTempDb();
        Seed(db, 30);

        using var ctx = CreateContext(db);
        var summary = await new AiLibrarySummaryService(ctx).BuildAsync();

        Assert.True(summary.Watched > 0, "应存在已看影片");
        Assert.True(summary.WantToWatch > 0, "应存在想看影片");
        Assert.True(summary.Favorites > 0, "应存在收藏影片");
        Assert.NotEmpty(summary.Categories);
        Assert.NotEmpty(summary.Tags);
        Assert.NotEmpty(summary.WatchedTop);
        Assert.NotEmpty(summary.WantWatchList);
        Assert.NotEmpty(summary.UnwatchedHighRated);
        // 多值导演被拆分后应出现独立导演名
        Assert.Contains(summary.TopDirectors, d => d.StartsWith("诺兰("));
    }

    private readonly record struct BenchResult(long Ms, long Bytes);

    private static async Task<BenchResult> MeasureAsync(Func<Task> action, int runs = 5)
    {
        await action(); // warmup
        long bestMs = long.MaxValue, bestBytes = long.MaxValue;
        for (int i = 0; i < runs; i++)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var sw = Stopwatch.StartNew();
            await action();
            sw.Stop();
            var after = GC.GetTotalAllocatedBytes(precise: true);
            if (sw.ElapsedMilliseconds < bestMs) bestMs = sw.ElapsedMilliseconds;
            var allocated = after - before;
            if (allocated < bestBytes) bestBytes = allocated;
        }
        return new BenchResult(bestMs, bestBytes);
    }

    [Theory]
    [InlineData(290)]
    [InlineData(2000)]
    public async Task Benchmark_SummaryVsFullLoad(int movieCount)
    {
        var db = NewTempDb();
        Seed(db, movieCount);

        var legacy = await MeasureAsync(async () =>
        {
            using var ctx = CreateContext(db);   // 每次新建 context：复用会因 identity resolution 低估成本
            await ctx.Movies
                .Include(m => m.Category)
                .Include(m => m.MovieTags).ThenInclude(mt => mt.Tag)
                .ToListAsync();
        });

        var modern = await MeasureAsync(async () =>
        {
            using var ctx = CreateContext(db);
            await new AiLibrarySummaryService(ctx).BuildAsync();
        });

        _out.WriteLine($"[{movieCount} 部] 全量加载 {legacy.Ms,5} ms / {legacy.Bytes / 1024.0 / 1024,8:F2} MB   " +
                       $"概况 {modern.Ms,5} ms / {modern.Bytes / 1024.0 / 1024,8:F2} MB   " +
                       $"内存降至 {modern.Bytes / (double)legacy.Bytes:P2}（{legacy.Bytes / (double)Math.Max(modern.Bytes, 1):F1}×）");

        Assert.True(modern.Bytes < legacy.Bytes / 10,
            $"概况分配量 {modern.Bytes / 1024.0 / 1024:F2} MB 未降到全量加载 " +
            $"{legacy.Bytes / 1024.0 / 1024:F2} MB 的 1/10 以下");
    }
}
