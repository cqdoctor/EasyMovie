using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace EasyMovie.Core;

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
        catch { }
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

        File.Copy(DbPath, backupPath, overwrite: true);

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

    public static void RestoreBackup(string backupFilePath)
    {
        if (!File.Exists(backupFilePath))
            throw new FileNotFoundException("Backup file not found");

        File.Copy(backupFilePath, DbPath, overwrite: true);
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
            try { File.Delete(old.FilePath); } catch { }
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
