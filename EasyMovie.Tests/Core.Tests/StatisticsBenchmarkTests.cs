using System.Diagnostics;
using EasyMovie.Core.Enums;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;
using EasyMovie.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace EasyMovie.Tests.Core.Tests;

/// <summary>
/// 统计服务性能基准 + 契约回归网（永久固化，离线可跑）。
///
/// 为什么必须用真实 SQLite 而不是 InMemory：
///   InMemory provider 不产生真实的 BLOB 读取成本（无磁盘/序列化/大对象堆分配），
///   会完全掩盖 StatisticsService 的主要开销。实测 EasyMovie.db 中 PosterData
///   占全库 99.4%（24.29 MB / 27.93 MB），这部分成本只有真实文件 DB 能复现。
///
/// 三重保障：
///   1. 性能基线：产出可复现的耗时 / 托管堆分配数字（290 → 1000 → 2000 部劣化曲线）
///   2. 契约 Oracle：保留优化前的全量内存实现作为参考实现，与新实现逐字段比对
///   3. 复杂度护栏：2000 部规模下耗时设上限，防止 O(n²) 实现回归
/// </summary>
public class StatisticsBenchmarkTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly List<string> _tempFiles = new();

    // 实测自 C:\Users\10638\AppData\Local\EasyMovie\EasyMovie.db：
    // 288 张海报 / 290 部电影，平均 86.4 KB，最大 168.9 KB，合计 24.29 MB（占库 99.4%）
    private const int AvgPosterBytes = 86 * 1024;

    public StatisticsBenchmarkTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { /* 清理失败不影响测试结果 */ }
        }
        // SQLite 伴随文件
        foreach (var f in _tempFiles.SelectMany(p => new[] { p + "-wal", p + "-shm" }))
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        }
        GC.SuppressFinalize(this);
    }

    private string NewTempDb()
    {
        var dir = Path.Combine(Path.GetTempPath(), "EasyMovieBench");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"bench_{Guid.NewGuid():N}.db");
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

    /// <summary>合成与生产环境同构的媒体库：海报 BLOB、多值人名/国家字段、观影记录。</summary>
    private static void Seed(string path, int movieCount, int watchLogCount)
    {
        using var ctx = CreateContext(path);
        ctx.Database.EnsureCreated();
        ctx.ChangeTracker.AutoDetectChangesEnabled = false;

        var rng = new Random(42); // 固定种子 → 结果可复现

        var categories = new[] { "科幻", "剧情", "动作", "喜剧", "悬疑", "动画", "纪录片" }
            .Select(n => new Category { Name = n }).ToList();
        ctx.Categories.AddRange(categories);

        var tags = new[] { "动作", "喜剧", "剧情", "科幻", "恐怖", "爱情", "悬疑", "动画" }
            .Select(n => new Tag { Name = n, Color = "#7C4DFF" }).ToList();
        ctx.Tags.AddRange(tags);
        ctx.SaveChanges();

        // 多值字段规模决定 Director/Cast 的 GroupBy 基数（原实现 AvgRating 为 O(n·g)）
        var directors = Enumerable.Range(0, 200).Select(i => $"导演{i}").ToArray();
        var casts = Enumerable.Range(0, 300).Select(i => $"演员{i}").ToArray();
        var countries = new[] { "美国", "中国", "日本", "韩国", "法国", "英国", "印度", "德国" };

        // 全库共享同一份海报字节：DB 文件体积与内存成本等价，但种子生成更快
        var poster = new byte[AvgPosterBytes];
        rng.NextBytes(poster);

        var movies = new List<Movie>(movieCount);
        for (int i = 0; i < movieCount; i++)
        {
            var d1 = directors[rng.Next(directors.Length)];
            var director = rng.Next(4) == 0 ? $"{d1}、{directors[rng.Next(directors.Length)]}" : d1;
            var cast = string.Join(", ",
                Enumerable.Range(0, 3 + rng.Next(3)).Select(_ => casts[rng.Next(casts.Length)]).Distinct());
            var country = rng.Next(3) == 0
                ? $"{countries[rng.Next(countries.Length)]} / {countries[rng.Next(countries.Length)]}"
                : countries[rng.Next(countries.Length)];

            movies.Add(new Movie
            {
                Title = $"影片{i}",
                Year = 1950 + rng.Next(77),
                Director = director,
                Cast = cast,
                Country = country,
                Runtime = 60 + rng.Next(120),
                Rating = rng.Next(10) < 6 ? rng.Next(1, 11) : null, // 60% 有评分
                WatchStatus = (WatchStatus)rng.Next(0, 3),
                IsFavorite = rng.Next(5) == 0,
                CategoryId = categories[rng.Next(categories.Count)].Id,
                PosterData = poster,
                Synopsis = new string('简', 200),
                SearchIndex = $"yingpian{i}",
                FilePath = $@"D:\Media\film{i}.mkv"
            });
        }

        // 分批提交，避免变更跟踪器持有过多实体拖慢种子阶段
        for (int i = 0; i < movies.Count; i += 500)
        {
            ctx.Movies.AddRange(movies.Skip(i).Take(500));
            ctx.SaveChanges();
        }

        // 观影记录：跨两个自然年 + 连续日期段，用于覆盖 DayOfWeek / Streak / 今年统计
        var movieIds = ctx.Movies.Select(m => m.Id).ToList();
        if (movieIds.Count > 0 && watchLogCount > 0)
        {
            var logs = new List<WatchLog>();
            var baseDate = new DateTime(DateTime.Now.Year, 1, 5);
            for (int i = 0; i < watchLogCount; i++)
            {
                logs.Add(new WatchLog
                {
                    MovieId = movieIds[rng.Next(movieIds.Count)],
                    WatchDate = baseDate.AddDays(i % 60), // 制造连续段 + 断档
                    CreatedAt = DateTime.UtcNow
                });
            }
            ctx.WatchLogs.AddRange(logs);
            ctx.SaveChanges();
        }

        // 类型标签关联
        if (movieIds.Count > 0)
        {
            var movieTags = new List<MovieTag>();
            foreach (var id in movieIds)
            {
                foreach (var t in tags.OrderBy(_ => rng.Next()).Take(1 + rng.Next(2)))
                    movieTags.Add(new MovieTag { MovieId = id, TagId = t.Id });
            }
            ctx.MovieTags.AddRange(movieTags);
            ctx.SaveChanges();
        }
    }

    private sealed record BenchResult(long ElapsedMs, long AllocatedBytes);

    /// <summary>预热 1 次（JIT + SQLite 页缓存）后取 N 次的中位数，抑制单次抖动。</summary>
    private static async Task<BenchResult> MeasureActionAsync(Func<Task> action, int runs = 3)
    {
        await action(); // warmup
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

    private static Task<BenchResult> MeasureAsync(Func<Task<StatisticsData>> action, int runs = 3)
        => MeasureActionAsync(async () => await action(), runs);

    /// <summary>
    /// 诊断：定位成本到底来自「读 BLOB」还是「CPU 聚合」。
    /// 优化前必须先搞清楚这一点——否则会把力气花在错误的地方。
    /// </summary>
    [Fact]
    public async Task Diagnose_CostBreakdown()
    {
        const int n = 1000;
        var path = NewTempDb();
        Seed(path, n, 50);

        var options = new DbContextOptionsBuilder<MovieDbContext>()
            .UseSqlite($"Data Source={path}").Options;
        using var ctx = new MovieDbContext(options);

        // 先确认：全实体查询到底有没有把 PosterData 读进内存
        var probe = await ctx.Movies.AsNoTracking().FirstAsync();
        _out.WriteLine($"【事实核查】PosterData 是否被物化: {(probe.PosterData is null ? "否(NULL)" : $"是, 长度 {probe.PosterData.Length / 1024} KB")}");

        // A: 全实体（当前实现所用）
        var full = await MeasureActionAsync(() => ctx.Movies.AsNoTracking().ToListAsync());
        // B: 仅主键
        var idOnly = await MeasureActionAsync(() => ctx.Movies.AsNoTracking().Select(m => m.Id).ToListAsync());
        // C: 统计真正需要的窄投影（不含 PosterData / Synopsis）
        var narrow = await MeasureActionAsync(() => ctx.Movies.AsNoTracking()
            .Select(m => new { m.Id, m.Year, m.Rating, m.Runtime, m.Director, m.Cast, m.Country,
                               m.WatchStatus, m.IsFavorite, m.WatchDate, m.CategoryId })
            .ToListAsync());

        _out.WriteLine($"=== 成本拆解 @ {n} 部（含 {AvgPosterBytes / 1024} KB/部 海报 BLOB） ===");
        _out.WriteLine($"  A 全实体 ToListAsync   : {full.ElapsedMs,6} ms  {full.AllocatedBytes / 1024.0 / 1024,7:F2} MB");
        _out.WriteLine($"  B 仅主键投影           : {idOnly.ElapsedMs,6} ms  {idOnly.AllocatedBytes / 1024.0 / 1024,7:F2} MB");
        _out.WriteLine($"  C 统计窄投影(无BLOB)   : {narrow.ElapsedMs,6} ms  {narrow.AllocatedBytes / 1024.0 / 1024,7:F2} MB");
        _out.WriteLine($"  → 读 BLOB 的额外代价   : {full.AllocatedBytes / 1024.0 / 1024 - narrow.AllocatedBytes / 1024.0 / 1024:F2} MB " +
                       $"({(full.AllocatedBytes - narrow.AllocatedBytes) / 1024.0 / n:F1} KB/部)");
    }

    [Theory]
    [InlineData(290, 13)]    // 当前真实库规模
    [InlineData(1000, 50)]
    [InlineData(2000, 100)]  // 劣化外推
    public async Task Benchmark_GetStatisticsAsync(int movieCount, int watchLogCount)
    {
        var path = NewTempDb();
        Seed(path, movieCount, watchLogCount);

        // 关键：每次测量都用全新 DbContext。
        // 复用同一 DbContext 会命中 ChangeTracker 的 identity resolution——实体已被跟踪时
        // EF 跳过物化、byte[] 不再重新分配，会把成本低估约 12 倍（实测 6.79 MB vs 85 MB）。
        // 生产环境 StatisticsView 长期持有 _context，因此两种场景都要测。
        var cold = await MeasureActionAsync(async () =>
        {
            var opt = new DbContextOptionsBuilder<MovieDbContext>().UseSqlite($"Data Source={path}").Options;
            using var ctx = new MovieDbContext(opt);
            await new StatisticsService(ctx).GetStatisticsAsync();
        });

        // 对照：复用同一 DbContext 的「再次切换到统计页」成本（剥离 IO，只剩 CPU 聚合）
        var warmOpt = new DbContextOptionsBuilder<MovieDbContext>().UseSqlite($"Data Source={path}").Options;
        using var warmCtx = new MovieDbContext(warmOpt);
        var warmService = new StatisticsService(warmCtx);
        var warm = await MeasureAsync(() => warmService.GetStatisticsAsync());

        _out.WriteLine($"=== GetStatisticsAsync @ {movieCount} 部 / {watchLogCount} 条观影记录 ===");
        _out.WriteLine($"  [冷] 新 DbContext : {cold.ElapsedMs,7} ms / {cold.AllocatedBytes / 1024.0 / 1024,7:F2} MB  (首次打开统计页)");
        _out.WriteLine($"  [热] 复用 Context : {warm.ElapsedMs,7} ms / {warm.AllocatedBytes / 1024.0 / 1024,7:F2} MB  (再次切换，纯 CPU 聚合)");
        _out.WriteLine($"  每部均摊(冷)      : {(double)cold.AllocatedBytes / movieCount / 1024,7:F1} KB/部");

        // 护栏：冷启动 2000 部（约 172 MB 海报）不得退化为分钟级
        var budgetMs = 3000 + movieCount * 3;
        Assert.True(cold.ElapsedMs < budgetMs,
            $"冷启动统计耗时 {cold.ElapsedMs}ms 超出预算 {budgetMs}ms（{movieCount} 部）——疑似复杂度退化");
    }

    /// <summary>
    /// 诊断：CPU 热点定位。冷/热耗时接近说明瓶颈在 CPU 而非 IO，
    /// 但到底是「实体物化」还是「聚合算法」，必须分开测才能对症下药。
    /// </summary>
    [Fact]
    public async Task Diagnose_CpuHotspot()
    {
        const int n = 1000;
        var path = NewTempDb();
        Seed(path, n, 50);

        var opt = new DbContextOptionsBuilder<MovieDbContext>().UseSqlite($"Data Source={path}").Options;
        using var ctx = new MovieDbContext(opt);

        // 一次性读入「不含 BLOB」的窄投影，后续两种算法基于同一份内存数据公平对比
        var rows = await ctx.Movies.AsNoTracking()
            .Select(m => new { m.Rating, m.Director, m.Cast, m.Country }).ToListAsync();
        var movies = rows.Select(r => new Movie
        { Rating = r.Rating, Director = r.Director, Cast = r.Cast, Country = r.Country }).ToList();

        // A: 导演 Top10（原实现 AvgRating 对每个分组键回扫全表 → O(n·g)）
        var dir = await MeasureActionAsync(() =>
        { _ = ReferenceStatistics.PersonTop(movies, m => m.Director, 10); return Task.CompletedTask; });
        // B: 演员 Top10（同上）
        var cast = await MeasureActionAsync(() =>
        { _ = ReferenceStatistics.PersonTop(movies, m => m.Cast, 10); return Task.CompletedTask; });
        // C: 除 Director/Cast 外的其余全部聚合（标量统计 + 分组）
        var rest = await MeasureActionAsync(() =>
        {
            _ = movies.Count;
            _ = movies.Count(m => m.Rating.HasValue);
            _ = movies.Where(m => m.Rating.HasValue).Select(m => m.Rating!.Value).DefaultIfEmpty(0).Average();
            _ = movies.Where(m => m.Runtime.HasValue).Sum(m => m.Runtime!.Value);
            _ = movies.GroupBy(m => m.Country).Select(g => new { g.Key, Count = g.Count() })
                      .OrderByDescending(x => x.Count).Take(15).ToList();
            _ = movies.GroupBy(m => m.Rating).Select(g => new { g.Key, Count = g.Count() }).ToList();
            return Task.CompletedTask;
        });

        var total = dir.ElapsedMs + cast.ElapsedMs + rest.ElapsedMs;
        _out.WriteLine($"=== CPU 热点拆解 @ {n} 部（数据已在内存，无 IO） ===");
        _out.WriteLine($"  导演 Top10 (O(n·g)) : {dir.ElapsedMs,6} ms  ({dir.ElapsedMs * 100.0 / total,5:F1}%)");
        _out.WriteLine($"  演员 Top10 (O(n·g)) : {cast.ElapsedMs,6} ms  ({cast.ElapsedMs * 100.0 / total,5:F1}%)");
        _out.WriteLine($"  其余全部聚合        : {rest.ElapsedMs,6} ms  ({rest.ElapsedMs * 100.0 / total,5:F1}%)");
        _out.WriteLine($"  → 人名 AvgRating 回扫占比: {(dir.ElapsedMs + cast.ElapsedMs) * 100.0 / total:F1}%");
    }

    /// <summary>契约 Oracle：新实现必须与优化前的全量内存参考实现逐字段一致。</summary>
    [Theory]
    [InlineData(60)]
    [InlineData(400)]
    public async Task Contract_OptimizedMatchesReferenceImplementation(int movieCount)
    {
        var path = NewTempDb();
        Seed(path, movieCount, 80);

        var options = new DbContextOptionsBuilder<MovieDbContext>()
            .UseSqlite($"Data Source={path}").Options;
        using var ctx = new MovieDbContext(options);

        // 参考实现：优化前的全量加载 + 纯内存聚合（作为语义基准）
        var reference = await ReferenceStatistics.CalculateAsync(ctx);
        // 被测实现：当前 StatisticsService
        var actual = await new StatisticsService(ctx).GetStatisticsAsync();

        StatisticsAssert.EqualByField(reference, actual, _out);
    }
}

/// <summary>
/// 参考实现（Oracle）：完整保留 StatisticsService 优化前的语义——
/// 全量加载实体（含 PosterData）+ 纯内存聚合。任何优化都必须与本实现逐字段等价，
/// 语义若有变更，应先修改本 Oracle 并说明原因，而不是放宽断言。
/// </summary>
public static class ReferenceStatistics
{
    public static async Task<StatisticsData> CalculateAsync(MovieDbContext ctx)
    {
        var movies = await ctx.Movies
            .Include(m => m.Category)
            .Include(m => m.MovieTags).ThenInclude(mt => mt.Tag)
            .ToListAsync();
        var watchLogs = await ctx.WatchLogs.ToListAsync();

        var data = new StatisticsData
        {
            TotalMovies = movies.Count,
            WantToWatch = movies.Count(m => m.WatchStatus == WatchStatus.WantToWatch),
            NotWatched = movies.Count(m => m.WatchStatus == WatchStatus.NotWatched),
            Watched = movies.Count(m => m.WatchStatus == WatchStatus.Watched),
            Favorites = movies.Count(m => m.IsFavorite),
            RatedCount = movies.Count(m => m.Rating.HasValue),
            AverageRating = movies.Where(m => m.Rating.HasValue).Select(m => m.Rating!.Value)
                .DefaultIfEmpty(0).Average(),
            TotalRuntimeMinutes = movies.Where(m => m.Runtime.HasValue).Sum(m => m.Runtime!.Value)
        };

        // 并列以 Name 作次级排序键 + Ordinal 语义：与 SQLite 侧的 BINARY 排序保持一致，
        // 使 Oracle 与被测实现在 Count 相同时给出确定且相同的顺序。
        data.CategoryStats = movies.Where(m => m.Category != null)
            .GroupBy(m => m.Category!.Name)
            .Select(g => new CategoryStat { Name = g.Key, Count = g.Count() })
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        var uncategorized = movies.Count(m => m.CategoryId == null);
        if (uncategorized > 0)
            data.CategoryStats.Add(new CategoryStat { Name = "未分类", Count = uncategorized });

        data.RatingStats = Enumerable.Range(1, 10)
            .Select(r => new RatingStat { Rating = r, Count = movies.Count(m => m.Rating == r) })
            .Where(r => r.Count > 0).ToList();

        data.YearlyStats = movies.GroupBy(m => m.Year)
            .Select(g => new YearlyStat
            {
                Year = g.Key,
                AddedCount = g.Count(),
                WatchedCount = g.Count(m => m.WatchStatus == WatchStatus.Watched)
            })
            .OrderBy(y => y.Year).ToList();

        var currentYear = DateTime.Now.Year;
        var thisYearMovies = movies.Where(m => m.WatchDate.HasValue && m.WatchDate.Value.Year == currentYear);
        data.MonthlyStats = Enumerable.Range(1, 12)
            .Select(m => new MonthlyStat
            {
                Year = currentYear,
                Month = m,
                WatchedCount = thisYearMovies.Count(x => x.WatchDate!.Value.Month == m)
            }).ToList();

        data.DirectorStats = PersonTop(movies, m => m.Director, 10);
        data.CastStats = PersonTop(movies, m => m.Cast, 10);

        data.CountryStats = movies.Where(m => !string.IsNullOrEmpty(m.Country))
            .SelectMany(m => m.Country!.Split(new[] { "/", " ", "·", "," }, StringSplitOptions.RemoveEmptyEntries))
            .Select(c => c.Trim()).Where(c => !string.IsNullOrEmpty(c))
            .GroupBy(c => c).Select(g => new CountryStat { Name = g.Key, Count = g.Count() })
            .OrderByDescending(c => c.Count).Take(15).ToList();

        var runtimeRanges = new[]
        {
            new RuntimeRangeStat { Label = "< 60", MinMinutes = 0, MaxMinutes = 59 },
            new RuntimeRangeStat { Label = "60-90", MinMinutes = 60, MaxMinutes = 90 },
            new RuntimeRangeStat { Label = "90-120", MinMinutes = 91, MaxMinutes = 120 },
            new RuntimeRangeStat { Label = "120-150", MinMinutes = 121, MaxMinutes = 150 },
            new RuntimeRangeStat { Label = "> 150", MinMinutes = 151, MaxMinutes = 999 }
        };
        foreach (var range in runtimeRanges)
            range.Count = movies.Count(m => m.Runtime.HasValue && m.Runtime!.Value >= range.MinMinutes
                                                               && m.Runtime!.Value <= range.MaxMinutes);
        data.RuntimeStats = runtimeRanges.Where(r => r.Count > 0).ToList();

        data.GenreStats = movies.Where(m => m.MovieTags.Any())
            .SelectMany(m => m.MovieTags.Select(mt => mt.Tag.Name))
            .GroupBy(t => t).Select(g => new GenreStat { Name = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Name, StringComparer.Ordinal)
            .Take(15).ToList();

        data.CompletionRate = data.TotalMovies > 0
            ? Math.Round((double)data.Watched / data.TotalMovies * 100, 1) : 0;

        var thisYearLogMovieIds = watchLogs.Where(w => w.WatchDate.Year == currentYear)
            .Select(w => w.MovieId).Distinct().ToList();
        data.ThisYearWatchedCount = thisYearLogMovieIds.Count;
        data.ThisYearWatchedRuntimeMinutes = movies
            .Where(m => thisYearLogMovieIds.Contains(m.Id) && m.Runtime.HasValue)
            .Sum(m => m.Runtime!.Value);

        var dayNames = new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
        data.DayOfWeekStats = Enumerable.Range(0, 7)
            .Select(dow => new DayOfWeekStat
            {
                DayOfWeek = dow,
                DayName = dayNames[dow],
                Count = watchLogs.Count(w => (int)w.WatchDate.DayOfWeek == dow)
            }).ToList();

        if (watchLogs.Any())
        {
            var watchDates = watchLogs.Select(w => w.WatchDate.Date).Distinct().OrderBy(d => d).ToList();
            var maxStreak = 1;
            var currentStreak = 1;
            for (int i = 1; i < watchDates.Count; i++)
            {
                if (watchDates[i] == watchDates[i - 1].AddDays(1))
                {
                    currentStreak++;
                    if (currentStreak > maxStreak) maxStreak = currentStreak;
                }
                else currentStreak = 1;
            }
            data.LongestWatchStreak = watchDates.Count > 0 ? maxStreak : 0;
        }

        return data;
    }

    // 复刻原实现的 AvgRating 语义：对每个分组键回扫全表求均值（O(n·g)）
    internal static List<PersonStat> PersonTop(List<Movie> movies, Func<Movie, string?> selector, int take)
    {
        return movies.Where(m => !string.IsNullOrEmpty(selector(m)))
            .SelectMany(m => selector(m)!.Split(new[] { ", ", "、", " / ", "/" }, StringSplitOptions.RemoveEmptyEntries))
            .Select(p => p.Trim()).Where(p => !string.IsNullOrEmpty(p))
            .GroupBy(p => p)
            .Select(g => new PersonStat
            {
                Name = g.Key,
                Count = g.Count(),
                AvgRating = movies
                    .Where(m => !string.IsNullOrEmpty(selector(m)) && selector(m)!.Contains(g.Key) && m.Rating.HasValue)
                    .Select(m => m.Rating!.Value).DefaultIfEmpty(0).Average()
            })
            .OrderByDescending(p => p.Count).Take(take).ToList();
    }
}

/// <summary>逐字段比对 StatisticsData，失败时精确指出是哪个字段不一致。</summary>
public static class StatisticsAssert
{
    public static void EqualByField(StatisticsData expected, StatisticsData actual, ITestOutputHelper? output = null)
    {
        var diffs = new List<string>();

        void Cmp(string name, object? e, object? a)
        {
            if (!Equals(e, a)) diffs.Add($"{name}: 期望 {Fmt(e)} / 实际 {Fmt(a)}");
        }

        Cmp(nameof(expected.TotalMovies), expected.TotalMovies, actual.TotalMovies);
        Cmp(nameof(expected.WantToWatch), expected.WantToWatch, actual.WantToWatch);
        Cmp(nameof(expected.NotWatched), expected.NotWatched, actual.NotWatched);
        Cmp(nameof(expected.Watched), expected.Watched, actual.Watched);
        Cmp(nameof(expected.Favorites), expected.Favorites, actual.Favorites);
        Cmp(nameof(expected.RatedCount), expected.RatedCount, actual.RatedCount);
        Cmp(nameof(expected.AverageRating), Math.Round(expected.AverageRating, 6), Math.Round(actual.AverageRating, 6));
        Cmp(nameof(expected.TotalRuntimeMinutes), expected.TotalRuntimeMinutes, actual.TotalRuntimeMinutes);
        Cmp(nameof(expected.CompletionRate), expected.CompletionRate, actual.CompletionRate);
        Cmp(nameof(expected.ThisYearWatchedCount), expected.ThisYearWatchedCount, actual.ThisYearWatchedCount);
        Cmp(nameof(expected.ThisYearWatchedRuntimeMinutes), expected.ThisYearWatchedRuntimeMinutes,
            actual.ThisYearWatchedRuntimeMinutes);
        Cmp(nameof(expected.LongestWatchStreak), expected.LongestWatchStreak, actual.LongestWatchStreak);

        CmpList(diffs, nameof(expected.CategoryStats), expected.CategoryStats, actual.CategoryStats,
            x => (x.Name, x.Count));
        CmpList(diffs, nameof(expected.RatingStats), expected.RatingStats, actual.RatingStats,
            x => (x.Rating, x.Count));
        CmpList(diffs, nameof(expected.YearlyStats), expected.YearlyStats, actual.YearlyStats,
            x => (x.Year, x.AddedCount, x.WatchedCount));
        CmpList(diffs, nameof(expected.MonthlyStats), expected.MonthlyStats, actual.MonthlyStats,
            x => (x.Year, x.Month, x.WatchedCount));
        CmpList(diffs, nameof(expected.DirectorStats), expected.DirectorStats, actual.DirectorStats,
            x => (x.Name, x.Count, Math.Round(x.AvgRating, 6)));
        CmpList(diffs, nameof(expected.CastStats), expected.CastStats, actual.CastStats,
            x => (x.Name, x.Count, Math.Round(x.AvgRating, 6)));
        CmpList(diffs, nameof(expected.CountryStats), expected.CountryStats, actual.CountryStats,
            x => (x.Name, x.Count));
        CmpList(diffs, nameof(expected.RuntimeStats), expected.RuntimeStats, actual.RuntimeStats,
            x => (x.Label, x.Count));
        CmpList(diffs, nameof(expected.GenreStats), expected.GenreStats, actual.GenreStats,
            x => (x.Name, x.Count));
        CmpList(diffs, nameof(expected.DayOfWeekStats), expected.DayOfWeekStats, actual.DayOfWeekStats,
            x => (x.DayOfWeek, x.DayName, x.Count));

        if (diffs.Count > 0)
        {
            var msg = "统计结果与参考实现不一致（共 " + diffs.Count + " 处）：\n  - " + string.Join("\n  - ", diffs);
            output?.WriteLine(msg);
            Assert.Fail(msg);
        }
        output?.WriteLine("契约比对通过：全部字段与参考实现一致");
    }

    private static void CmpList<T>(List<string> diffs, string name, List<T> expected, List<T> actual, Func<T, object> key)
    {
        var e = string.Join(" | ", expected.Select(key));
        var a = string.Join(" | ", actual.Select(key));
        if (e != a) diffs.Add($"{name}:\n      期望 [{e}]\n      实际 [{a}]");
    }

    private static string Fmt(object? v) => v?.ToString() ?? "<null>";
}
