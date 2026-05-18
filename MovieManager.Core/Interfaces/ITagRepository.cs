using MovieManager.Core.Models;

namespace MovieManager.Core.Interfaces;

/// <summary>
/// 标签仓储接口
/// </summary>
public interface ITagRepository
{
    Task<Tag?> GetByIdAsync(int id);
    Task<List<Tag>> GetAllAsync();
    Task<List<Tag>> GetByIdsAsync(List<int> ids);
    Task<Tag> AddAsync(Tag tag);
    Task<Tag> UpdateAsync(Tag tag);
    Task<bool> DeleteAsync(int id);
    Task<bool> ExistsAsync(int id);
    Task<List<Tag>> GetTagsForMovieAsync(int movieId);
    Task AddMovieTagsAsync(int movieId, List<int> tagIds);
    Task RemoveMovieTagsAsync(int movieId, List<int> tagIds);
    Task<int> GetMovieCountAsync(int tagId);
}
