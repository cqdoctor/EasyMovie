using System;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using EasyMovie.Core.Models;
using EasyMovie.Data;
using Serilog;

namespace EasyMovie.Client;

/// <summary>连接打开后执行 PRAGMA：启用 WAL 日志模式（读写并发不再互相排他锁，根治 "database is locked"），
/// 并设置 busy_timeout 兜底。WAL 模式是持久化的（写入 DB 头），只需在连接打开时设置一次。
/// 注意：Microsoft.Data.Sqlite 9.x 的连接串不支持 BusyTimeout/Busy Timeout 关键字，必须通过 PRAGMA 设置。</summary>
public sealed class BusyTimeoutInterceptor : DbConnectionInterceptor
{
    public static readonly BusyTimeoutInterceptor Instance = new();
    // journal_mode=WAL 是持久化的（写入 DB 文件头），只需首个连接设置一次；
    // 每个连接都执行会反复获取 SQLite 锁（启动期多连接并发打开时实测会显著拖慢）。
    private static int _walConfigured;
    private const string PragmaAll = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=3000;";
    private const string PragmaBusyOnly = "PRAGMA busy_timeout=3000;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => Execute(connection);

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        => Execute(connection);

    private static void Execute(DbConnection connection)
    {
        try
        {
            if (connection is SqliteConnection sqlite)
            {
                using var cmd = sqlite.CreateCommand();
                cmd.CommandText = Interlocked.Exchange(ref _walConfigured, 1) == 0 ? PragmaAll : PragmaBusyOnly;
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex) { Log.Warning(ex, "设置 busy_timeout PRAGMA 失败"); }
    }
}

public static class DbHelper
{
    private static readonly string DbDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyMovie");
    private static readonly string DbPath = Path.Combine(DbDir, "EasyMovie.db");

    private static readonly string OldDbDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MovieManager");
    private static readonly string OldDbPath = Path.Combine(OldDbDir, "MovieManager.db");

    private static readonly object _lock = new();
    private static bool _initialized;

    /// <summary>连接串：仅指定数据源。busy_timeout 由 BusyTimeoutInterceptor 在连接打开后通过 PRAGMA 设置。</summary>
    public static string ConnectionString => $"Data Source={DbPath}";

    /// <summary>构建带 BusyTimeout 拦截器的 DbContextOptions（统一所有 MovieDbContext 创建入口）。</summary>
    public static DbContextOptions<MovieDbContext> CreateOptions()
        => new DbContextOptionsBuilder<MovieDbContext>()
            .UseSqlite(ConnectionString)
            .AddInterceptors(BusyTimeoutInterceptor.Instance)
            .Options;

    public static MovieDbContext CreateContext()
    {
        // 注意：此处不再调用 EnsureInitialized()。数据库 schema 的首次初始化
        // （迁移 / 数据清洗 / 种子标签，实测首次约数十秒）较重，统一由 WarmupAsync()
        // 在后台线程负责，避免被 UI 线程创建 DbContext 时抢先触发、阻塞首帧渲染
        // 导致启动闪屏长时间不消失。启动期需要立即查询数据库的视图，应在查询前 await DbHelper.WarmupAsync()。
        if (!Directory.Exists(DbDir)) Directory.CreateDirectory(DbDir);
        var context = new MovieDbContext(CreateOptions());
        return context;
    }

    /// <summary>
    /// 在后台线程预热数据库（schema 迁移 / 数据清洗 / 种子标签，首次约数十秒）。
    /// 应在 App 启动最早阶段调用一次，使首次初始化重活在后台线程执行，
    /// 而非被 UI 线程创建 DbContext 时抢先触发、阻塞首帧渲染。
    /// 幂等：无论调用多少次，实际初始化只执行一次（内部 EnsureInitialized 自带 _initialized 标志与锁）。
    /// 任何启动期需立即查询数据库的视图，应在查询前 await 本方法。
    /// </summary>
    private static Task? _warmupTask;
    public static Task WarmupAsync()
    {
        if (_warmupTask == null)
        {
            lock (_lock)
            {
                _warmupTask ??= Task.Run(() =>
                {
                    try { EnsureInitialized(); }
                    catch (Exception ex) { Log.Error(ex, "数据库预热(EnsureInitialized)失败"); }
                });
            }
        }
        return _warmupTask;
    }

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_lock)
        {
            if (_initialized) return;

            // 非首次启动快速路径：首次完整初始化（EnsureCreated + schema 检查 + 历史数据清洗 +
            // 种子标签）完成后写 flag 文件；再次启动直接跳过全部重活（实测每次重跑约 2.2s，
            // 且持 SQLite 写锁，会把启动期 Dashboard 预载查询阻塞 busy_timeout 3s）。
            // flag 与 DB 一同生成，旧版本迁移（MigrateFromOldVersion）只发生一次，不受影响。
            if (File.Exists(InitFlagPath))
            {
                _initialized = true;
                App.LogStartup("数据库预热(EnsureInitialized)命中已初始化标记，跳过重活");
                return;
            }

            if (!Directory.Exists(DbDir)) Directory.CreateDirectory(DbDir);

            MigrateFromOldVersion();

            var options = DbHelper.CreateOptions();
            using var ctx = new MovieDbContext(options);
            ctx.Database.EnsureCreated();

            try
            {
                using var cmd = ctx.Database.GetDbConnection().CreateCommand();
                ctx.Database.OpenConnection();

                cmd.CommandText = "PRAGMA table_info(Movies)";
                var hasSearchIndex = false;
                var hasPosterData = false;
                var hasCollectionId = false;
                var hasCollectionOrder = false;
                var hasCollectionsTable = false;
                var hasPlaybackPosition = false;
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var colName = reader.GetString(1);
                        if (colName == "SearchIndex") hasSearchIndex = true;
                        if (colName == "PosterData") hasPosterData = true;
                        if (colName == "CollectionId") hasCollectionId = true;
                        if (colName == "CollectionOrder") hasCollectionOrder = true;
                        if (colName == "PlaybackPosition") hasPlaybackPosition = true;
                    }
                }

                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='Collections'";
                using (var tableReader = cmd.ExecuteReader())
                {
                    if (tableReader.Read()) hasCollectionsTable = true;
                }

                if (!hasCollectionsTable)
                {
                    cmd.CommandText = @"CREATE TABLE Collections (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Name TEXT NOT NULL,
                        Description TEXT,
                        SortOrder INTEGER NOT NULL DEFAULT 0,
                        CreatedAt TEXT NOT NULL,
                        UpdatedAt TEXT NOT NULL);";
                    cmd.ExecuteNonQuery();
                }

                if (!hasCollectionId)
                {
                    cmd.CommandText = "ALTER TABLE Movies ADD COLUMN CollectionId INTEGER REFERENCES Collections(Id) ON DELETE SET NULL;";
                    cmd.ExecuteNonQuery();
                }
                if (!hasCollectionOrder)
                {
                    cmd.CommandText = "ALTER TABLE Movies ADD COLUMN CollectionOrder INTEGER;";
                    cmd.ExecuteNonQuery();
                }

                if (!hasSearchIndex)
                {
                    cmd.CommandText = "ALTER TABLE Movies ADD COLUMN SearchIndex TEXT;";
                    cmd.ExecuteNonQuery();
                }
                if (!hasPosterData)
                {
                    cmd.CommandText = "ALTER TABLE Movies ADD COLUMN PosterData BLOB;";
                    cmd.ExecuteNonQuery();
                }

                if (!hasPlaybackPosition)
                {
                    cmd.CommandText = "ALTER TABLE Movies ADD COLUMN PlaybackPosition INTEGER NOT NULL DEFAULT 0;";
                    cmd.ExecuteNonQuery();
                }

                var hasWatchLogsTable = false;
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='WatchLogs'";
                using (var tableReader2 = cmd.ExecuteReader())
                {
                    if (tableReader2.Read()) hasWatchLogsTable = true;
                }

                if (!hasWatchLogsTable)
                {
                    cmd.CommandText = @"CREATE TABLE WatchLogs (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        MovieId INTEGER NOT NULL REFERENCES Movies(Id) ON DELETE CASCADE,
                        WatchDate TEXT NOT NULL,
                        Rating INTEGER,
                        Location TEXT,
                        Companion TEXT,
                        Notes TEXT,
                        CreatedAt TEXT NOT NULL);";
                    cmd.ExecuteNonQuery();
                }

                ctx.Database.CloseConnection();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "数据库 Schema 升级失败");
            }

            try
            {
                CleanHtmlInExistingData();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "清洗历史 HTML 脏数据失败");
            }

            try
            {
                CleanDirtyPersonData();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "清洗脏人名数据失败");
            }

            try
            {
                MigrateDefaultWatchStatus();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "迁移默认观看状态失败");
            }

            try
            {
                SeedDefaultTags();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "种子默认标签失败");
            }

            // 首次完整初始化完成，写 flag + 里程碑日志。
            // flag 写不进去的后果：每次启动都重跑一遍完整初始化（含迁移/清洗），启动会明显变慢——值得留痕。
            try { File.WriteAllText(InitFlagPath, DateTime.UtcNow.ToString("O")); }
            catch (Exception ex) { Log.Warning(ex, "写入数据库初始化标志失败: {Path}", InitFlagPath); }
            App.LogStartup("数据库预热(EnsureInitialized)完成(首次)");
            _initialized = true;
        }
    }

    private static readonly string InitFlagPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyMovie", ".db_initialized_v1");

    private static void MigrateFromOldVersion()
    {
        try
        {
            if (File.Exists(DbPath)) return;
            if (!File.Exists(OldDbPath)) return;

            if (!Directory.Exists(DbDir)) Directory.CreateDirectory(DbDir);
            File.Copy(OldDbPath, DbPath, overwrite: false);

            var oldSettings = Path.Combine(OldDbDir, "settings.json");
            var newSettings = Path.Combine(DbDir, "settings.json");
            if (File.Exists(oldSettings) && !File.Exists(newSettings))
                File.Copy(oldSettings, newSettings, overwrite: false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "从旧版本迁移数据库/配置失败");
        }
    }

    private static readonly string HtmlCleanFlagPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyMovie", ".html_cleaned_v2");

    private static readonly string DirtyDataFlagPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyMovie", ".dirty_data_cleaned_v2");

    private static void CleanHtmlInExistingData()
    {
        if (File.Exists(HtmlCleanFlagPath)) return;

        var options = DbHelper.CreateOptions();
        using var ctx = new MovieDbContext(options);

        // 只投影需要的文本字段，避免把 PosterData 等大 BLOB 整行加载进内存
        var rows = ctx.Movies
            .Select(m => new { m.Id, m.Synopsis, m.Director, m.Cast, m.Country, m.Notes })
            .AsNoTracking()
            .ToList();
        var changed = false;
        foreach (var row in rows)
        {
            var cleanSynopsis = StripHtml(row.Synopsis);
            var cleanDirector = StripHtml(row.Director);
            var cleanCast = StripHtml(row.Cast);
            var cleanCountry = StripHtml(row.Country);
            var cleanNotes = StripHtml(row.Notes);

            if (cleanSynopsis == row.Synopsis && cleanDirector == row.Director &&
                cleanCast == row.Cast && cleanCountry == row.Country && cleanNotes == row.Notes)
                continue;

            // 仅附载主键，按列标脏回写变化字段，绝不触碰 PosterData 等其它列
            var tracked = new Movie { Id = row.Id };
            ctx.Attach(tracked);
            if (cleanSynopsis != row.Synopsis)
            {
                ctx.Entry(tracked).Property(x => x.Synopsis).CurrentValue = cleanSynopsis;
                ctx.Entry(tracked).Property(x => x.Synopsis).IsModified = true;
            }
            if (cleanDirector != row.Director)
            {
                ctx.Entry(tracked).Property(x => x.Director).CurrentValue = cleanDirector;
                ctx.Entry(tracked).Property(x => x.Director).IsModified = true;
            }
            if (cleanCast != row.Cast)
            {
                ctx.Entry(tracked).Property(x => x.Cast).CurrentValue = cleanCast;
                ctx.Entry(tracked).Property(x => x.Cast).IsModified = true;
            }
            if (cleanCountry != row.Country)
            {
                ctx.Entry(tracked).Property(x => x.Country).CurrentValue = cleanCountry;
                ctx.Entry(tracked).Property(x => x.Country).IsModified = true;
            }
            if (cleanNotes != row.Notes)
            {
                ctx.Entry(tracked).Property(x => x.Notes).CurrentValue = cleanNotes;
                ctx.Entry(tracked).Property(x => x.Notes).IsModified = true;
            }
            changed = true;
        }
        if (changed) ctx.SaveChanges();

        File.WriteAllText(HtmlCleanFlagPath, DateTime.UtcNow.ToString("O"));
    }

    private static string? StripHtml(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var result = Regex.Replace(input, @"<[^>]+>", "");
        result = System.Net.WebUtility.HtmlDecode(result);
        result = Regex.Replace(result, @"\s+", " ").Trim();
        return string.IsNullOrEmpty(result) ? null : result;
    }

    private static readonly string[] InvalidPersonLabels = { "人员", "人物", "演员", "主演", "导演", "暂无", "未知", "暂未录入", "更多" };

    private static bool ContainsTemplateOrLabel(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        if (Regex.IsMatch(value, @"\$\{.*?\}|\$\(data\.\w+\)|\{\{.*?\}\}|<%.*?%>")) return true;
        if (InvalidPersonLabels.Contains(value.Trim())) return true;
        return false;
    }

    private static string? CleanPersonField(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (ContainsTemplateOrLabel(value)) return null;
        var parts = value.Split(new[] { ", ", "、", " / ", "/" }, StringSplitOptions.None)
            .Where(p => !ContainsTemplateOrLabel(p))
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrWhiteSpace(p));
        var cleaned = string.Join(", ", parts);
        return string.IsNullOrEmpty(cleaned) ? null : cleaned;
    }

    private static void CleanDirtyPersonData()
    {
        if (File.Exists(DirtyDataFlagPath)) return;

        var options = DbHelper.CreateOptions();
        using var ctx = new MovieDbContext(options);

        // 只投影需要的字段，避免加载 PosterData 等大 BLOB
        var rows = ctx.Movies
            .Select(m => new { m.Id, m.Director, m.Cast })
            .AsNoTracking()
            .ToList();
        var changed = false;
        foreach (var row in rows)
        {
            var cleanDirector = CleanPersonField(row.Director);
            var cleanCast = CleanPersonField(row.Cast);

            if (cleanDirector == row.Director && cleanCast == row.Cast) continue;

            // 仅附载主键，按列标脏回写变化字段，绝不触碰 PosterData 等其它列
            var tracked = new Movie { Id = row.Id };
            ctx.Attach(tracked);
            if (cleanDirector != row.Director)
            {
                ctx.Entry(tracked).Property(x => x.Director).CurrentValue = cleanDirector;
                ctx.Entry(tracked).Property(x => x.Director).IsModified = true;
            }
            if (cleanCast != row.Cast)
            {
                ctx.Entry(tracked).Property(x => x.Cast).CurrentValue = cleanCast;
                ctx.Entry(tracked).Property(x => x.Cast).IsModified = true;
            }
            changed = true;
        }
        if (changed) ctx.SaveChanges();

        File.WriteAllText(DirtyDataFlagPath, DateTime.UtcNow.ToString("O"));
    }

    private static readonly string WatchStatusMigratedPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyMovie", ".watchstatus_migrated_v3");

    private static void MigrateDefaultWatchStatus()
    {
        if (File.Exists(WatchStatusMigratedPath)) return;

        var options = DbHelper.CreateOptions();
        using var ctx = new MovieDbContext(options);

        ctx.Database.OpenConnection();
        using var cmd = ctx.Database.GetDbConnection().CreateCommand();
        // 旧 Watching(1) → Watched(2)
        cmd.CommandText = "UPDATE Movies SET WatchStatus = 2 WHERE WatchStatus = 1";
        cmd.ExecuteNonQuery();
        // 旧 WantToWatch(0) → NotWatched(0) (值不变，但含义变了)
        // 旧 NotWatched(3) → NotWatched(0)
        cmd.CommandText = "UPDATE Movies SET WatchStatus = 0 WHERE WatchStatus = 3";
        cmd.ExecuteNonQuery();
        // 没有观影记录的 Watched → NotWatched
        cmd.CommandText = "UPDATE Movies SET WatchStatus = 0 WHERE WatchStatus = 2 AND Id NOT IN (SELECT DISTINCT MovieId FROM WatchLogs WHERE MovieId IS NOT NULL)";
        cmd.ExecuteNonQuery();
        ctx.Database.CloseConnection();

        File.WriteAllText(WatchStatusMigratedPath, DateTime.UtcNow.ToString("O"));
    }

    private static readonly string SeedTagsFlagPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyMovie", ".tags_seeded_v2");

    private static void SeedDefaultTags()
    {
        var colors = new[] { "#F44336","#E91E63","#9C27B0","#673AB7","#3F51B5","#2196F3","#03A9F4","#00BCD4","#009688","#4CAF50","#8BC34A","#CDDC39","#FFEB3B","#FFC107","#FF9800","#FF5722","#795548","#607D8B" };
        var rng = new Random();

        var tags = new[] {
            "动作", "喜剧", "剧情", "科幻", "恐怖", "爱情", "悬疑", "惊悚",
            "动画", "冒险", "奇幻", "犯罪", "纪录片", "战争", "传记", "历史",
            "音乐", "家庭", "西部", "短片", "武侠", "古装", "灾难", "黑色幽默"
        };

        var options = DbHelper.CreateOptions();
        using var ctx = new MovieDbContext(options);

        // 添加缺失的类型标签（支持已有标签的情况）
        var existingNames = ctx.Tags.Select(t => t.Name).ToHashSet();
        bool added = false;
        foreach (var name in tags)
        {
            if (!existingNames.Contains(name))
            {
                ctx.Tags.Add(new EasyMovie.Core.Models.Tag
                {
                    Name = name,
                    Color = colors[rng.Next(colors.Length)]
                });
                added = true;
            }
        }
        if (added) ctx.SaveChanges();

        File.WriteAllText(SeedTagsFlagPath, DateTime.UtcNow.ToString("O"));
    }
}
