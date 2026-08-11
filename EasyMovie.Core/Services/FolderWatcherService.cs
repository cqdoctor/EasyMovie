using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace EasyMovie.Core.Services;

/// <summary>
/// 文件夹监控服务 - FileSystemWatcher + 轮询兜底，确保不遗漏文件
/// </summary>
public class FolderWatcherService : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly HashSet<string> _recentlyCreated = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _pollingTimer;
    private List<string> _monitoredFolders = new();
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(10);

    /// <summary>视频文件扩展名</summary>
    public static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".m4v", ".webm", ".ts", ".rmvb", ".mpg", ".mpeg", ".x264", ".x265", ".hevc" };

    /// <summary>检测到新文件时触发（文件路径）</summary>
    public event Action<string>? NewFileDetected;

    /// <summary>获取已入库文件路径集合的回调（用于滤重）</summary>
    public Func<HashSet<string>>? GetExistingPaths { get; set; }

    /// <summary>是否正在运行</summary>
    public bool IsRunning { get; private set; }

    /// <summary>启动监控（FileSystemWatcher + 轮询兜底）</summary>
    public void Start(IEnumerable<string> folders)
    {
        Stop();
        _monitoredFolders = folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Log.Information("[FolderWatcher] 启动监控，共 {Count} 个目录", _monitoredFolders.Count);

        foreach (var folder in _monitoredFolders)
        {
            if (!Directory.Exists(folder))
            {
                Log.Warning("[FolderWatcher] 目录不存在，跳过: {Folder}", folder);
                continue;
            }

            try
            {
                var watcher = new FileSystemWatcher(folder)
                {
                    EnableRaisingEvents = false,
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                    InternalBufferSize = 65536
                };
                watcher.Created += OnFileCreated;
                watcher.Changed += OnFileChanged;
                watcher.Renamed += OnFileRenamed;
                watcher.Error += (_, args) =>
                    Log.Error(args.GetException(), "[FolderWatcher] 监控错误: {Folder}", folder);

                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
                Log.Information("[FolderWatcher] 开始监控: {Folder}", folder);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[FolderWatcher] 无法监控: {Folder}", folder);
            }
        }
        IsRunning = _watchers.Count > 0;

        // 启动轮询兜底：每10秒扫描一次监控目录，防止 FileSystemWatcher 遗漏事件
        _pollingTimer = new Timer(_ => PollFolders(), null, TimeSpan.Zero, PollingInterval);
        Log.Information("[FolderWatcher] 轮询兜底已启动，间隔 {Interval} 秒", PollingInterval.TotalSeconds);
    }

    public void Stop()
    {
        _pollingTimer?.Dispose();
        _pollingTimer = null;

        foreach (var w in _watchers)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }
        _watchers.Clear();
        lock (_recentlyCreated) { _recentlyCreated.Clear(); }
        _monitoredFolders.Clear();
        IsRunning = false;
        Log.Information("[FolderWatcher] 监控已停止");
    }

    /// <summary>轮询扫描所有监控目录，检测新文件</summary>
    private void PollFolders()
    {
        try
        {
            HashSet<string>? existingPaths = null;
            try { existingPaths = GetExistingPaths?.Invoke(); }
            catch (Exception ex) { Log.Error(ex, "[FolderWatcher] 获取已入库路径失败"); }

            var found = 0;
            foreach (var folder in _monitoredFolders)
            {
                if (!Directory.Exists(folder)) continue;
                try
                {
                    var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                        .Where(f => IsVideoFile(f));

                    foreach (var file in files)
                    {
                        // 跳过已在数据库中的文件
                        if (existingPaths != null && existingPaths.Contains(file))
                            continue;

                        lock (_recentlyCreated)
                        {
                            // 已在防抖集合中但未入库 → 可能是上次导入失败，清除重试
                            if (_recentlyCreated.Contains(file))
                            {
                                if (existingPaths == null || !existingPaths.Contains(file))
                                {
                                    _recentlyCreated.Remove(file);
                                    Log.Information("[FolderWatcher] 轮询重试未入库文件: {Path}", file);
                                }
                                else
                                {
                                    continue;
                                }
                            }
                            _recentlyCreated.Add(file);
                        }

                        Log.Information("[FolderWatcher] 轮询发现新文件: {Path}", file);
                        found++;

                        // 延迟后通知
                        var filePath = file;
                        Task.Delay(DebounceDelay).ContinueWith(_ =>
                        {
                            if (File.Exists(filePath))
                                NewFileDetected?.Invoke(filePath);
                        });
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[FolderWatcher] 扫描目录异常: {Folder}", folder);
                }
            }
            if (found > 0)
                Log.Information("[FolderWatcher] 轮询本轮发现 {Count} 个新文件", found);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[FolderWatcher] 轮询异常");
        }
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        Log.Debug("[FolderWatcher] Created 事件: {Path}", e.FullPath);
        CheckVideoFile(e.FullPath);
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        Log.Debug("[FolderWatcher] Changed 事件: {Path}", e.FullPath);
        CheckVideoFile(e.FullPath);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        Log.Debug("[FolderWatcher] Renamed 事件: {OldPath} -> {NewPath}", e.OldFullPath, e.FullPath);
        CheckVideoFile(e.FullPath);
    }

    private void CheckVideoFile(string filePath)
    {
        if (!IsVideoFile(filePath))
        {
            var ext = Path.GetExtension(filePath);
            if (!string.IsNullOrEmpty(ext))
                Log.Debug("[FolderWatcher] 非视频文件，忽略: {Path} (扩展名: {Ext})", filePath, ext);
            return;
        }

        Log.Information("[FolderWatcher] 检测到视频文件: {Path}", filePath);

        // 防抖：同一文件短时间内只触发一次
        lock (_recentlyCreated)
        {
            if (!_recentlyCreated.Add(filePath))
            {
                Log.Debug("[FolderWatcher] 文件已在防抖集合中，跳过: {Path}", filePath);
                return;
            }
        }

        // 等文件写入完成再通知
        Task.Delay(DebounceDelay).ContinueWith(_ =>
        {
            if (File.Exists(filePath))
            {
                Log.Information("[FolderWatcher] 通知新文件: {Path}", filePath);
                NewFileDetected?.Invoke(filePath);
            }
            else
            {
                Log.Warning("[FolderWatcher] 文件已不存在，取消通知: {Path}", filePath);
            }
        });
    }

    private static bool IsVideoFile(string path) =>
        VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public void Dispose() => Stop();
}