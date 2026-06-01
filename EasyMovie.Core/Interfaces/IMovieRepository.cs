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
