using EasyMovie.Core.Models;
using EasyMovie.Data;
using Microsoft.EntityFrameworkCore;

namespace EasyMovie.Data;

public class WatchLogService
{
    private readonly MovieDbContext _context;

    public WatchLogService(MovieDbContext context)
    {
        _context = context;
    }

    public async Task<List<WatchLog>> GetByMovieIdAsync(int movieId)
    {
        return await _context.WatchLogs
            .Where(w => w.MovieId == movieId)
            .OrderByDescending(w => w.WatchDate)
            .ToListAsync();
    }

    public async Task<List<WatchLog>> GetAllWithMovieAsync(int skip = 0, int take = 50)
    {
        return await _context.WatchLogs
            .Include(w => w.Movie)
            .OrderByDescending(w => w.WatchDate)
            .Skip(skip)
            .Take(take)
            .ToListAsync();
    }

    public async Task<int> GetCountAsync()
    {
        return await _context.WatchLogs.CountAsync();
    }

    public async Task<WatchLog> AddAsync(WatchLog log)
    {
        log.CreatedAt = DateTime.UtcNow;
        _context.WatchLogs.Add(log);
        await _context.SaveChangesAsync();
        return log;
    }

    public async Task<WatchLog> UpdateAsync(WatchLog log)
    {
        _context.WatchLogs.Update(log);
        await _context.SaveChangesAsync();
        return log;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var log = await _context.WatchLogs.FindAsync(id);
        if (log == null) return false;
        _context.WatchLogs.Remove(log);
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<WatchLog?> GetByIdAsync(int id)
    {
        return await _context.WatchLogs
            .Include(w => w.Movie)
            .FirstOrDefaultAsync(w => w.Id == id);
    }

    public async Task<List<WatchLog>> GetByMonthAsync(int year, int month)
    {
        var start = new DateTime(year, month, 1);
        var end = start.AddMonths(1);
        return await _context.WatchLogs
            .Include(w => w.Movie)
            .Where(w => w.WatchDate >= start && w.WatchDate < end)
            .OrderBy(w => w.WatchDate)
            .ToListAsync();
    }
}
