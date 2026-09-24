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

    /// <summary>
    /// 按文件路径判重（窄查询，不物化实体、不读 PosterData）。
    /// 单文件导入（文件夹监控）用它替代「把整行查出来再比较」。
    /// </summary>
    Task<bool> ExistsByFilePathAsync(string filePath);

    // —— 定点更新（ExecuteUpdate）：绕过「读整实体 → Update 全列」——
    //
    // 为什么不能用「GetByIdAsync + UpdateAsync」来做单列修改：
    //   · 读的那一下会把 86KB 海报拉进内存，并把这个实体钉在 ChangeTracker 上
    //     （PosterData 占库 99.4%，见 #影片库性能契约）；
    //   · UpdateAsync 走 Movies.Update() 会把**全部**列标脏，等于改个收藏也要把海报回写一遍；
    //   · 更危险的组合风险：一旦 GetByIdAsync 将来改成窄投影，这种写法会把没投影到的列写成 null。
    // 这组方法直接下发单列 UPDATE：不读实体、不加载海报、不触碰未改列。
    // UpdatedAt 由实现统一维护。返回值 false = 电影不存在（未命中任何行）。

    Task<bool> SetRatingAsync(int movieId, int? rating);
    Task<bool> SetWatchStatusAsync(int movieId, WatchStatus status, DateTime? watchDate);
    Task<bool> ToggleFavoriteAsync(int movieId);
    Task<bool> SetNotesAsync(int movieId, string? notes);
    Task<bool> SetCategoryIdAsync(int movieId, int? categoryId);
}
