using System.Security.Cryptography;
using EasyMovie.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EasyMovie.Tests.Core.Tests;

/// <summary>
/// 数据库文件级维护的行为锁定测试：WAL 备份完整性、挂起恢复、坏备份中止、flag 清理。
/// 全部在 %TEMP% 下的独立目录进行，显式传路径，不依赖 %LOCALAPPDATA% 与真实库。
/// </summary>
public class DatabaseMaintenanceTests : IDisposable
{
    private readonly string _dir;

    public DatabaseMaintenanceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "EasyMovieDbMaintenance", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* 清理失败不影响断言 */ }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private static SqliteConnection Open(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString());
        conn.Open();
        return conn;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void CreateBaselineDb(string path, string title)
    {
        using var conn = Open(path);
        Exec(conn, @"CREATE TABLE Movies (Id INTEGER PRIMARY KEY, Title TEXT);
                     CREATE TABLE Categories (Id INTEGER PRIMARY KEY, Name TEXT);
                     CREATE TABLE MovieTags (MovieId INTEGER, TagId INTEGER);");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Movies(Id,Title) VALUES(1,$t)";
        cmd.Parameters.AddWithValue("$t", title);
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(string path, string sql)
    {
        using var conn = Open(path);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static string IntegrityCheck(string path)
    {
        using var conn = Open(path);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        return (string)cmd.ExecuteScalar()!;
    }

    private static string Sha256(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    [Fact]
    public void CreateConsistentSnapshot_CapturesUncheckpointedWal_AndProducesSingleFile()
    {
        var src = P("src.db");
        var snapshot = P("snapshot.db");

        using (var conn = Open(src))
        {
            Exec(conn, "PRAGMA journal_mode=WAL;");
            Exec(conn, "PRAGMA wal_autocheckpoint=0;");
            Exec(conn, @"CREATE TABLE Movies (Id INTEGER PRIMARY KEY, Title TEXT);
                         CREATE TABLE Categories (Id INTEGER PRIMARY KEY, Name TEXT);
                         CREATE TABLE MovieTags (MovieId INTEGER, TagId INTEGER);");
            Exec(conn, "INSERT INTO Movies(Id,Title) VALUES(1,'wal-only')");

            // 该行仍只存在于 -wal 中，主库文件尚未包含它
            DatabaseMaintenance.CreateConsistentSnapshot(src, snapshot);
        }

        Assert.True(File.Exists(snapshot));
        Assert.False(File.Exists(snapshot + "-wal"));
        Assert.False(File.Exists(snapshot + "-shm"));
        Assert.Equal(1, Scalar(snapshot, "SELECT COUNT(*) FROM Movies WHERE Title='wal-only'"));
    }

    [Fact]
    public void ApplyPendingRestore_ReplacesWalDatabase_RemovesSidecars_CreatesRollback_ClearsFlags()
    {
        var main = P("main.db");
        var backup = P("backup.db");

        // 旧库：先建基础数据，再在关闭自动 checkpoint 的连接里写一行，使数据留在 -wal 中。
        CreateBaselineDb(main, "old");
        var preservedDb = P("preserved.db");
        var preservedWal = P("preserved.db-wal");
        using (var conn = Open(main))
        {
            Exec(conn, "PRAGMA journal_mode=WAL;");
            Exec(conn, "PRAGMA wal_autocheckpoint=0;");
            Exec(conn, "INSERT INTO Movies(Id,Title) VALUES(2,'wal-old')");
            File.Copy(main, preservedDb, overwrite: true);
            File.Copy(main + "-wal", preservedWal, overwrite: true);
        }
        // 关闭连接时 SQLite 已合并并删除 -wal；还原副本以模拟"带未 checkpoint WAL 的旧库"
        File.Copy(preservedDb, main, overwrite: true);
        if (File.Exists(main + "-wal")) File.Delete(main + "-wal");
        File.Copy(preservedWal, main + "-wal", overwrite: true);
        if (File.Exists(main + "-shm")) File.Delete(main + "-shm");

        // 备份（恢复源）
        CreateBaselineDb(backup, "new");
        var pending = DatabaseMaintenance.GetPendingRestorePath(main);
        File.Copy(backup, pending, overwrite: true);

        // 初始化/迁移 flag：应被清理；观看状态迁移 flag 不应被误删（并不可幂等）
        foreach (var flag in DatabaseMaintenance.InitializationFlagFiles)
            File.WriteAllText(P(flag), "x");
        var watchStatusFlag = P(".watchstatus_migrated_v3");
        File.WriteAllText(watchStatusFlag, "x");

        var rollback = P("rollback.db");
        var applied = DatabaseMaintenance.ApplyPendingRestore(main, pending, rollback);

        Assert.True(applied);
        Assert.Equal(1, Scalar(main, "SELECT COUNT(*) FROM Movies WHERE Title='new'"));
        Assert.Equal(0, Scalar(main, "SELECT COUNT(*) FROM Movies WHERE Title='old'"));
        Assert.False(File.Exists(main + "-wal"));
        Assert.False(File.Exists(main + "-shm"));
        Assert.False(File.Exists(pending));
        Assert.True(File.Exists(rollback));
        Assert.Equal("ok", IntegrityCheck(main));

        foreach (var flag in DatabaseMaintenance.InitializationFlagFiles)
            Assert.False(File.Exists(P(flag)));
        Assert.True(File.Exists(watchStatusFlag));
    }

    [Fact]
    public void ApplyPendingRestore_NoPendingFile_ReturnsFalse()
    {
        var main = P("main.db");
        CreateBaselineDb(main, "keep");

        Assert.False(DatabaseMaintenance.ApplyPendingRestore(main));
    }

    [Fact]
    public void ApplyPendingRestore_InvalidBackup_Throws_AndLeavesMainDbUntouched()
    {
        var main = P("main.db");
        CreateBaselineDb(main, "keep");
        var before = Sha256(main);

        var pending = DatabaseMaintenance.GetPendingRestorePath(main);
        File.WriteAllBytes(pending, new byte[2048]); // 没有 SQLite 页头

        Assert.Throws<InvalidDataException>(() => DatabaseMaintenance.ApplyPendingRestore(main));

        Assert.Equal(before, Sha256(main));
        Assert.Empty(Directory.GetFiles(_dir, "*.pre-restore-*"));
        Assert.False(File.Exists(P("rollback.db")));
    }
}
