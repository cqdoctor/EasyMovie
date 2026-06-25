using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;

namespace EasyMovie.Core.Services;

/// <summary>
/// 标签业务服务
/// </summary>
public class TagService : ITagService
{
    private readonly ITagRepository _tagRepo;

    public TagService(ITagRepository tagRepo)
    {
        _tagRepo = tagRepo;
    }

    public async Task<Tag?> GetByIdAsync(int id)
    {
        return await _tagRepo.GetByIdAsync(id);
    }

    public async Task<List<Tag>> GetAllAsync()
    {
        return await _tagRepo.GetAllAsync();
    }

    public async Task<Tag> AddAsync(Tag tag)
    {
        if (string.IsNullOrWhiteSpace(tag.Name))
            throw new ArgumentException("标签名称不能为空");

        return await _tagRepo.AddAsync(tag);
    }

    public async Task<Tag> UpdateAsync(Tag tag)
    {
        if (string.IsNullOrWhiteSpace(tag.Name))
            throw new ArgumentException("标签名称不能为空");

        if (!await _tagRepo.ExistsAsync(tag.Id))
            throw new InvalidOperationException($"标签 ID {tag.Id} 不存在");

        return await _tagRepo.UpdateAsync(tag);
    }

    public async Task<bool> DeleteAsync(int id)
    {
        return await _tagRepo.DeleteAsync(id);
    }

    public async Task<List<Tag>> GetTagsForMovieAsync(int movieId)
    {
        return await _tagRepo.GetTagsForMovieAsync(movieId);
    }

    public async Task<int> GetMovieCountAsync(int tagId)
    {
        if (!await _tagRepo.ExistsAsync(tagId))
            throw new InvalidOperationException($"标签 ID {tagId} 不存在");

        return await _tagRepo.GetMovieCountAsync(tagId);
    }
}
