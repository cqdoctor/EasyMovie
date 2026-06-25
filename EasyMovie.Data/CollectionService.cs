using EasyMovie.Core.Models;
using EasyMovie.Data;
using Microsoft.EntityFrameworkCore;

namespace EasyMovie.Data;

public class CollectionService
{
    private readonly MovieDbContext _context;

    public CollectionService(MovieDbContext context)
    {
        _context = context;
    }

    public async Task<List<MovieCollection>> GetAllAsync()
    {
        return await _context.Collections
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Name)
            .ToListAsync();
    }

    public async Task<MovieCollection?> GetByIdAsync(int id)
    {
        return await _context.Collections
            .Include(c => c.Movies.OrderBy(m => m.CollectionOrder).ThenBy(m => m.Year))
            .FirstOrDefaultAsync(c => c.Id == id);
    }

    public async Task<List<MovieCollection>> GetAllWithMoviesAsync()
    {
        return await _context.Collections
            .Include(c => c.Movies.OrderBy(m => m.CollectionOrder).ThenBy(m => m.Year))
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Name)
            .ToListAsync();
    }

    public async Task<MovieCollection> AddAsync(MovieCollection collection)
    {
        collection.CreatedAt = DateTime.UtcNow;
        collection.UpdatedAt = DateTime.UtcNow;
        _context.Collections.Add(collection);
        await _context.SaveChangesAsync();
        return collection;
    }

    public async Task<MovieCollection> UpdateAsync(MovieCollection collection)
    {
        collection.UpdatedAt = DateTime.UtcNow;
        _context.Collections.Update(collection);
        await _context.SaveChangesAsync();
        return collection;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var collection = await _context.Collections.FindAsync(id);
        if (collection == null) return false;
        _context.Collections.Remove(collection);
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task AddMovieToCollectionAsync(int collectionId, int movieId, int? order = null)
    {
        var movie = await _context.Movies.FindAsync(movieId);
        if (movie == null) return;
        movie.CollectionId = collectionId;
        movie.CollectionOrder = order;
        await _context.SaveChangesAsync();
    }

    public async Task RemoveMovieFromCollectionAsync(int movieId)
    {
        var movie = await _context.Movies.FindAsync(movieId);
        if (movie == null) return;
        movie.CollectionId = null;
        movie.CollectionOrder = null;
        await _context.SaveChangesAsync();
    }

    public async Task<MovieCollection> GetOrCreateByNameAsync(string name)
    {
        var existing = await _context.Collections.FirstOrDefaultAsync(c => c.Name == name);
        if (existing != null) return existing;
        var collection = new MovieCollection { Name = name, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _context.Collections.Add(collection);
        await _context.SaveChangesAsync();
        return collection;
    }
}
