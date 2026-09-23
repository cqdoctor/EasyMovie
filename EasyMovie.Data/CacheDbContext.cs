using Microsoft.EntityFrameworkCore;

namespace EasyMovie.Data;

/// <summary>
/// 离线元数据缓存库（cache.db）。与业务主库（MovieDbContext）完全独立。
/// 通过 EnsureCreated 建表，无需迁移。
/// </summary>
public class CacheDbContext : DbContext
{
    public DbSet<CachedMovie> CachedMovies => Set<CachedMovie>();

    private static readonly SemaphoreSlim _writeLock = new(1, 1);

    public CacheDbContext(DbContextOptions<CacheDbContext> options) : base(options) { }

    public override int SaveChanges()
    {
        _writeLock.Wait();
        try { return base.SaveChanges(); }
        finally { _writeLock.Release(); }
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try { return await base.SaveChangesAsync(cancellationToken); }
        finally { _writeLock.Release(); }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfiguration(new CachedMovieConfiguration());
    }

    /// <summary>缓存库文件位置：与业务库同目录（%LocalAppData%/EasyMovie/cache.db）</summary>
    public static string DbPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyMovie", "cache.db");

    public static string ConnectionString => $"Data Source={DbPath}";

    public static DbContextOptions<CacheDbContext> CreateOptions()
        => new DbContextOptionsBuilder<CacheDbContext>()
            .UseSqlite(ConnectionString)
            // 与主库一致：启用 WAL + busy_timeout=3000。缓存库会被 GUI 进程与独立 BackfillRunner 进程
            // 并发读写，缺此项则默认 busy_timeout=0 → 撞锁立即 "database is locked"（无重试）。
            .AddInterceptors(BusyTimeoutInterceptor.Instance)
            .Options;

    public static CacheDbContext Create() => new(CreateOptions());
}
