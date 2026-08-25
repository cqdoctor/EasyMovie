using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Serilog;

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
        _ => _systemThemeDetector()
    };

    /// <summary>
    /// 系统主题（Theme=System 时）的深色判定委托。
    /// 默认按夜间时间回退；Windows 客户端应在启动时通过 <see cref="SetSystemThemeDetector"/> 注入读取系统注册表的实现，
    /// 从而实现"跟随 Windows 深浅色"。
    /// </summary>
    private static Func<bool> _systemThemeDetector = IsNightTime;

    /// <summary>注入系统主题检测器（Windows 客户端注入真实系统主题读取；传 null 忽略）。</summary>
    public static void SetSystemThemeDetector(Func<bool> detector)
    {
        if (detector != null) _systemThemeDetector = detector;
    }

    /// <summary>根据时间判断是否为夜间（18:00-06:00 为夜间，使用深色主题）。作为系统主题检测的默认回退。</summary>
    public static bool IsNightTime()
    {
        var hour = DateTime.Now.Hour;
        return hour >= 18 || hour < 6;
    }

    public static string SkinName
    {
        get => _current.SkinName;
        set { _current.SkinName = value; Save(); }
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

    public static string? OmdbApiKey
    {
        get => _current.OmdbApiKey;
        set { _current.OmdbApiKey = value; Save(); }
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

    public static string AiProvider
    {
        get => _current.AiProvider;
        set { _current.AiProvider = value; Save(); }
    }

    public static string? AiApiKey
    {
        get => _current.AiApiKey;
        set { _current.AiApiKey = value; Save(); }
    }

    public static string AiApiEndpoint
    {
        get => _current.AiApiEndpoint;
        set { _current.AiApiEndpoint = value; Save(); }
    }

    public static string AiModel
    {
        get => _current.AiModel;
        set { _current.AiModel = value; Save(); }
    }

    public static bool FolderMonitorEnabled
    {
        get => _current.FolderMonitorEnabled;
        set { _current.FolderMonitorEnabled = value; Save(); }
    }

    /// <summary>是否启用“定时同步在线信息”（按豆瓣/TMDB 外部 ID 刷新元数据）</summary>
    public static bool MetadataAutoSyncEnabled
    {
        get => _current.MetadataAutoSyncEnabled;
        set { _current.MetadataAutoSyncEnabled = value; Save(); }
    }

    /// <summary>定时同步间隔（小时），默认 24</summary>
    public static int MetadataAutoSyncIntervalHours
    {
        get => _current.MetadataAutoSyncIntervalHours;
        set { _current.MetadataAutoSyncIntervalHours = value; Save(); }
    }

    /// <summary>上映提醒：把"想看"清单与豆瓣正在热映/即将上映匹配，命中时在首页提示</summary>
    public static bool ReleaseReminderEnabled
    {
        get => _current.ReleaseReminderEnabled;
        set { _current.ReleaseReminderEnabled = value; Save(); }
    }

    /// <summary>上映提醒范围：true 同时提醒"正在热映"，false 仅提醒"即将上映"</summary>
    public static bool ReleaseReminderIncludeNowPlaying
    {
        get => _current.ReleaseReminderIncludeNowPlaying;
        set { _current.ReleaseReminderIncludeNowPlaying = value; Save(); }
    }

    public static List<string> MonitoredFolders
    {
        get => _current.MonitoredFolders;
        set { _current.MonitoredFolders = value; Save(); }
    }

    private static readonly object _deletedLock = new();

    /// <summary>已删除的电影文件路径（防止重新导入）</summary>
    public static HashSet<string> DeletedFilePaths
    {
        get => _current.DeletedFilePaths;
        set { _current.DeletedFilePaths = value; Save(); }
    }

    /// <summary>记录已删除的文件路径，防止重新导入（线程安全：后台导入线程与 FolderWatcher 监控线程可能并发读写）</summary>
    public static void MarkFileDeleted(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        lock (_deletedLock)
        {
            if (_current.DeletedFilePaths.Add(filePath))
                Save();
        }
    }

    /// <summary>线程安全地判断文件是否已被删除（防止重复导入）</summary>
    public static bool IsFileDeleted(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        lock (_deletedLock)
            return _current.DeletedFilePaths.Contains(filePath);
    }

    /// <summary>从黑名单移除（用户手动导入时调用）。线程安全并落盘——旧实现直接 Remove 不落盘，移除后下次扫描仍会被当黑名单。</summary>
    public static void UnmarkFileDeleted(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        lock (_deletedLock)
        {
            if (_current.DeletedFilePaths.Remove(filePath))
                Save();
        }
    }

    /// <summary>返回当前黑名单的快照，枚举安全（避免遍历时被后台线程修改抛 InvalidOperationException）</summary>
    public static List<string> GetDeletedFilePathsSnapshot()
    {
        lock (_deletedLock)
            return _current.DeletedFilePaths.ToList();
    }

    /// <summary>手动保存设置（用于直接修改集合后）</summary>
    public static void SaveSettings() => Save();

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
        catch (Exception ex)
        {
            Log.Error(ex, "加载设置失败，已回退到默认值");
            _current = new();
        }
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
        catch (Exception ex)
        {
            Log.Error(ex, "保存设置失败");
        }
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
        catch (Exception ex)
        {
            Log.Error(ex, "迁移旧版设置失败");
        }
    }

    private class SettingsData
    {
        public AppThemeMode Theme { get; set; } = AppThemeMode.System;
        public string SkinName { get; set; } = "";
        public string? TmdbApiKey { get; set; }
        public string? HttpProxy { get; set; }
        public string? DoubanCookie { get; set; }
        public string? OmdbApiKey { get; set; }
        public string Language { get; set; } = "zh-CN";
        public int BackupIntervalDays { get; set; } = 7;
        public int MaxBackupCount { get; set; } = 10;
        public string AiProvider { get; set; } = "";
        public string? AiApiKey { get; set; }
        public string AiApiEndpoint { get; set; } = "https://api.openai.com/v1";
        public string AiModel { get; set; } = "gpt-4o-mini";
        public bool FolderMonitorEnabled { get; set; } = false;
        public bool MetadataAutoSyncEnabled { get; set; } = false;
        public int MetadataAutoSyncIntervalHours { get; set; } = 24;
        public bool ReleaseReminderEnabled { get; set; } = true;
        public bool ReleaseReminderIncludeNowPlaying { get; set; } = true;
        public List<string> MonitoredFolders { get; set; } = new();
        public HashSet<string> DeletedFilePaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>主题模式</summary>
public enum AppThemeMode
{
    System = 0,
    Dark = 1,
    Light = 2
}
