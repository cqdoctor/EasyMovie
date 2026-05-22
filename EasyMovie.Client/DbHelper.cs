using System;
using System.IO;
using Microsoft.EntityFrameworkCore;
using EasyMovie.Data;

namespace EasyMovie.Client;

public static class DbHelper
{
    private static readonly string DbDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyMovie");
    private static readonly string DbPath = Path.Combine(DbDir, "EasyMovie.db");

    // 旧版 MovieManager 数据路径（用于自动迁移）
    private static readonly string OldDbDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MovieManager");
    private static readonly string OldDbPath = Path.Combine(OldDbDir, "MovieManager.db");

    public static string ConnectionString => $"Data Source={DbPath}";

    public static MovieDbContext CreateContext()
    {
        // 自动从旧版 MovieManager 迁移数据
        MigrateFromOldVersion();

        if (!Directory.Exists(DbDir)) Directory.CreateDirectory(DbDir);
        var options = new DbContextOptionsBuilder<MovieDbContext>().UseSqlite(ConnectionString).Options;
        var context = new MovieDbContext(options);
        context.Database.EnsureCreated();
        // 自动添加新列（兼容旧数据库）
        try
        {
            using var cmd = context.Database.GetDbConnection().CreateCommand();
            cmd.CommandText = "PRAGMA table_info(Movies)";
            var hasSearchIndex = false;
            context.Database.OpenConnection();
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (reader.GetString(1) == "SearchIndex") { hasSearchIndex = true; break; }
                }
            }
            if (!hasSearchIndex)
            {
                cmd.CommandText = "ALTER TABLE Movies ADD COLUMN SearchIndex TEXT;";
                cmd.ExecuteNonQuery();
            }
            // 检查 PosterData 列
            var hasPosterData = false;
            context.Database.OpenConnection();
            using (var reader2 = cmd.ExecuteReader())
            {
                while (reader2.Read())
                {
                    if (reader2.GetString(1) == "PosterData") { hasPosterData = true; break; }
                }
            }
            if (!hasPosterData)
            {
                cmd.CommandText = "ALTER TABLE Movies ADD COLUMN PosterData BLOB;";
                cmd.ExecuteNonQuery();
            }
            context.Database.CloseConnection();
        }
        catch { }
        return context;
    }

    /// <summary>从旧版 MovieManager 自动迁移数据库和设置</summary>
    private static void MigrateFromOldVersion()
    {
        try
        {
            // 新数据库已存在则跳过迁移
            if (File.Exists(DbPath)) return;
            // 旧数据库不存在则跳过
            if (!File.Exists(OldDbPath)) return;

            if (!Directory.Exists(DbDir)) Directory.CreateDirectory(DbDir);
            File.Copy(OldDbPath, DbPath, overwrite: false);

            // 迁移设置文件
            var oldSettings = Path.Combine(OldDbDir, "settings.json");
            var newSettings = Path.Combine(DbDir, "settings.json");
            if (File.Exists(oldSettings) && !File.Exists(newSettings))
                File.Copy(oldSettings, newSettings, overwrite: false);
        }
        catch { }
    }
}
