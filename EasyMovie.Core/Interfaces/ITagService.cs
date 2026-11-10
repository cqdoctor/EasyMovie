using EasyMovie.Core.Models;

namespace EasyMovie.Core.Interfaces;

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

    /// <summary>
    /// 按名称取标签，不存在则创建。
    /// 自动打标链路专用：豆瓣/TMDB 返回的类型名需要幂等地落成 Tag 实体，
    /// 否则 MovieTags 关联建不出来（Category 侧已有对称的 GetOrCreateByNameAsync）。
    /// </summary>
    Task<Tag> GetOrCreateByNameAsync(string name);
}
