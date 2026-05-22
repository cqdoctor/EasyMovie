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
            Watching = movies.Count(m => m.WatchStatus == WatchStatus.Watching),
            Watched = movies.Count(m => m.WatchStatus == WatchStatus.Watched),
            Favorites = movies.Count(m => m.IsFavorite),
            RatedCount = movies.Count(m => m.Rating.HasValue),
            AverageRating = movies.Where(m => m.Rating.HasValue)
                .Select(m => m.Rating!.Value)
                .DefaultIfEmpty(0)
                .Average()
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
