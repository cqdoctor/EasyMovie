namespace EasyMovie.Core.Interfaces;

/// <summary>
/// 统计数据 DTO
/// </summary>
public class StatisticsData
{
    public int TotalMovies { get; set; }
    public int WantToWatch { get; set; }
    public int NotWatched { get; set; }
    public int Watched { get; set; }
    public int Favorites { get; set; }
    public double AverageRating { get; set; }
    public int RatedCount { get; set; }
    public int TotalRuntimeMinutes { get; set; }
    public List<CategoryStat> CategoryStats { get; set; } = new();
    public List<RatingStat> RatingStats { get; set; } = new();
    public List<YearlyStat> YearlyStats { get; set; } = new();
    public List<MonthlyStat> MonthlyStats { get; set; } = new();
    public List<PersonStat> DirectorStats { get; set; } = new();
    public List<PersonStat> CastStats { get; set; } = new();
    public List<CountryStat> CountryStats { get; set; } = new();
    public List<RuntimeRangeStat> RuntimeStats { get; set; } = new();
    public List<GenreStat> GenreStats { get; set; } = new();
    public double CompletionRate { get; set; }
    public int ThisYearWatchedCount { get; set; }
    public int ThisYearWatchedRuntimeMinutes { get; set; }
    public List<DayOfWeekStat> DayOfWeekStats { get; set; } = new();
    public int LongestWatchStreak { get; set; }
}

public class CategoryStat
{
    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class RatingStat
{
    public int Rating { get; set; }
    public int Count { get; set; }
}

public class YearlyStat
{
    public int Year { get; set; }
    public int AddedCount { get; set; }
    public int WatchedCount { get; set; }
}

public class MonthlyStat
{
    public int Year { get; set; }
    public int Month { get; set; }
    public int WatchedCount { get; set; }
    public string Label => $"{Year}-{Month:D2}";
}

public class PersonStat
{
    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
    public double AvgRating { get; set; }
}

public class CountryStat
{
    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class RuntimeRangeStat
{
    public string Label { get; set; } = string.Empty;
    public int MinMinutes { get; set; }
    public int MaxMinutes { get; set; }
    public int Count { get; set; }
}

public class GenreStat
{
    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class DayOfWeekStat
{
    public int DayOfWeek { get; set; }
    public string DayName { get; set; } = string.Empty;
    public int Count { get; set; }
}

/// <summary>
/// 统计服务接口
/// </summary>
public interface IStatisticsService
{
    Task<StatisticsData> GetStatisticsAsync();
    Task<List<CategoryStat>> GetCategoryDistributionAsync();
    Task<List<RatingStat>> GetRatingDistributionAsync();
    Task<List<YearlyStat>> GetYearlyStatsAsync();
    Task<List<MonthlyStat>> GetMonthlyWatchStatsAsync(int year);
}
