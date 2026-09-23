using System;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Serilog;

namespace EasyMovie.Data;

/// <summary>
/// 连接打开后执行 PRAGMA：启用 WAL 日志模式（读写并发不再互相排他锁，根治 "database is locked"），
/// 并设置 busy_timeout 兜底。WAL 模式是持久化的（写入 DB 头），只需每个数据源在首个连接时设置一次。
/// 注意：Microsoft.Data.Sqlite 9.x 的连接串不支持 BusyTimeout/Busy Timeout 关键字，必须通过 PRAGMA 设置。
///
/// 位于 <b>Data 层</b>（原先放在 Client 层）：主库 MovieDbContext 与缓存库 CacheDbContext 都在本层，
/// 二者都需要 WAL + busy_timeout。缓存库长期漏配导致 GUI 进程与独立 BackfillRunner 进程并发读写
/// cache.db 时命中默认 busy_timeout=0 → 立即 "database is locked"（无重试）。下沉到 Data 后，两库统一接入。
/// </summary>
public sealed class BusyTimeoutInterceptor : DbConnectionInterceptor
{
    public static readonly BusyTimeoutInterceptor Instance = new();

    // WAL 是写入 DB 文件头的持久化设置：每个数据源（主库 / 缓存库）只需在首个连接上执行一次。
    // 用「数据源」而非单一布尔量记账——否则两个库共用一个标志时，只有先打开的那个库能拿到 WAL。
    private static readonly ConcurrentDictionary<string, byte> _walApplied =
        new(StringComparer.OrdinalIgnoreCase);

    private const string PragmaAll = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=3000;";
    private const string PragmaBusyOnly = "PRAGMA busy_timeout=3000;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => Execute(connection);

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Execute(connection);
        return Task.CompletedTask;
    }

    private static void Execute(DbConnection connection)
    {
        try
        {
            if (connection is SqliteConnection sqlite)
            {
                var key = connection.DataSource ?? string.Empty;
                var firstForThisDb = _walApplied.TryAdd(key, 0);

                using var cmd = sqlite.CreateCommand();
                cmd.CommandText = firstForThisDb ? PragmaAll : PragmaBusyOnly;
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex) { Log.Warning(ex, "设置 busy_timeout PRAGMA 失败"); }
    }
}
