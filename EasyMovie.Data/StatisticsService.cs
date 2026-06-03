using Microsoft.EntityFrameworkCore;
using EasyMovie.Core.Enums;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;

namespace EasyMovie.Data;

/// <summary>
/// 统计服务实现
/// </summary>
public class StatisticsService : IStatisticsService
{
    private readonly MovieDbContext _context;

    public StatisticsService(MovieDbContext context)
    {
        _context = context;
    }

    public async Task<StatisticsData> GetStatisticsAsync()
    {
        var movies = await _context.Movies
            .Include(m => m.Category)
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

        // 分类分布
        data.CategoryStats = movies
            .Where(m => m.Category != null)
            .GroupBy(m => m.Category!.Name)
            .Select(g => new CategoryStat { Name = g.Key, Count = g.Count() })
            .OrderByDescending(c => c.Count)
            .ToList();

        // 有电影但未分类的
        var uncategorized = movies.Count(m => m.CategoryId == null);
        if (uncategorized > 0)
            data.CategoryStats.Add(new CategoryStat { Name = "未分类", Count = uncategorized });

        // 评分分布
        data.RatingStats = Enumerable.Range(1, 10)
            .Select(r => new RatingStat
            {
                Rating = r,
                Count = movies.Count(m => m.Rating == r)
            })
            .Where(r => r.Count > 0)
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
        var thisYearMovies = movies.Where(m => m.WatchDate.HasValue && m.WatchDate.Value.Year == currentYear);
        data.MonthlyStats = Enumerable.Range(1, 12)
            .Select(m => new MonthlyStat
            {
                Year = currentYear,
                Month = m,
                WatchedCount = thisYearMovies.Count(x => x.WatchDate!.Value.Month == m)
            })
            .ToList();

        // 导演排行 Top 10
        data.DirectorStats = movies
            .Where(m => !string.IsNullOrEmpty(m.Director))
            .SelectMany(m => m.Director!.Split(new[] { ", ", "、", " / ", "/" }, StringSplitOptions.RemoveEmptyEntries))
            .Select(d => d.Trim())
            .Where(d => !string.IsNullOrEmpty(d))
            .GroupBy(d => d)
            .Select(g => new PersonStat
            {
                Name = g.Key,
                Count = g.Count(),
                AvgRating = movies
                    .Where(m => !string.IsNullOrEmpty(m.Director) && m.Director!.Contains(g.Key) && m.Rating.HasValue)
                    .Select(m => m.Rating!.Value)
                    .DefaultIfEmpty(0)
                    .Average()
            })
            .OrderByDescending(p => p.Count)
            .Take(10)
            .ToList();

        // 演员排行 Top 10
        data.CastStats = movies
            .Where(m => !string.IsNullOrEmpty(m.Cast))
            .SelectMany(m => m.Cast!.Split(new[] { ", ", "、", " / ", "/" }, StringSplitOptions.RemoveEmptyEntries))
            .Select(c => c.Trim())
            .Where(c => !string.IsNullOrEmpty(c))
            .GroupBy(c => c)
            .Select(g => new PersonStat
            {
                Name = g.Key,
                Count = g.Count(),
                AvgRating = movies
                    .Where(m => !string.IsNullOrEmpty(m.Cast) && m.Cast!.Contains(g.Key) && m.Rating.HasValue)
                    .Select(m => m.Rating!.Value)
                    .DefaultIfEmpty(0)
                    .Average()
            })
            .OrderByDescending(p => p.Count)
            .Take(10)
            .ToList();

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

        return data;
    }

    public async Task<List<CategoryStat>> GetCategoryDistributionAsync()
    {
        var movies = await _context.Movies.Include(m => m.Category).ToListAsync();

        return movies
            .Where(m => m.Category != null)
            .GroupBy(m => m.Category!.Name)
            .Select(g => new CategoryStat { Name = g.Key, Count = g.Count() })
            .OrderByDescending(c => c.Count)
            .ToList();
    }

    public async Task<List<RatingStat>> GetRatingDistributionAsync()
    {
        var movies = await _context.Movies.ToListAsync();

        return Enumerable.Range(1, 10)
            .Select(r => new RatingStat
            {
                Rating = r,
                Count = movies.Count(m => m.Rating == r)
            })
            .Where(r => r.Count > 0)
            .ToList();
    }

    public async Task<List<YearlyStat>> GetYearlyStatsAsync()
    {
        var movies = await _context.Movies.ToListAsync();

        return movies
            .GroupBy(m => m.Year)
            .Select(g => new YearlyStat
            {
                Year = g.Key,
                AddedCount = g.Count(),
                WatchedCount = g.Count(m => m.WatchStatus == WatchStatus.Watched)
            })
            .OrderBy(y => y.Year)
            .ToList();
    }

    public async Task<List<MonthlyStat>> GetMonthlyWatchStatsAsync(int year)
    {
        var movies = await _context.Movies
            .Where(m => m.WatchDate.HasValue && m.WatchDate.Value.Year == year)
            .ToListAsync();

        return Enumerable.Range(1, 12)
            .Select(m => new MonthlyStat
            {
                Year = year,
                Month = m,
                WatchedCount = movies.Count(x => x.WatchDate!.Value.Month == m)
            })
            .ToList();
    }
}
