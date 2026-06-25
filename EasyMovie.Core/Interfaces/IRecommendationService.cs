using EasyMovie.Core.Models;

namespace EasyMovie.Core.Interfaces;

/// <summary>
/// 电影推荐服务接口
/// </summary>
public interface IRecommendationService
{
    /// <summary>获取综合推荐（同导演高分 + 同类型高分 + 高分未看）</summary>
    Task<List<RecommendedMovie>> GetRecommendationsAsync(int topN = 20);

    /// <summary>获取同导演高分电影</summary>
    Task<List<RecommendedMovie>> GetBySameDirectorAsync(int topN = 10);

    /// <summary>获取同类型高分电影</summary>
    Task<List<RecommendedMovie>> GetBySameCategoryAsync(int topN = 10);

    /// <summary>获取高分未看电影</summary>
    Task<List<RecommendedMovie>> GetHighRatedUnwatchedAsync(int topN = 10);
}

/// <summary>推荐电影项</summary>
public class RecommendedMovie
{
    public Movie Movie { get; set; } = null!;
    public string Reason { get; set; } = "";
    public double Score { get; set; }
}
