using Microsoft.EntityFrameworkCore;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;

namespace EasyMovie.Data.Repositories;

public class TagRepository : ITagRepository
{
    private readonly MovieDbContext _context;

    public TagRepository(MovieDbContext context)
    {
        _context = context;
    }

    public async Task<Tag?> GetByIdAsync(int id)
    {
        return await _context.Tags.FindAsync(id);
    }

    public async Task<List<Tag>> GetAllAsync()
    {
        return await _context.Tags
            .OrderBy(t => t.Name)
            .ToListAsync();
    }

    public async Task<List<Tag>> GetByIdsAsync(List<int> ids)
    {
        return await _context.Tags
            .Where(t => ids.Contains(t.Id))
            .ToListAsync();
    }

    public async Task<Tag> AddAsync(Tag tag)
    {
        tag.CreatedAt = DateTime.UtcNow;
        _context.Tags.Add(tag);
        await _context.SaveChangesAsync();
        return tag;
    }

    public async Task<Tag> UpdateAsync(Tag tag)
    {
        _context.Tags.Update(tag);
        await _context.SaveChangesAsync();
        return tag;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var tag = await _context.Tags.FindAsync(id);
        if (tag == null) return false;
        _context.Tags.Remove(tag);
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ExistsAsync(int id)
    {
        return await _context.Tags.AnyAsync(t => t.Id == id);
    }

    public async Task<List<Tag>> GetTagsForMovieAsync(int movieId)
    {
        return await _context.MovieTags
            .Where(mt => mt.MovieId == movieId)
            .Select(mt => mt.Tag)
            .ToListAsync();
    }

    public async Task AddMovieTagsAsync(int movieId, List<int> tagIds)
    {
        var existing = await _context.MovieTags
            .Where(mt => mt.MovieId == movieId)
            .Select(mt => mt.TagId)
            .ToListAsync();

        var toAdd = tagIds
            .Except(existing)
            .Select(tagId => new MovieTag { MovieId = movieId, TagId = tagId });

        _context.MovieTags.AddRange(toAdd);
        await _context.SaveChangesAsync();
    }

    public async Task RemoveMovieTagsAsync(int movieId, List<int> tagIds)
    {
        var toRemove = await _context.MovieTags
            .Where(mt => mt.MovieId == movieId && tagIds.Contains(mt.TagId))
            .ToListAsync();

        _context.MovieTags.RemoveRange(toRemove);
        await _context.SaveChangesAsync();
    }

    public async Task<int> GetMovieCountAsync(int tagId)
    {
        return await _context.MovieTags.CountAsync(mt => mt.TagId == tagId);
    }
}
