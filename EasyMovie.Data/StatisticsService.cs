using Microsoft.EntityFrameworkCore;
using EasyMovie.Core.Enums;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;

namespace EasyMovie.Data;

/// <summary>
/// 统计服务实现
/// </summary>
/// <remarks>
/// 性能设计（2026-08-28 实测重构，基线见 EasyMovie.Tests/Core.Tests/StatisticsBenchmarkTests.cs）：
///
/// 优化前 @1000 部：冷 1299 ms / 95 MB；@290 部（当前真实库）：冷 409 ms / 27.9 MB。两处根因：
///   1. 全实体加载把 PosterData 一起读进内存 —— 实测海报占全库 99.4%（24.29 MB / 27.93 MB，
///      平均 86.4 KB/张），而统计页一个字节的海报都不需要。全实体 vs 窄投影：85.36 MB vs 0.45 MB。
///   2. 人名 Top10 的 AvgRating 在 OrderByDescending 之前对「每个」分组回扫全表（O(n·g)），
///      而最终只保留 10 个 —— 实测占 CPU 的 99.4%（演员 127 ms + 导演 36 ms，其余全部聚合仅 1 ms）。
///
/// 对策：
///   1. 主查询改为窄投影（只取统计所需标量列），不读 PosterData，也不 Include 导航集合
///      （Include 会产生 JOIN 笛卡尔积并做实体 fixup，代价随标签数放大）。
///   2. 人名 Top10 改为两遍：先 O(n) 计数排序取 Top N，再只为这 N 个名字算 AvgRating
///      （O(n·take) 而非 O(n·g)）。排序键仍为 Count，且沿用同样的 GroupBy + 稳定排序，
///      因此并列时的先后顺序与原实现逐项一致。
///
/// 语义由测试中的参考实现（ReferenceStatistics）逐字段守护，任何改动都必须保持字段级一致。
/// </remarks>
public class StatisticsService : IStatisticsService
{
    // 与 Movie.Director / Movie.Cast 的多值分隔约定保持一致
    private static readonly string[] PersonSeparators = { ", ", "、", " / ", "/" };

    private readonly MovieDbContext _context;

    public StatisticsService(MovieDbContext context)
    {
        _context = context;
    }

    public async Task<StatisticsData> GetStatisticsAsync()
    {
        // 窄投影：只取统计真正用到的列。Note: 绝不可改回 Movies.ToListAsync()——
        // 那会把 PosterData（占库 99.4%）整包读进托管堆。
        var movies = await _context.Movies
            .AsNoTracking()
            .Select(m => new MovieStatRow
            {
                Id = m.Id,
                Year = m.Year,
                Rating = m.Rating,
                Runtime = m.Runtime,
                Director = m.Director,
                Cast = m.Cast,
                Country = m.Country,
                WatchStatus = m.WatchStatus,
                IsFavorite = m.IsFavorite,
                WatchDate = m.WatchDate,
                CategoryId = m.CategoryId
            })
            .ToListAsync();

        var watchLogs = await _context.WatchLogs
            .AsNoTracking()
            .Select(w => new WatchLogStatRow { MovieId = w.MovieId, WatchDate = w.WatchDate })
            .ToListAsync();

        var data = new StatisticsData
        {
            TotalMovies = movies.Count,
            WantToWatch = movies.Count(m => m.WatchStatus == WatchStatus.WantToWatch),
            NotWatched = movies.Count(m => m.WatchStatus == WatchStatus.NotWatched),
            Watched = movies.Count(m => m.WatchStatus == WatchStatus.Watched),
            Favorites = movies.Count(m => m.IsFavorite),
            RatedCount = movies.Count(m => m.Rating.HasValue),
            AverageRating = movies.Where(m => m.Rating.HasValue)
                .Select(m => m.Rating!.Value)
                .DefaultIfEmpty(0)
                .Average(),
            TotalRuntimeMinutes = movies.Where(m => m.Runtime.HasValue).Sum(m => m.Runtime!.Value)
        };

        // 分类分布（需要 join Categories，单独下推，避免主查询带导航属性）
        data.CategoryStats = await GetCategoryDistributionAsync();

        // 有电影但未分类的
        var uncategorized = movies.Count(m => m.CategoryId == null);
        if (uncategorized > 0)
            data.CategoryStats.Add(new CategoryStat { Name = "未分类", Count = uncategorized });

        // 评分分布
        data.RatingStats = movies
            .Where(m => m.Rating.HasValue && m.Rating.Value >= 1 && m.Rating.Value <= 10)
            .GroupBy(m => m.Rating!.Value)
            .Select(g => new RatingStat { Rating = g.Key, Count = g.Count() })
            .OrderBy(r => r.Rating)
            .ToList();

        // 年度统计
        data.YearlyStats = movies
            .GroupBy(m => m.Year)
            .Select(g => new YearlyStat
            {
                Year = g.Key,
                AddedCount = g.Count(),
                WatchedCount = g.Count(m => m.WatchStatus == WatchStatus.Watched)
            })
            .OrderBy(y => y.Year)
            .ToList();

        // 今年月度统计
        var currentYear = DateTime.Now.Year;
        var thisYearMovies = movies.Where(m => m.WatchDate.HasValue && m.WatchDate.Value.Year == currentYear)
            .ToList();
        data.MonthlyStats = Enumerable.Range(1, 12)
            .Select(m => new MonthlyStat
            {
                Year = currentYear,
                Month = m,
                WatchedCount = thisYearMovies.Count(x => x.WatchDate!.Value.Month == m)
            })
            .ToList();

        // 导演 / 演员排行 Top 10
        data.DirectorStats = BuildPersonTop(movies, m => m.Director, 10);
        data.CastStats = BuildPersonTop(movies, m => m.Cast, 10);

        // 国家/地区分布
        data.CountryStats = movies
            .Where(m => !string.IsNullOrEmpty(m.Country))
            .SelectMany(m => m.Country!.Split(new[] { "/", " ", "·", "," }, StringSplitOptions.RemoveEmptyEntries))
            .Select(c => c.Trim())
            .Where(c => !string.IsNullOrEmpty(c))
            .GroupBy(c => c)
            .Select(g => new CountryStat { Name = g.Key, Count = g.Count() })
            .OrderByDescending(c => c.Count)
            .Take(15)
            .ToList();

        // 片长分布
        var runtimeRanges = new[]
        {
            new RuntimeRangeStat { Label = "< 60", MinMinutes = 0, MaxMinutes = 59 },
            new RuntimeRangeStat { Label = "60-90", MinMinutes = 60, MaxMinutes = 90 },
            new RuntimeRangeStat { Label = "90-120", MinMinutes = 91, MaxMinutes = 120 },
            new RuntimeRangeStat { Label = "120-150", MinMinutes = 121, MaxMinutes = 150 },
            new RuntimeRangeStat { Label = "> 150", MinMinutes = 151, MaxMinutes = 999 }
        };
        foreach (var range in runtimeRanges)
        {
            range.Count = movies.Count(m => m.Runtime.HasValue && m.Runtime!.Value >= range.MinMinutes && m.Runtime!.Value <= range.MaxMinutes);
        }
        data.RuntimeStats = runtimeRanges.Where(r => r.Count > 0).ToList();

        // 类型分布（基于标签，单独聚合，避免主查询 Include 集合导致 JOIN 笛卡尔积）
        data.GenreStats = await GetGenreDistributionAsync();

        // 观影完成率
        data.CompletionRate = data.TotalMovies > 0
            ? Math.Round((double)data.Watched / data.TotalMovies * 100, 1)
            : 0;

        // 今年观影统计（复用上方 currentYear 变量）
        var thisYearLogMovieIds = watchLogs
            .Where(w => w.WatchDate.Year == currentYear)
            .Select(w => w.MovieId)
            .Distinct()
            .ToList();
        data.ThisYearWatchedCount = thisYearLogMovieIds.Count;
        data.ThisYearWatchedRuntimeMinutes = movies
            .Where(m => thisYearLogMovieIds.Contains(m.Id) && m.Runtime.HasValue)
            .Sum(m => m.Runtime!.Value);

        // 最活跃星期（基于观影记录）
        var dayNames = new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
        data.DayOfWeekStats = Enumerable.Range(0, 7)
            .Select(dow => new DayOfWeekStat
            {
                DayOfWeek = dow,
                DayName = dayNames[dow],
                Count = watchLogs.Count(w => (int)w.WatchDate.DayOfWeek == dow)
            })
            .ToList();

        // 最长连续观影天数
        if (watchLogs.Any())
        {
            var watchDates = watchLogs
                .Select(w => w.WatchDate.Date)
                .Distinct()
                .OrderBy(d => d)
                .ToList();

            var maxStreak = 1;
            var currentStreak = 1;
            for (int i = 1; i < watchDates.Count; i++)
            {
                if (watchDates[i] == watchDates[i - 1].AddDays(1))
                {
                    currentStreak++;
                    if (currentStreak > maxStreak) maxStreak = currentStreak;
                }
                else
                {
                    currentStreak = 1;
                }
            }
            data.LongestWatchStreak = watchDates.Count > 0 ? maxStreak : 0;
        }

        return data;
    }

    /// <summary>
    /// 人名（导演/演员）Top N。
    /// 关键：AvgRating 只在最终保留的 Top N 上计算，不为排序后被丢弃的分组付费。
    /// 排序键仍为 Count，并沿用与原实现相同的 GroupBy + 稳定排序，故顺序逐项一致。
    /// </summary>
    private static List<PersonStat> BuildPersonTop(
        List<MovieStatRow> movies, Func<MovieStatRow, string?> selector, int take)
    {
        // 第一遍：O(n) 计数 + 排序取 Top N（此处不算 AvgRating）
        var top = movies
            .Where(m => !string.IsNullOrEmpty(selector(m)))
            .SelectMany(m => selector(m)!.Split(PersonSeparators, StringSplitOptions.RemoveEmptyEntries))
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrEmpty(p))
            .GroupBy(p => p)
            .Select(g => new PersonStat { Name = g.Key, Count = g.Count() })
            .OrderByDescending(p => p.Count)
            .Take(take)
            .ToList();

        if (top.Count == 0) return top;

        // 第二遍：只为 Top N 计算均值。保留原语义——子串匹配（Contains）、
        // 未评分不参与、无命中时 DefaultIfEmpty(0).Average() == 0。
        // 预取文本并预先过滤无评分项，省掉 O(n·take) 次委托调用与空值判断。
        var rated = movies
            .Where(m => m.Rating.HasValue && !string.IsNullOrEmpty(selector(m)))
            .Select(m => (Text: selector(m)!, Rating: m.Rating!.Value))
            .ToList();

        foreach (var person in top)
        {
            person.AvgRating = rated
                .Where(x => x.Text.Contains(person.Name))
                .Select(x => x.Rating)
                .DefaultIfEmpty(0)
                .Average();
        }

        return top;
    }

    /// <summary>类型（标签）分布：直接按标签聚合，避免加载整个 MovieTags 集合。</summary>
    /// <remarks>
    /// 并列（Count 相同）时以 Name 作为次级排序键，使结果确定且可复现。
    /// 原实现依赖 movies 的遍历序，顺序是偶然的；SQL 聚合则依赖表物理行序。
    /// 注意：此处不能传 StringComparer（EF 无法翻译成 SQL）。SQL 侧的 ORDER BY 在
    /// SQLite 上即 BINARY 排序（等价于 Ordinal），参考实现已用 StringComparer.Ordinal 对齐。
    /// </remarks>
    private async Task<List<GenreStat>> GetGenreDistributionAsync()
    {
        var stats = await _context.MovieTags
            .AsNoTracking()
            .GroupBy(mt => mt.Tag.Name)
            .Select(g => new GenreStat { Name = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Name)
            .Take(15)
            .ToListAsync();

        // EF 无法翻译带 comparer 的 OrderBy（InMemory/SQLite 的排序语义也不同），
        // 因此在内存中按 Ordinal 复核一次，确保两种 provider 下并列顺序完全一致。
        return stats.OrderByDescending(g => g.Count)
                    .ThenBy(g => g.Name, StringComparer.Ordinal)
                    .ToList();
    }

    public async Task<List<CategoryStat>> GetCategoryDistributionAsync()
    {
        // 下推到 SQL：原实现加载全部 Movie 实体（含 PosterData）只为做一次 GroupBy。
        // 并列时以 Name 作次级排序键，保证结果确定且可复现（理由同 GetGenreDistributionAsync）。
        var stats = await _context.Movies
            .AsNoTracking()
            .Where(m => m.Category != null)
            .GroupBy(m => m.Category!.Name)
            .Select(g => new CategoryStat { Name = g.Key, Count = g.Count() })
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.Name)
            .ToListAsync();

        // 同 GetGenreDistributionAsync：按 Ordinal 复核，消除 provider 间排序语义差异。
        return stats.OrderByDescending(c => c.Count)
                    .ThenBy(c => c.Name, StringComparer.Ordinal)
                    .ToList();
    }

    public async Task<List<RatingStat>> GetRatingDistributionAsync()
    {
        var counts = await _context.Movies
            .AsNoTracking()
            .Where(m => m.Rating.HasValue && m.Rating.Value >= 1 && m.Rating.Value <= 10)
            .GroupBy(m => m.Rating!.Value)
            .Select(g => new { Rating = g.Key, Count = g.Count() })
            .ToListAsync();

        return Enumerable.Range(1, 10)
            .Select(r => new RatingStat
            {
                Rating = r,
                Count = counts.FirstOrDefault(c => c.Rating == r)?.Count ?? 0
            })
            .Where(r => r.Count > 0)
            .ToList();
    }

    public async Task<List<YearlyStat>> GetYearlyStatsAsync()
    {
        return await _context.Movies
            .AsNoTracking()
            .GroupBy(m => m.Year)
            .Select(g => new YearlyStat
            {
                Year = g.Key,
                AddedCount = g.Count(),
                WatchedCount = g.Count(m => m.WatchStatus == WatchStatus.Watched)
            })
            .OrderBy(y => y.Year)
            .ToListAsync();
    }

    public async Task<List<MonthlyStat>> GetMonthlyWatchStatsAsync(int year)
    {
        var counts = await _context.Movies
            .AsNoTracking()
            .Where(m => m.WatchDate.HasValue && m.WatchDate.Value.Year == year)
            .GroupBy(m => m.WatchDate!.Value.Month)
            .Select(g => new { Month = g.Key, Count = g.Count() })
            .ToListAsync();

        return Enumerable.Range(1, 12)
            .Select(m => new MonthlyStat
            {
                Year = year,
                Month = m,
                WatchedCount = counts.FirstOrDefault(c => c.Month == m)?.Count ?? 0
            })
            .ToList();
    }

    /// <summary>
    /// 统计专用的窄投影行：只包含聚合所需字段，不含 PosterData（占全库 99.4%）等大字段。
    /// </summary>
    internal sealed class MovieStatRow
    {
        public int Id { get; set; }
        public int Year { get; set; }
        public int? Rating { get; set; }
        public int? Runtime { get; set; }
        public string? Director { get; set; }
        public string? Cast { get; set; }
        public string? Country { get; set; }
        public WatchStatus WatchStatus { get; set; }
        public bool IsFavorite { get; set; }
        public DateTime? WatchDate { get; set; }
        public int? CategoryId { get; set; }
    }

    internal sealed class WatchLogStatRow
    {
        public int MovieId { get; set; }
        public DateTime WatchDate { get; set; }
    }
}
