using System;
using System.IO;
using System.Text.Json;

namespace EasyMovie.Core;

/// <summary>
/// 应用设置持久化管理
/// </summary>
public static class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EasyMovie", "settings.json");

    // 旧版路径（用于自动迁移）
    private static readonly string OldSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MovieManager", "settings.json");

    private static SettingsData _current = new();

    public static AppThemeMode Theme
    {
        get => _current.Theme;
        set { _current.Theme = value; Save(); }
    }

    /// <summary>当前是否为深色主题（考虑系统主题）</summary>
    public static bool IsDarkTheme => _current.Theme switch
    {
        AppThemeMode.Dark => true,
        AppThemeMode.Light => false,
        _ => IsNightTime()
    };

    /// <summary>根据时间判断是否为夜间（18:00-06:00 为夜间，使用深色主题）</summary>
    public static bool IsNightTime()
    {
        var hour = DateTime.Now.Hour;
        return hour >= 18 || hour < 6;
    }

    public static string? TmdbApiKey
    {
        get => _current.TmdbApiKey;
        set { _current.TmdbApiKey = value; Save(); }
    }

    public static string? HttpProxy
    {
        get => _current.HttpProxy;
        set { _current.HttpProxy = value; Save(); }
    }

    public static string? DoubanCookie
    {
        get => _current.DoubanCookie;
        set { _current.DoubanCookie = value; Save(); }
    }

    /// <summary>界面语言 (zh-CN / en-US)</summary>
    public static string Language
    {
        get => _current.Language;
        set { _current.Language = value; Save(); }
    }

    public static int BackupIntervalDays
    {
        get => _current.BackupIntervalDays;
        set { _current.BackupIntervalDays = value; Save(); }
    }

    public static int MaxBackupCount
    {
        get => _current.MaxBackupCount;
        set { _current.MaxBackupCount = value; Save(); }
    }

    static AppSettings() => Load();

    private static void Load()
    {
        // 自动从旧版迁移设置
        MigrateFromOldVersion();
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                _current = JsonSerializer.Deserialize<SettingsData>(json) ?? new();
            }
        }
        catch { _current = new(); }
    }

    private static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_current));
        }
        catch { }
    }

    /// <summary>从旧版 MovieManager 自动迁移设置文件</summary>
    private static void MigrateFromOldVersion()
    {
        try
        {
            if (File.Exists(SettingsPath)) return;
            if (!File.Exists(OldSettingsPath)) return;
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.Copy(OldSettingsPath, SettingsPath, overwrite: false);
        }
        catch { }
    }

    private class SettingsData
    {
        public AppThemeMode Theme { get; set; } = AppThemeMode.System;
        public string? TmdbApiKey { get; set; }
        public string? HttpProxy { get; set; }
        public string? DoubanCookie { get; set; }
        public string Language { get; set; } = "zh-CN";
        public int BackupIntervalDays { get; set; } = 7;
        public int MaxBackupCount { get; set; } = 10;
    }
}

/// <summary>主题模式</summary>
public enum AppThemeMode
{
    System = 0,
    Dark = 1,
    Light = 2
}
