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

    private static readonly string OldDbDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MovieManager");
    private static readonly string OldDbPath = Path.Combine(OldDbDir, "MovieManager.db");

    private static readonly object _lock = new();
    private static bool _initialized;

    public static string ConnectionString => $"Data Source={DbPath}";

    public static MovieDbContext CreateContext()
    {
        EnsureInitialized();

        if (!Directory.Exists(DbDir)) Directory.CreateDirectory(DbDir);
        var options = new DbContextOptionsBuilder<MovieDbContext>().UseSqlite(ConnectionString).Options;
        var context = new MovieDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_lock)
        {
            if (_initialized) return;

            if (!Directory.Exists(DbDir)) Directory.CreateDirectory(DbDir);

            MigrateFromOldVersion();

            var options = new DbContextOptionsBuilder<MovieDbContext>().UseSqlite(ConnectionString).Options;
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
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var colName = reader.GetString(1);
                        if (colName == "SearchIndex") hasSearchIndex = true;
                        if (colName == "PosterData") hasPosterData = true;
                        if (colName == "CollectionId") hasCollectionId = true;
                        if (colName == "CollectionOrder") hasCollectionOrder = true;
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

                ctx.Database.CloseConnection();
            }
            catch { }

            _initialized = true;
        }
    }

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
        catch { }
    }
}
