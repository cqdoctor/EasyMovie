﻿using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using EasyMovie.Core.Models;
using EasyMovie.Data.Configurations;

namespace EasyMovie.Data;

public class MovieDbContext : DbContext
{
    public DbSet<Movie> Movies => Set<Movie>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<MovieTag> MovieTags => Set<MovieTag>();
    public DbSet<MovieCollection> Collections => Set<MovieCollection>();
    public DbSet<WatchLog> WatchLogs => Set<WatchLog>();

    public MovieDbContext(DbContextOptions<MovieDbContext> options) : base(options)
    {
    }

    // 进程级写串行化：后台导入线程（FolderWatcher）与 UI 线程会各自持有不同的 DbContext 实例并并发写
    // 同一 SQLite 文件，若不串行化会触发 "database is locked"。统一在 SaveChanges 入口加锁，确保任意时刻
    // 仅有一个写事务在进行。
    //
    // ⚠️ 不变量（改动前必读，2026-09-20 审计固化）：
    //   持锁期间**绝不允许阻塞式等待 UI 线程**（`Dispatcher.Invoke` / `.Wait()` / `.Result`）。
    //   反例链路：UI 线程调同步 SaveChanges → `_writeLock.Wait()` 阻塞；若此时持锁线程正 `Dispatcher.Invoke`
    //   回 UI 线程 → 双向等待，**硬死锁**（UI 永久卡死、无法恢复）。
    //   当前全仓已核对：没有任何持锁路径包含阻塞式 Dispatcher.Invoke（后台写库路径用的是非阻塞 `BeginInvoke`），
    //   故死锁暂不可达。新增"后台写库 + 界面更新"逻辑时，界面更新请一律用 `BeginInvoke`/`InvokeAsync`，
    //   或把写库放到 `await` 之后，切勿在锁内同步等 UI。
    private static readonly SemaphoreSlim _writeLock = new(1, 1);

    public override int SaveChanges()
    {
        _writeLock.Wait();
        try
        {
            return base.SaveChanges();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            return await base.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfiguration(new MovieConfiguration());
        modelBuilder.ApplyConfiguration(new CategoryConfiguration());
        modelBuilder.ApplyConfiguration(new TagConfiguration());
        modelBuilder.ApplyConfiguration(new MovieTagConfiguration());
        modelBuilder.ApplyConfiguration(new MovieCollectionConfiguration());
        modelBuilder.ApplyConfiguration(new WatchLogConfiguration());
    }
}
