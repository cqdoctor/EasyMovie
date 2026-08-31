using System.Diagnostics;
using EasyMovie.Core;
using EasyMovie.Core.Helpers;
using EasyMovie.Core.Models;
using EasyMovie.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace EasyMovie.Tests.Core.Tests;

/// <summary>
/// MovieBootstrapService 契约回归网 + 性能基准（永久固化，离线可跑）。
///
/// 背景：原逻辑内联在 MovieListView.LoadDataAsync()，每次窗口 Loaded 都
/// _movieService.GetAllAsync() —— 该方法 Include(Category) + Include(MovieTags).ThenInclude(Tag)，
/// 把全库海报读进内存只为填几个下拉框（290 部 24 MB、2000 部 172 MB；集合导航 JOIN 还会成倍放大）。
///
/// 三重保障：
///   1. 契约 Oracle：保留优化前的「全量加载实现」作为参考实现，与新实现逐字段比对
///      （电影 CategoryId / SearchIndex、分类 Id+Name、筛选选项），确保重构没有改变语义
///   2. 性能基线：产出可复现的耗时 / 托管堆分配数字（290 → 2000 部劣化曲线）
///   3. 复杂度护栏：2000 部规模下引导分配量设上限
///
/// 【基准测量的两个必要约束】（否则数字不可复现、护栏会 flaky）
///   · [Collection("Benchmark")]：GC.GetTotalAllocatedBytes 是**进程级全局计数器**，
///     若基准测试类彼此并行，其他类的分配会被计入本类的测量值。必须与所有基准类
///     同属一个 Collection 串行执行（StatisticsBenchmarkTests /
///     RecommendationBenchmarkTests 同样标记了 "Benchmark"）。
///   · 取最小值而非中位数：即使串行，后台 GC / JIT / 连接池仍会引入噪声，
///     最小值受噪声影响最小。
/// </summary>
[Collection("Benchmark")]
public class MovieBootstrapTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly List<string> _tempFiles = new();

    // 实测自 EasyMovie.db：288 张海报 / 290 部，平均 86.4 KB（占库 99.4%）
    private const int AvgPosterBytes = 86 * 1024;

    public MovieBootstrapTests(ITestOutputHelper output) => _out = output;

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
        var dir = Path.Combine(Path.GetTempPath(), "EasyMovieBootstrapBench");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"boot_{Guid.NewGuid():N}.db");
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

    // ───────────────────────────── 种子 ─────────────────────────────

    /// <summary>
    /// 构造合成媒体库。dirty=true 时覆盖全部写入分支：无效分类、未分类影片、
    /// 缺失搜索索引、多值国家字段、非法国家名。
    /// CreatedAt 逐部递增，确保 OrderByDescending(CreatedAt) 顺序确定（分类 Id 分配依赖它）。
    /// </summary>
    private static void Seed(string path, int movieCount, bool dirty)
    {
        using var ctx = CreateContext(path);
        ctx.Database.EnsureCreated();
        ctx.ChangeTracker.AutoDetectChangesEnabled = false;

        var rng = new Random(42);

        var catNames = dirty
            ? new[] { "科幻", "剧情", "动作", "人收藏", "123" }   // 后两个是垃圾分类名
            : new[] { "科幻", "剧情", "动作" };
        foreach (var n in catNames)
            ctx.Categories.Add(new Category { Name = n, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        ctx.SaveChanges();

        var cats = ctx.Categories.AsNoTracking().ToList();
        var junkCat = cats.FirstOrDefault(c => c.Name == "人收藏");
        var goodCats = cats.Where(c => c.Name is "科幻" or "剧情" or "动作").ToList();

        var countries = new[] { "美国", "中国", "日本", "韩国", "法国" };
        var languages = new[] { "英语", "汉语普通话", "日语", "韩语" };
        var directors = new[] { "诺兰", "张艺谋", "是枝裕和", "朴赞郁" };

        var poster = new byte[AvgPosterBytes];
        rng.NextBytes(poster);

        var baseTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var movies = new List<Movie>(movieCount);

        for (int i = 0; i < movieCount; i++)
        {
            int? categoryId;
            string? country = countries[rng.Next(countries.Length)];
            string? searchIndex = PinyinIndexHelper.BuildSearchIndex($"影片{i}", $"Movie{i}", "诺兰", "演员A, 演员B");

            switch (i % 5)
            {
                case 0: categoryId = goodCats[i % goodCats.Count].Id; break;
                case 1: categoryId = dirty ? junkCat?.Id : goodCats[i % goodCats.Count].Id; break;
                case 2: categoryId = null; country = $"美国 / {countries[rng.Next(countries.Length)]}"; break;
                case 3: categoryId = null; searchIndex = dirty ? null : searchIndex; break;
                default: categoryId = null; country = null; break;
            }

            // 额外覆盖「国家名本身非法 → 应跳过自动分配」
            if (dirty && i % 7 == 0) { categoryId = null; country = "人收藏"; }

            movies.Add(new Movie
            {
                Title = $"影片{i}",
                OriginalTitle = $"Movie{i}",
                Year = 1950 + rng.Next(77),
                Director = directors[i % directors.Length],
                Cast = "演员A, 演员B, 演员C",
                Country = country,
                Language = languages[i % languages.Length],
                Runtime = 60 + rng.Next(120),
                CategoryId = categoryId,
                SearchIndex = searchIndex,
                FilePath = $@"D:\Movies\film{i}.mkv",
                PosterData = poster,
                CreatedAt = baseTime.AddSeconds(i),   // 严格递增 → 顺序确定
                UpdatedAt = baseTime.AddSeconds(i)
            });
        }

        ctx.Movies.AddRange(movies);
        ctx.SaveChanges();
    }

    // ─────────────────────── 优化前参考实现（Oracle） ───────────────────────

    private sealed record FilterSnapshot(
        List<int> Years,
        List<string> Countries,
        List<string> Languages,
        List<string> Directors,
        bool HasYearRange, double YearMin, double YearMax,
        bool HasRuntimeRange, double RuntimeMin, double RuntimeMax);

    private sealed record BootstrapSnapshot(
        List<(int Id, int? CategoryId, string? SearchIndex)> Movies,
        List<(int Id, string Name)> Categories,
        List<string> CategoriesWithMovies,
        bool HasUncategorized,
        FilterSnapshot Filter);

    /// <summary>
    /// 优化前的实现，逐行复刻自 MovieListView（LoadDataAsync / RebuildSearchIndexBatchAsync /
    /// AutoAssignCountryCategoriesBatchAsync / PopulateYearFilter / PopulateAdvancedFilterOptions）。
    /// 这是判定新实现是否等价的唯一标准，不得随新实现一起改动。
    /// </summary>
    private static async Task<BootstrapSnapshot> LegacyBootstrapAsync(string path)
    {
        using var ctx = CreateContext(path);

        // 原 MovieRepository.GetAllAsync()
        var allMovies = await ctx.Movies
            .Include(m => m.Category)
            .Include(m => m.MovieTags).ThenInclude(mt => mt.Tag)
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync();

        // 原 CategoryRepository.GetAllAsync()
        var allCats = await ctx.Categories.Include(c => c.Children).OrderBy(c => c.Name).ToListAsync();

        // 原 RebuildSearchIndexBatchAsync
        var needUpdate = allMovies.Where(m => string.IsNullOrEmpty(m.SearchIndex)).ToList();
        if (needUpdate.Count != 0)
        {
            foreach (var m in needUpdate)
                m.SearchIndex = PinyinIndexHelper.BuildSearchIndex(m.Title, m.OriginalTitle, m.Director, m.Cast);
            await ctx.SaveChangesAsync();
        }

        // 原 AutoAssignCountryCategoriesBatchAsync
        var invalidCats = allCats.Where(c => !CategoryNameValidator.IsValidCategoryName(c.Name)).ToList();
        foreach (var cat in invalidCats)
        {
            foreach (var m in allMovies.Where(m => m.CategoryId == cat.Id)) m.CategoryId = null;
            ctx.Categories.Remove(cat);
        }
        if (invalidCats.Count > 0) await ctx.SaveChangesAsync();

        var uncatMovies = allMovies
            .Where(m => !m.CategoryId.HasValue && !string.IsNullOrWhiteSpace(m.Country))
            .ToList();
        if (uncatMovies.Count != 0)
        {
            var validCats = await ctx.Categories.Include(c => c.Children).OrderBy(c => c.Name).ToListAsync();
            foreach (var movie in uncatMovies)
            {
                var firstCountry = movie.Country!
                    .Split('/', '·')
                    .FirstOrDefault(c => CategoryNameValidator.IsValidCategoryName(c.Trim()))
                    ?.Trim();
                if (string.IsNullOrEmpty(firstCountry)
                    || !CategoryNameValidator.IsValidCategoryName(firstCountry)) continue;

                var existing = validCats.FirstOrDefault(c => c.Name == firstCountry);
                if (existing != null) movie.CategoryId = existing.Id;
                else
                {
                    var newCat = new Category
                    {
                        Name = firstCountry,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    ctx.Categories.Add(newCat);
                    await ctx.SaveChangesAsync();
                    validCats.Add(newCat);
                    movie.CategoryId = newCat.Id;
                }
            }
            await ctx.SaveChangesAsync();
        }

        // 数据可能已变更，重新加载
        var allCats2 = await ctx.Categories.Include(c => c.Children).OrderBy(c => c.Name).ToListAsync();
        var usedCategoryIds = allMovies.Where(m => m.CategoryId.HasValue)
                                       .Select(m => m.CategoryId!.Value).Distinct().ToHashSet();
        var catsWithMovies = allCats2.Where(c => usedCategoryIds.Contains(c.Id)).Select(c => c.Name).ToList();
        bool hasUncategorized = allMovies.Any(m => !m.CategoryId.HasValue);

        return new BootstrapSnapshot(
            allMovies.OrderBy(m => m.Id).Select(m => (m.Id, m.CategoryId, m.SearchIndex)).ToList(),
            allCats2.Select(c => (c.Id, c.Name)).OrderBy(c => c.Id).ToList(),
            catsWithMovies,
            hasUncategorized,
            LegacyBuildFilter(allMovies));
    }

    /// <summary>原 PopulateYearFilter + PopulateAdvancedFilterOptions 的数据部分（不含 WPF 赋值）。</summary>
    private static FilterSnapshot LegacyBuildFilter(List<Movie> allMovies)
    {
        var years = allMovies.Where(m => m.Year > 0).Select(m => m.Year).Distinct()
                             .OrderByDescending(y => y).ToList();

        var countries = allMovies.Where(m => !string.IsNullOrWhiteSpace(m.Country))
            .SelectMany(m => m.Country!.Split('/', ' ', '·', ','))
            .Select(c => TextCleaner.CleanHtmlFragment(c.Trim()))
            .Where(c => !string.IsNullOrEmpty(c) && CategoryNameValidator.IsValidCategoryName(c))
            .Distinct().OrderBy(c => c).ToList();

        var languages = allMovies.Where(m => !string.IsNullOrWhiteSpace(m.Language))
            .SelectMany(m => m.Language!.Split('/', ' ', '·', ','))
            .Select(l => TextCleaner.CleanHtmlFragment(l.Trim()))
            .Where(l => !string.IsNullOrEmpty(l))
            .Distinct().OrderBy(l => l).ToList();

        var directors = allMovies.Where(m => !string.IsNullOrWhiteSpace(m.Director))
            .SelectMany(m => m.Director!.Split('/', ','))
            .Select(d => TextCleaner.CleanHtmlFragment(d.Trim()))
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct().OrderBy(d => d).ToList();

        var currentYear = DateTime.Now.Year;
        var validYears = allMovies.Where(m => m.Year >= 1880 && m.Year <= currentYear + 1)
                                  .Select(m => (double)m.Year).ToList();
        double yMin = 0, yMax = 0;
        bool hasY = validYears.Count > 0;
        if (hasY)
        {
            yMin = Math.Floor(validYears.Min() / 10.0) * 10;
            yMax = Math.Min(currentYear, Math.Ceiling(validYears.Max() / 10.0) * 10);
        }

        var validRuntimes = allMovies.Where(m => m.Runtime > 0 && m.Runtime < 600)
                                     .Select(m => (double)m.Runtime!.Value).ToList();
        double rMin = 0, rMax = 0;
        bool hasR = validRuntimes.Count > 0;
        if (hasR)
        {
            rMin = Math.Floor(validRuntimes.Min() / 30.0) * 30;
            rMax = Math.Ceiling(validRuntimes.Max() / 30.0) * 30;
        }

        return new FilterSnapshot(years, countries, languages, directors, hasY, yMin, yMax, hasR, rMin, rMax);
    }

    // ─────────────────────── 新实现快照 ───────────────────────

    private static async Task<BootstrapSnapshot> NewBootstrapAsync(string path)
    {
        using var ctx = CreateContext(path);
        var result = await new MovieBootstrapService(ctx).BootstrapAsync();

        var movies = await ctx.Movies.AsNoTracking()
            .Select(m => new { m.Id, m.CategoryId, m.SearchIndex })
            .OrderBy(m => m.Id)
            .ToListAsync();

        return new BootstrapSnapshot(
            movies.Select(m => (m.Id, m.CategoryId, m.SearchIndex)).ToList(),
            result.AllCategories.Select(c => (c.Id, c.Name)).OrderBy(c => c.Id).ToList(),
            result.CategoriesWithMovies.Select(c => c.Name).ToList(),
            result.HasUncategorized,
            new FilterSnapshot(
                result.FilterOptions.Years,
                result.FilterOptions.Countries,
                result.FilterOptions.Languages,
                result.FilterOptions.Directors,
                result.FilterOptions.HasYearRange,
                result.FilterOptions.YearMin,
                result.FilterOptions.YearMax,
                result.FilterOptions.HasRuntimeRange,
                result.FilterOptions.RuntimeMin,
                result.FilterOptions.RuntimeMax));
    }

    private static void AssertEquivalent(BootstrapSnapshot expected, BootstrapSnapshot actual)
    {
        // 1. 每部电影的 CategoryId（分类分配的最终落点）
        Assert.Equal(
            expected.Movies.Select(m => $"{m.Id}:{m.CategoryId?.ToString() ?? "-"}"),
            actual.Movies.Select(m => $"{m.Id}:{m.CategoryId?.ToString() ?? "-"}"));

        // 2. 每部电影的 SearchIndex
        Assert.Equal(
            expected.Movies.Select(m => $"{m.Id}:{m.SearchIndex ?? "-"}"),
            actual.Movies.Select(m => $"{m.Id}:{m.SearchIndex ?? "-"}"));

        // 3. 分类表（含 Id —— 验证新建分类的创建顺序与 Id 分配完全一致）
        Assert.Equal(
            expected.Categories.Select(c => $"{c.Id}:{c.Name}"),
            actual.Categories.Select(c => $"{c.Id}:{c.Name}"));

        // 4. 有电影的分类列表 / 未分类标记
        Assert.Equal(expected.CategoriesWithMovies, actual.CategoriesWithMovies);
        Assert.Equal(expected.HasUncategorized, actual.HasUncategorized);

        // 5. 筛选下拉框选项
        Assert.Equal(expected.Filter.Years, actual.Filter.Years);
        Assert.Equal(expected.Filter.Countries, actual.Filter.Countries);
        Assert.Equal(expected.Filter.Languages, actual.Filter.Languages);
        Assert.Equal(expected.Filter.Directors, actual.Filter.Directors);
        Assert.Equal(expected.Filter.HasYearRange, actual.Filter.HasYearRange);
        Assert.Equal(expected.Filter.YearMin, actual.Filter.YearMin);
        Assert.Equal(expected.Filter.YearMax, actual.Filter.YearMax);
        Assert.Equal(expected.Filter.HasRuntimeRange, actual.Filter.HasRuntimeRange);
        Assert.Equal(expected.Filter.RuntimeMin, actual.Filter.RuntimeMin);
        Assert.Equal(expected.Filter.RuntimeMax, actual.Filter.RuntimeMax);
    }

    // ─────────────────────── Oracle 用例 ───────────────────────

    [Fact]
    public async Task Oracle_DirtyLibrary_MatchesLegacyImplementation()
    {
        // 覆盖：无效分类清理、国家分类自动分配（新建 + 复用）、搜索索引重建、
        //       多值国家字段取首个合法值、非法国家名跳过
        var legacyDb = NewTempDb();
        var newDb = NewTempDb();
        Seed(legacyDb, 20, dirty: true);
        Seed(newDb, 20, dirty: true);

        var expected = await LegacyBootstrapAsync(legacyDb);
        var actual = await NewBootstrapAsync(newDb);

        AssertEquivalent(expected, actual);
    }

    [Fact]
    public async Task Oracle_CleanLibrary_MatchesLegacyImplementation()
    {
        // 覆盖：无需任何写入的常见路径（三条写入分支全部短路）
        var legacyDb = NewTempDb();
        var newDb = NewTempDb();
        Seed(legacyDb, 20, dirty: false);
        Seed(newDb, 20, dirty: false);

        var expected = await LegacyBootstrapAsync(legacyDb);
        var actual = await NewBootstrapAsync(newDb);

        AssertEquivalent(expected, actual);
    }

    [Fact]
    public async Task Oracle_DirtyLibrary_ActuallyWroteSomething()
    {
        // 护栏：确认 dirty 种子真的触发了写入路径，否则上面两条 Oracle 是在测空气
        var db = NewTempDb();
        Seed(db, 20, dirty: true);

        using (var probe = CreateContext(db))
        {
            Assert.Contains(await probe.Categories.CountAsync(c => c.Name == "人收藏" || c.Name == "123"),
                            new[] { 1, 2 });
            Assert.True(await probe.Movies.AnyAsync(m => m.SearchIndex == null));
            Assert.True(await probe.Movies.AnyAsync(m => m.CategoryId == null && m.Country != null));
        }

        using var ctx = CreateContext(db);
        await new MovieBootstrapService(ctx).BootstrapAsync();

        // 垃圾分类名已被清理，且不再有未分类（带国家的）影片
        Assert.Empty(await ctx.Categories.AsNoTracking().Where(c => c.Name == "人收藏" || c.Name == "123").ToListAsync());
        Assert.Empty(await ctx.Movies.AsNoTracking().Where(m => m.SearchIndex == null).ToListAsync());
        Assert.Empty(await ctx.Movies.AsNoTracking()
            .Where(m => m.CategoryId == null && m.Country != null && m.Country != "人收藏").ToListAsync());
    }

    // ─────────────────────── 性能基准 ───────────────────────

    private readonly record struct BenchResult(long Ms, long Bytes);

    /// <summary>
    /// 测量耗时与托管堆分配，取多次运行的**最小值**。
    /// 为什么是最小值：即便已串行，GC / JIT / 连接池的噪声只会让数字变大，
    /// 最小值最接近真实成本，也最可复现（中位数会把噪声一起average进去）。
    /// </summary>
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
    public async Task Benchmark_BootstrapVsFullLoad(int movieCount)
    {
        var db = NewTempDb();
        Seed(db, movieCount, dirty: false);   // 干净库：纯查询成本，不受写入干扰

        // 基准 A：优化前的查询形状（全量 + 两个 Include）
        var legacy = await MeasureAsync(async () =>
        {
            using var ctx = CreateContext(db);   // 每次新建 context：复用会因 identity resolution 低估成本
            await ctx.Movies
                .Include(m => m.Category)
                .Include(m => m.MovieTags).ThenInclude(mt => mt.Tag)
                .OrderByDescending(m => m.CreatedAt)
                .ToListAsync();
        });

        // 基准 B：新实现的引导（窄投影 + 分类 + 筛选选项派生）
        var modern = await MeasureAsync(async () =>
        {
            using var ctx = CreateContext(db);
            await new MovieBootstrapService(ctx).BootstrapAsync();
        });

        var ratio = legacy.Bytes / (double)Math.Max(modern.Bytes, 1);
        _out.WriteLine($"[{movieCount} 部] 全量加载 {legacy.Ms,5} ms / {legacy.Bytes / 1024.0 / 1024,8:F2} MB   " +
                       $"引导 {modern.Ms,5} ms / {modern.Bytes / 1024.0 / 1024,8:F2} MB   " +
                       $"内存降至 {1 / ratio:P2}（{ratio:F1}×）");

        // 护栏：新实现必须显著低于全量加载，且与规模无关地保持「不读 BLOB」这一性质
        Assert.True(modern.Bytes < legacy.Bytes / 10,
            $"引导分配量 {modern.Bytes / 1024.0 / 1024:F2} MB 未降到全量加载 " +
            $"{legacy.Bytes / 1024.0 / 1024:F2} MB 的 1/10 以下");

        // 规模护栏：2000 部时引导分配量仍需远小于单部海报体积（86 KB）的线性累积
        Assert.True(modern.Bytes < movieCount * AvgPosterBytes / 10,
            "引导分配量随影片数线性增长，说明又读回了大字段");
    }

    [Fact]
    public async Task Benchmark_ExistingFilePaths_VsFullLoad()
    {
        var db = NewTempDb();
        Seed(db, 290, dirty: false);

        var legacy = await MeasureAsync(async () =>
        {
            using var ctx = CreateContext(db);
            await ctx.Movies.Include(m => m.Category)
                .Include(m => m.MovieTags).ThenInclude(mt => mt.Tag)
                .OrderByDescending(m => m.CreatedAt).ToListAsync();
        });

        var modern = await MeasureAsync(async () =>
        {
            using var ctx = CreateContext(db);
            await new MovieBootstrapService(ctx).GetExistingFilePathsAsync();
        });

        _out.WriteLine($"[去重路径] 全量 {legacy.Ms} ms / {legacy.Bytes / 1024.0 / 1024:F2} MB   " +
                       $"单列 {modern.Ms} ms / {modern.Bytes / 1024.0:F0} KB");

        Assert.True(modern.Bytes < legacy.Bytes / 10);
    }

    /// <summary>
    /// 在**真实生产库**上测量（默认跳过，需设置环境变量 EASYMOVIE_REAL_DB 指向 EasyMovie.db）。
    ///
    /// 安全约束：绝不读写原库 —— 先复制两份副本，legacy 与 modern 各用一份（Bootstrap 会写库，
    /// 共用一份会让后跑的一方看到已被修改的初始状态，测量失真）。
    ///
    /// 为什么需要这个诊断：合成数据无法完全复现真实库的标签关联度、国家/语言字段形态，
    /// 只有真实库能给出用户实际会感受到的数字。
    /// </summary>
    [Fact]
    public async Task Diagnose_RealDatabase()
    {
        var src = Environment.GetEnvironmentVariable("EASYMOVIE_REAL_DB");
        if (string.IsNullOrEmpty(src) || !File.Exists(src))
        {
            _out.WriteLine("跳过：未设置环境变量 EASYMOVIE_REAL_DB（指向 EasyMovie.db 的完整路径）");
            return;
        }

        // 复制到临时目录，连带 WAL/SHM（否则可能读到不完整数据）
        string MakeCopy()
        {
            var dst = NewTempDb();
            File.Copy(src, dst, overwrite: true);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                if (File.Exists(src + suffix)) File.Copy(src + suffix, dst + suffix, overwrite: true);
            }
            return dst;
        }

        var dbLegacy = MakeCopy();
        var dbModern = MakeCopy();

        using (var probe = CreateContext(dbLegacy))
        {
            _out.WriteLine($"真实库（副本）影片数: {await probe.Movies.CountAsync()}");
        }

        var legacy = await MeasureAsync(async () =>
        {
            using var ctx = CreateContext(dbLegacy);
            await ctx.Movies
                .Include(m => m.Category)
                .Include(m => m.MovieTags).ThenInclude(mt => mt.Tag)
                .OrderByDescending(m => m.CreatedAt)
                .ToListAsync();
        });

        var modern = await MeasureAsync(async () =>
        {
            using var ctx = CreateContext(dbModern);
            await new MovieBootstrapService(ctx).BootstrapAsync();
        });

        _out.WriteLine($"[真实库] 全量加载 {legacy.Ms,5} ms / {legacy.Bytes / 1024.0 / 1024,8:F2} MB   " +
                       $"引导 {modern.Ms,5} ms / {modern.Bytes / 1024.0 / 1024,8:F2} MB   " +
                       $"内存降至 {modern.Bytes / (double)legacy.Bytes:P2}（{legacy.Bytes / (double)Math.Max(modern.Bytes, 1):F1}×）");
    }

    [Fact]
    public async Task GetExistingFilePaths_MatchesFullLoad()
    {
        var db = NewTempDb();
        Seed(db, 20, dirty: false);

        using var ctx = CreateContext(db);
        var all = await ctx.Movies.AsNoTracking().ToListAsync();
        var expected = new HashSet<string>(all.Where(m => m.FilePath != null).Select(m => m.FilePath!));

        using var ctx2 = CreateContext(db);
        var actual = await new MovieBootstrapService(ctx2).GetExistingFilePathsAsync();

        Assert.Equal(expected.OrderBy(p => p), actual.OrderBy(p => p));
        Assert.NotEmpty(actual);
    }
}
