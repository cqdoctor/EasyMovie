using EasyMovie.Core.Enums;
using EasyMovie.Core.Models;

namespace EasyMovie.Core.Interfaces;

/// <summary>
/// 电影业务服务接口
/// </summary>
public interface IMovieService
{
    Task<Movie?> GetByIdAsync(int id);
    Task<List<Movie>> GetAllAsync();
    Task<(List<Movie> Movies, int TotalCount)> SearchAsync(
        string? keyword, int? categoryId, List<int>? tagIds,
        int? yearFrom, int? yearTo, int? ratingMin, int? ratingMax, WatchStatus? status,
        List<string>? countries, List<string>? languages, int? runtimeMin, int? runtimeMax, List<string>? directors,
        string? sortBy, bool sortDesc, int page, int pageSize, bool? isFavorite = null);
    Task<Movie> AddAsync(Movie movie);
    Task<Movie> UpdateAsync(Movie movie);
    Task<bool> DeleteAsync(int id);
    /// <summary>只取文件路径（窄查询，不读 PosterData）。见 <see cref="IMovieRepository.GetFilePathAsync"/>。</summary>
    Task<string?> GetFilePathAsync(int id);
    Task<bool> SetRatingAsync(int movieId, int? rating);
    Task<bool> SetWatchStatusAsync(int movieId, WatchStatus status, DateTime? watchDate);
    Task<bool> ToggleFavoriteAsync(int movieId);
    Task<bool> UpdateNotesAsync(int movieId, string? notes);
    Task<bool> SetCategoryAsync(int movieId, int? categoryId);
    Task SetTagsAsync(int movieId, List<int> tagIds);
    Task<int> GetTotalCountAsync();

    /// <summary>按文件路径判重（窄查询，不物化实体）。见 <see cref="IMovieRepository.ExistsByFilePathAsync"/>。</summary>
    Task<bool> ExistsByFilePathAsync(string filePath);
}
