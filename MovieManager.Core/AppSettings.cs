using System;
using System.IO;
using System.Text.Json;

namespace MovieManager.Core;

/// <summary>
/// 应用设置持久化管理
/// </summary>
public static class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MovieManager", "settings.json");

    private static SettingsData _current = new();

    public static bool IsDarkTheme
    {
        get => _current.IsDarkTheme;
        set { _current.IsDarkTheme = value; Save(); }
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

    static AppSettings() => Load();

    private static void Load()
    {
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

    private class SettingsData
    {
        public bool IsDarkTheme { get; set; } = true;
        public string? TmdbApiKey { get; set; }
        public string? HttpProxy { get; set; }
        public string? DoubanCookie { get; set; }
    }
}
