using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
            catch { }

            try
            {
                CleanHtmlInExistingData();
            }
            catch { }

            try
            {
                CleanDirtyPersonData();
            }
            catch { }

            try
            {
                MigrateDefaultWatchStatus();
            }
            catch { }

            try
            {
                SeedDefaultTags();
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

    private static readonly string HtmlCleanFlagPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyMovie", ".html_cleaned_v2");

    private static readonly string DirtyDataFlagPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyMovie", ".dirty_data_cleaned_v2");

    private static void CleanHtmlInExistingData()
    {
        if (File.Exists(HtmlCleanFlagPath)) return;

        var options = new DbContextOptionsBuilder<MovieDbContext>().UseSqlite(ConnectionString).Options;
        using var ctx = new MovieDbContext(options);

        var movies = ctx.Movies.ToList();
        var changed = false;
        foreach (var m in movies)
        {
            var cleanSynopsis = StripHtml(m.Synopsis);
            var cleanDirector = StripHtml(m.Director);
            var cleanCast = StripHtml(m.Cast);
            var cleanCountry = StripHtml(m.Country);
            var cleanNotes = StripHtml(m.Notes);

            if (cleanSynopsis != m.Synopsis || cleanDirector != m.Director ||
                cleanCast != m.Cast || cleanCountry != m.Country || cleanNotes != m.Notes)
            {
                m.Synopsis = cleanSynopsis;
                m.Director = cleanDirector;
                m.Cast = cleanCast;
                m.Country = cleanCountry;
                m.Notes = cleanNotes;
                changed = true;
            }
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

        var options = new DbContextOptionsBuilder<MovieDbContext>().UseSqlite(ConnectionString).Options;
        using var ctx = new MovieDbContext(options);

        var movies = ctx.Movies.ToList();
        var changed = false;
        foreach (var m in movies)
        {
            var cleanDirector = CleanPersonField(m.Director);
            var cleanCast = CleanPersonField(m.Cast);

            if (cleanDirector != m.Director || cleanCast != m.Cast)
            {
                m.Director = cleanDirector;
                m.Cast = cleanCast;
                changed = true;
            }
        }
        if (changed) ctx.SaveChanges();

        File.WriteAllText(DirtyDataFlagPath, DateTime.UtcNow.ToString("O"));
    }

    private static readonly string WatchStatusMigratedPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyMovie", ".watchstatus_migrated_v3");

    private static void MigrateDefaultWatchStatus()
    {
        if (File.Exists(WatchStatusMigratedPath)) return;

        var options = new DbContextOptionsBuilder<MovieDbContext>().UseSqlite(ConnectionString).Options;
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

        var options = new DbContextOptionsBuilder<MovieDbContext>().UseSqlite(ConnectionString).Options;
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
