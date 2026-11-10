using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EasyMovie.Core;
using Serilog;

namespace EasyMovie.Data;

/// <summary>
/// 备份历史管理。备份写入与恢复动作均委托 <see cref="DatabaseMaintenance"/>：
/// 写入走 VACUUM INTO（含 WAL 中未 checkpoint 的数据），恢复只做"落 pending"，重启后生效。
/// 位于 Data 层是因为 <c>Microsoft.Data.Sqlite</c> 只在本层被引用。
/// </summary>
public static class BackupService
{
    private static readonly string BackupDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EasyMovie", "backups");

    private static readonly string DbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EasyMovie", "EasyMovie.db");

    public static string BackupDirectory => BackupDir;

    public static void EnsureAutoBackup()
    {
        try
        {
            var intervalDays = AppSettings.BackupIntervalDays;
            if (intervalDays <= 0) return;

            if (!Directory.Exists(BackupDir))
                Directory.CreateDirectory(BackupDir);

            var lastBackup = GetBackupHistory()
                .Select(b => b.Timestamp)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();

            if ((DateTime.Now - lastBackup).TotalDays >= intervalDays)
                CreateBackup();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "自动备份失败");
        }
    }

    public static BackupInfo CreateBackup()
    {
        if (!File.Exists(DbPath))
            throw new FileNotFoundException("Database file not found");

        if (!Directory.Exists(BackupDir))
            Directory.CreateDirectory(BackupDir);

        var timestamp = DateTime.Now;
        var fileName = $"EasyMovie_{timestamp:yyyyMMdd_HHmmss}.db";
        var backupPath = Path.Combine(BackupDir, fileName);

        // 不再 File.Copy 主库：WAL 模式下会漏掉未 checkpoint 的事务。
        DatabaseMaintenance.CreateConsistentSnapshot(DbPath, backupPath);

        CleanupOldBackups();

        return new BackupInfo
        {
            FileName = fileName,
            FilePath = backupPath,
            Timestamp = timestamp,
            FileSize = new FileInfo(backupPath).Length
        };
    }

    public static List<BackupInfo> GetBackupHistory()
    {
        if (!Directory.Exists(BackupDir))
            return new List<BackupInfo>();

        return Directory.GetFiles(BackupDir, "EasyMovie_*.db")
            .Select(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f);
                var datePart = name.Replace("EasyMovie_", "");
                DateTime ts;
                if (!DateTime.TryParseExact(datePart, "yyyyMMdd_HHmmss", null,
                    System.Globalization.DateTimeStyles.None, out ts))
                    ts = File.GetCreationTime(f);

                return new BackupInfo
                {
                    FileName = Path.GetFileName(f),
                    FilePath = f,
                    Timestamp = ts,
                    FileSize = new FileInfo(f).Length
                };
            })
            .OrderByDescending(b => b.Timestamp)
            .ToList();
    }

    /// <summary>
    /// 校验备份并登记为"下次启动恢复"。运行期绝不覆盖主库。
    /// </summary>
    public static void RestoreBackup(string backupFilePath)
    {
        if (!File.Exists(backupFilePath))
            throw new FileNotFoundException("Backup file not found");

        DatabaseMaintenance.StagePendingRestore(backupFilePath, DbPath);
    }

    public static void DeleteBackup(string backupFilePath)
    {
        if (File.Exists(backupFilePath))
            File.Delete(backupFilePath);
    }

    private static void CleanupOldBackups()
    {
        var maxBackups = AppSettings.MaxBackupCount;
        if (maxBackups <= 0) return;

        var backups = GetBackupHistory();
        if (backups.Count <= maxBackups) return;

        foreach (var old in backups.Skip(maxBackups))
        {
            try { File.Delete(old.FilePath); }
            catch (Exception ex) { Log.Error(ex, "删除旧备份失败: {Path}", old.FilePath); }
        }
    }
}

public class BackupInfo
{
    public string FileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public long FileSize { get; set; }

    public string FileSizeText
    {
        get
        {
            if (FileSize < 1024) return $"{FileSize} B";
            if (FileSize < 1024 * 1024) return $"{FileSize / 1024.0:F1} KB";
            return $"{FileSize / 1024.0 / 1024.0:F1} MB";
        }
    }

    public string DisplayText => $"{Timestamp:yyyy-MM-dd HH:mm}  ({FileSizeText})";
}
