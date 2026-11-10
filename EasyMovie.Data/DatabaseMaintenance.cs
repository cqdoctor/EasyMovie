using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Serilog;

namespace EasyMovie.Data;

/// <summary>
/// 数据库文件级维护：一致性备份、备份校验、启动时应用挂起的恢复。
///
/// **为什么需要本类**：主库处于 WAL 模式（见 <see cref="BusyTimeoutInterceptor"/>），
/// 直接 File.Copy 主库会漏掉尚未 checkpoint 的 -wal 内容；而在应用运行中用备份覆盖主库，
/// 会留下旧的 -wal/-shm 被 SQLite 回放，造成"恢复后数据错乱"。
/// 因此备份统一走 <c>VACUUM INTO</c> 生成单文件一致性快照，恢复统一走
/// "校验 → 落 restore-pending → 下次启动、任何连接建立之前原子替换"。
/// </summary>
public static class DatabaseMaintenance
{
    /// <summary>挂起恢复文件的固定后缀（与主库同目录，保证同一卷上可原子替换）。</summary>
    public const string PendingRestoreSuffix = ".restore-pending";

    /// <summary>
    /// 恢复后需要清理的初始化/迁移标志（与 DbHelper 中的常量一一对应）。
    ///
    /// **刻意不含 <c>.watchstatus_migrated_v3</c>**：该迁移内容是
    /// <c>UPDATE Movies SET WatchStatus=2 WHERE WatchStatus=1</c>，
    /// 而当前枚举 WantToWatch=1、Watched=2 —— 重跑会把所有"想看"改成"已看"，
    /// 并不可幂等。恢复库来自同一版本，观看状态已是新口径，无需重跑。
    /// </summary>
    public static readonly IReadOnlyList<string> InitializationFlagFiles = new[]
    {
        ".db_initialized_v1",
        ".html_cleaned_v3",
        ".dirty_data_cleaned_v2",
        ".rating_backfilled_v1",
        ".tags_seeded_v2"
    };

    private static readonly byte[] SqliteHeader =
        System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0");

    /// <summary>挂起恢复文件的路径（与主库同目录）。</summary>
    public static string GetPendingRestorePath(string dbPath) => dbPath + PendingRestoreSuffix;

    /// <summary>恢复前旧库的回滚副本路径。</summary>
    public static string GetRollbackPath(string dbPath, DateTime timestamp)
        => $"{dbPath}.pre-restore-{timestamp:yyyyMMdd_HHmmss}";

    /// <summary>
    /// 用 <c>VACUUM INTO</c> 生成一致性快照。
    /// 相比 File.Copy：会读取 WAL 中已提交但未 checkpoint 的事务，且产出的是单文件（不含 -wal/-shm）。
    /// </summary>
    public static void CreateConsistentSnapshot(string sourceDbPath, string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(sourceDbPath))
            throw new ArgumentException("数据库路径不能为空", nameof(sourceDbPath));
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentException("备份路径不能为空", nameof(destinationPath));
        if (!File.Exists(sourceDbPath))
            throw new FileNotFoundException("数据库文件不存在", sourceDbPath);

        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // VACUUM INTO 要求目标文件不存在
        TryDelete(destinationPath);
        DeleteSidecars(destinationPath);

        using var conn = Open(sourceDbPath, SqliteOpenMode.ReadWrite);
        using var cmd = conn.CreateCommand();
        // 目标路径用字面量拼入：VACUUM 语句不支持参数绑定
        cmd.CommandText = $"VACUUM INTO '{destinationPath.Replace("'", "''")}';";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 校验备份文件：页头 + <c>PRAGMA quick_check</c> + 必需表。
    /// 任一项不通过直接抛 <see cref="InvalidDataException"/>，调用方据此中止恢复。
    /// </summary>
    public static void ValidateBackup(string backupPath)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
            throw new ArgumentException("备份路径不能为空", nameof(backupPath));
        if (!File.Exists(backupPath))
            throw new FileNotFoundException("备份文件不存在", backupPath);
        if (new FileInfo(backupPath).Length < 512)
            throw new InvalidDataException("备份文件过小，不是有效的数据库");

        using (var fs = new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var header = new byte[SqliteHeader.Length];
            var read = fs.Read(header, 0, header.Length);
            if (read < header.Length || !header.AsSpan().SequenceEqual(SqliteHeader))
                throw new InvalidDataException("备份文件不是有效的 SQLite 数据库（页头校验失败）");
        }

        // 以读写方式打开：备份若为 WAL 模式，只读打开在缺少 -shm 时可能直接无法打开。
        // 校验过程不写入数据，关闭后清理由本次打开新建的 -wal/-shm。
        var walExisted = File.Exists(backupPath + "-wal");
        var shmExisted = File.Exists(backupPath + "-shm");
        try
        {
            using var conn = Open(backupPath, SqliteOpenMode.ReadWrite);

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA quick_check;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var value = reader.GetString(0);
                    if (!string.Equals(value, "ok", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"备份完整性校验失败: {value}");
                }
            }

            foreach (var table in new[] { "Movies", "Categories", "MovieTags" })
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name";
                cmd.Parameters.AddWithValue("$name", table);
                if (cmd.ExecuteScalar() is null)
                    throw new InvalidDataException($"备份缺少必需表: {table}");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (!walExisted) TryDelete(backupPath + "-wal");
            if (!shmExisted) TryDelete(backupPath + "-shm");
        }
    }

    /// <summary>
    /// 校验备份并落盘为 <c>&lt;主库&gt;.restore-pending</c>，不触碰当前主库。
    /// 恢复将在下次启动、任何数据库连接建立之前由 <see cref="ApplyPendingRestore"/> 执行。
    /// </summary>
    public static void StagePendingRestore(string backupPath, string dbPath)
    {
        ValidateBackup(backupPath);

        if (string.IsNullOrWhiteSpace(dbPath))
            throw new ArgumentException("数据库路径不能为空", nameof(dbPath));

        var pendingPath = GetPendingRestorePath(dbPath);
        var dir = Path.GetDirectoryName(pendingPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        TryDelete(pendingPath);
        DeleteSidecars(pendingPath);
        File.Copy(backupPath, pendingPath, overwrite: true);
    }

    /// <summary>
    /// 应用挂起的恢复。返回 false 表示没有待恢复文件。
    ///
    /// 顺序：校验 pending → 清连接池 → 对当前库 checkpoint(TRUNCATE) → 删 -wal/-shm →
    /// 原子替换（保留回滚副本）→ 清理初始化 flag。
    /// 校验或 checkpoint 失败会抛异常且**不触碰主库**；替换失败时主库仍是已 checkpoint 的完整旧库。
    /// </summary>
    /// <param name="dbPath">主库路径。</param>
    /// <param name="pendingPath">待恢复文件路径；默认取主库同目录的 .restore-pending。</param>
    /// <param name="rollbackPath">旧库回滚副本路径；默认带时间戳。</param>
    public static bool ApplyPendingRestore(string dbPath, string? pendingPath = null, string? rollbackPath = null)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
            throw new ArgumentException("数据库路径不能为空", nameof(dbPath));

        pendingPath ??= GetPendingRestorePath(dbPath);
        if (!File.Exists(pendingPath)) return false;

        // 1) 先校验：坏备份在这里中止，主库分毫未动
        ValidateBackup(pendingPath);

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // 2) 关闭连接池，保证后续能删除 -wal/-shm
        SqliteConnection.ClearAllPools();

        // 3) 把当前库 WAL 中的已提交内容并回主库，随后才能安全删除 -wal/-shm。
        //    checkpoint 失败（例如仍有连接持锁）必须中止：此时删除侧车文件会丢数据。
        if (File.Exists(dbPath))
        {
            try
            {
                using var conn = Open(dbPath, SqliteOpenMode.ReadWrite);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("当前数据库无法完成 WAL checkpoint，已中止恢复", ex);
            }
            SqliteConnection.ClearAllPools();
            DeleteSidecars(dbPath);
        }

        // 4) 原子替换，并保留旧库回滚副本
        rollbackPath ??= GetRollbackPath(dbPath, DateTime.Now);
        if (File.Exists(dbPath))
            File.Replace(pendingPath, dbPath, rollbackPath);
        else
            File.Move(pendingPath, dbPath);

        // 5) 防御性清理：替换后不应存在任何旧侧车文件
        DeleteSidecars(dbPath);
        DeleteSidecars(pendingPath);

        // 6) 让 schema 补齐与迁移在恢复库上重跑（幂等部分）
        ClearInitializationFlags(dir);

        return true;
    }

    /// <summary>删除数据库初始化/迁移标志文件，返回删除数量。</summary>
    public static int ClearInitializationFlags(string? dbDirectory)
    {
        if (string.IsNullOrEmpty(dbDirectory) || !Directory.Exists(dbDirectory)) return 0;

        var removed = 0;
        foreach (var name in InitializationFlagFiles)
        {
            var path = Path.Combine(dbDirectory, name);
            try
            {
                if (!File.Exists(path)) continue;
                File.Delete(path);
                removed++;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "清理数据库初始化标志失败: {Path}", path);
            }
        }
        return removed;
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
            DefaultTimeout = 30
        };
        var conn = new SqliteConnection(builder.ToString());
        conn.Open();
        return conn;
    }

    private static void DeleteSidecars(string dbPath)
    {
        TryDelete(dbPath + "-wal");
        TryDelete(dbPath + "-shm");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Log.Warning(ex, "删除文件失败: {Path}", path); }
    }
}
