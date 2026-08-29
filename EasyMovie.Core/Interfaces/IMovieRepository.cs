using EasyMovie.Core.Enums;
using EasyMovie.Core.Models;

namespace EasyMovie.Core.Interfaces;

/// <summary>
/// 电影仓储接口
/// </summary>
public interface IMovieRepository
{
    Task<Movie?> GetByIdAsync(int id);
    Task<List<Movie>> GetAllAsync();

    /// <summary>
    /// 按 Id 批量取完整实体（含海报 PosterData 与分类/标签导航属性）。
    /// 用于「先用窄投影算出结果，再只加载真正要展示的少数几部」的两阶段查询模式——
    /// 推荐最终只展示 20 部，没必要为挑这 20 部而把全库海报读进内存。
    /// </summary>
    Task<List<Movie>> GetByIdsAsync(IEnumerable<int> ids);

    /// <summary>
    /// 推荐专用：一次性取回算法所需的全部轻量数据（不含海报、不做导航 JOIN）。
    /// </summary>
    Task<RecommendationData> GetRecommendationDataAsync();

    Task<List<Movie>> SearchAsync(string? keyword, int? categoryId, List<int>? tagIds,
        int? yearFrom, int? yearTo, int? ratingMin, int? ratingMax, WatchStatus? status,
        List<string>? countries, List<string>? languages, int? runtimeMin, int? runtimeMax, List<string>? directors,
        string? sortBy, bool sortDesc, int skip, int take, bool? isFavorite = null);
    Task<int> CountAsync(string? keyword, int? categoryId, List<int>? tagIds,
        int? yearFrom, int? yearTo, int? ratingMin, int? ratingMax, WatchStatus? status,
        List<string>? countries, List<string>? languages, int? runtimeMin, int? runtimeMax, List<string>? directors,
        bool? isFavorite = null);
    Task<Movie> AddAsync(Movie movie);
    Task<Movie> UpdateAsync(Movie movie);
    Task<bool> DeleteAsync(int id);
    Task<bool> ExistsAsync(int id);
}
