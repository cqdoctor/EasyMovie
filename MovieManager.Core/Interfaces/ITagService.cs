using MovieManager.Core.Models;

namespace MovieManager.Core.Interfaces;

/// <summary>
/// 标签业务服务接口
/// </summary>
public interface ITagService
{
    Task<Tag?> GetByIdAsync(int id);
    Task<List<Tag>> GetAllAsync();
    Task<Tag> AddAsync(Tag tag);
    Task<Tag> UpdateAsync(Tag tag);
    Task<bool> DeleteAsync(int id);
    Task<List<Tag>> GetTagsForMovieAsync(int movieId);
    Task<int> GetMovieCountAsync(int tagId);
}
