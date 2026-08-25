using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;
using EasyMovie.Core.Services;
using EasyMovie.Data;
using EasyMovie.Data.Repositories;
using EasyMovie.Tools.ImportExport;
using EasyMovie.Tools.MovieApi;
using EasyMovie.Client.ViewModels;
using Microsoft.Extensions.DependencyInjection;

using Serilog;

namespace EasyMovie.Client.Views;

public partial class SettingsView : UserControl
{
    private readonly MovieDbContext _context;
    private readonly SettingsViewModel _vm;
    private CancellationTokenSource? _backfillCts;

    public SettingsView()
    {
        InitializeComponent();
        _context = DbHelper.CreateContext();
        // 通过 DI 容器解析 ViewModel；DI 不可用时回退手工创建，行为等价
        _vm = App.Services?.GetService<SettingsViewModel>()
              ?? new SettingsViewModel(new ImportExportService(_context));
        TmdbKeyBox.Text = AppSettings.TmdbApiKey ?? "";
        ProxyBox.Text = AppSettings.HttpProxy ?? "";
        DoubanCookieBox.Text = AppSettings.DoubanCookie ?? "";
        OmdbKeyBox.Text = AppSettings.OmdbApiKey ?? "";
        UpdateSkinStyles();
        UpdateLanguageStyles();
        InitBackupSettings();
        InitAISettings();
        InitFolderMonitor();
        InitAutoSync();
        InitReleaseReminder();
        UpdateNetworkStatus();
    }

    private void Skin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string skinName)
        {
            AppSettings.SkinName = skinName;
            App.LoadSkin(skinName);
            UpdateSkinStyles();
        }
    }

    private void UpdateSkinStyles()
    {
        var skin = AppSettings.SkinName;
        if (string.IsNullOrEmpty(skin)) skin = App.IsDarkTheme ? "Dark" : "Light";
        SkinDarkBtn.Opacity = skin == "Dark" ? 1.0 : 0.5;
        SkinOceanBtn.Opacity = skin == "Ocean" ? 1.0 : 0.5;
        SkinForestBtn.Opacity = skin == "Forest" ? 1.0 : 0.5;
        SkinSunsetBtn.Opacity = skin == "Sunset" ? 1.0 : 0.5;
        SkinLightBtn.Opacity = skin == "Light" ? 1.0 : 0.5;
    }

    private void ZhLang_Click(object sender, RoutedEventArgs e)
    {
        LanguageManager.SetLanguage("zh-CN");
        UpdateLanguageStyles();
    }

    private void EnLang_Click(object sender, RoutedEventArgs e)
    {
        LanguageManager.SetLanguage("en-US");
        UpdateLanguageStyles();
    }

    private void UpdateLanguageStyles()
    {
        var lang = LanguageManager.CurrentLanguage;
        ZhLangBtn.Opacity = lang == "zh-CN" ? 1.0 : 0.5;
        EnLangBtn.Opacity = lang == "en-US" ? 1.0 : 0.5;
    }

    private void SaveNetwork_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.TmdbApiKey = TmdbKeyBox.Text?.Trim();
        AppSettings.HttpProxy = ProxyBox.Text?.Trim();
        AppSettings.DoubanCookie = DoubanCookieBox.Text?.Trim();
        AppSettings.OmdbApiKey = OmdbKeyBox.Text?.Trim();
        AppMessageBox.ShowInfo(LanguageManager.GetString("Msg_NetworkSaved"), LanguageManager.GetString("Nav_Settings"));
        UpdateNetworkStatus();
    }

    private void TmdbHelp_Click(object sender, RoutedEventArgs e)
    {
        TmdbHelpPanel.Visibility = TmdbHelpPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void DoubanHelp_Click(object sender, RoutedEventArgs e)
    {
        DoubanHelpPanel.Visibility = DoubanHelpPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OmdbHelp_Click(object sender, RoutedEventArgs e)
    {
        OmdbHelpPanel.Visibility = OmdbHelpPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateNetworkStatus()
    {
        var parts = new List<string>();
        if (string.IsNullOrEmpty(AppSettings.TmdbApiKey))
            parts.Add("TMDB 未配置");
        if (string.IsNullOrEmpty(AppSettings.DoubanCookie))
            parts.Add("豆瓣 Cookie 未配置");
        if (string.IsNullOrEmpty(AppSettings.OmdbApiKey))
            parts.Add("OMDb 未配置");

        if (parts.Count > 0)
            NetworkStatusText.Text = "⚠ " + string.Join("，", parts) + " — 点 ? 查看教程";
        else
            NetworkStatusText.Text = "✅ 已配置";
    }

    #region 导入导出

    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = FolderPathBox.Text?.Trim();
            try { var dlg = new OpenFolderDialog { Title = LanguageManager.GetString("Msg_SelectFolder") }; if (dlg.ShowDialog() == true) { path = dlg.FolderName; FolderPathBox.Text = path; } } catch (Exception ex) { Log.Error(ex, "SettingsView 操作异常"); }
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) { AppMessageBox.ShowInfo(LanguageManager.GetString("Msg_InvalidFolder")); return; }
            using var ctx = DbHelper.CreateContext();
            var ms = new MovieService(new MovieRepository(ctx), new TagRepository(ctx));
            var r = await new FolderImportService(new DoubanApiClient()).ImportFolderAsync(path, RecursiveCheck.IsChecked == true, ms);
            AppMessageBox.ShowInfo(string.Format(LanguageManager.GetString("Msg_FolderImportResult"), r.Imported, r.Skipped), LanguageManager.GetString("Settings_ImportExport"));
        }
        catch (Exception ex) { AppMessageBox.ShowError(ex.Message); }
    }

    private async void ExportCsv_Click(object sender, RoutedEventArgs e) { var d = new SaveFileDialog { Filter = "CSV|*.csv", FileName = $"export_{DateTime.Now:yyyyMMdd}.csv" }; if (d.ShowDialog() != true) return; try { await _vm.ImportExportService.ExportMoviesToCsvAsync(d.FileName); AppMessageBox.ShowInfo(LanguageManager.GetString("Msg_ExportDone")); } catch (Exception ex) { AppMessageBox.ShowError(ex.Message); } }
    private async void ExportJson_Click(object sender, RoutedEventArgs e) { var d = new SaveFileDialog { Filter = "JSON|*.json", FileName = $"export_{DateTime.Now:yyyyMMdd}.json" }; if (d.ShowDialog() != true) return; try { await _vm.ImportExportService.ExportMoviesToJsonAsync(d.FileName); AppMessageBox.ShowInfo(LanguageManager.GetString("Msg_ExportDone")); } catch (Exception ex) { AppMessageBox.ShowError(ex.Message); } }
    private async void ExportFullBackup_Click(object sender, RoutedEventArgs e) { var d = new SaveFileDialog { Filter = "JSON|*.json", FileName = $"backup_{DateTime.Now:yyyyMMdd_HHmm}.json" }; if (d.ShowDialog() != true) return; try { await _vm.ImportExportService.ExportFullDataToJsonAsync(d.FileName); AppMessageBox.ShowInfo(LanguageManager.GetString("Msg_BackupDone")); } catch (Exception ex) { AppMessageBox.ShowError(ex.Message); } }
    private async void ImportCsv_Click(object sender, RoutedEventArgs e) { var d = new OpenFileDialog { Filter = "CSV|*.csv" }; if (d.ShowDialog() != true) return; try { var r = await _vm.ImportExportService.ImportMoviesFromCsvAsync(d.FileName); AppMessageBox.ShowInfo(string.Format(LanguageManager.GetString("Msg_ImportCount"), r.SuccessCount)); } catch (Exception ex) { AppMessageBox.ShowError(ex.Message); } }
    private async void ImportJson_Click(object sender, RoutedEventArgs e) { var d = new OpenFileDialog { Filter = "JSON|*.json" }; if (d.ShowDialog() != true) return; try { var r = await _vm.ImportExportService.ImportMoviesFromJsonAsync(d.FileName); AppMessageBox.ShowInfo(string.Format(LanguageManager.GetString("Msg_ImportCount"), r.SuccessCount)); } catch (Exception ex) { AppMessageBox.ShowError(ex.Message); } }
    private async void RestoreBackup_Click(object sender, RoutedEventArgs e) { if (!AppMessageBox.Confirm(LanguageManager.GetString("Msg_ConfirmOverwrite"), LanguageManager.GetString("Msg_Confirm"))) return; var d = new OpenFileDialog { Filter = "JSON|*.json" }; if (d.ShowDialog() != true) return; try { var r = await _vm.ImportExportService.ImportFullDataFromJsonAsync(d.FileName); AppMessageBox.ShowInfo(string.Format(LanguageManager.GetString("Msg_RestoreCount"), r.SuccessCount)); } catch (Exception ex) { AppMessageBox.ShowError(ex.Message); } }
    private async void BackupDbFile_Click(object sender, RoutedEventArgs e) { var d = new SaveFileDialog { Filter = "DB|*.db", FileName = $"EasyMovie_{DateTime.Now:yyyyMMdd_HHmm}.db" }; if (d.ShowDialog() != true) return; try { await _vm.ImportExportService.BackupDatabaseAsync(d.FileName); AppMessageBox.ShowInfo(LanguageManager.GetString("Msg_BackupDone")); } catch (Exception ex) { AppMessageBox.ShowError(ex.Message); } }
    private async void RestoreDbFile_Click(object sender, RoutedEventArgs e) { if (!AppMessageBox.Confirm(LanguageManager.GetString("Msg_ConfirmReplaceDb"), LanguageManager.GetString("Msg_Confirm"))) return; var d = new OpenFileDialog { Filter = "DB|*.db" }; if (d.ShowDialog() != true) return; try { await _vm.ImportExportService.RestoreDatabaseAsync(d.FileName); AppMessageBox.ShowInfo(LanguageManager.GetString("Msg_RestartRequired")); } catch (Exception ex) { AppMessageBox.ShowError(ex.Message); } }

    #endregion

    private void ManageCatTag_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        var dlg = new Window
        {
            Title = LanguageManager.GetString("CatTag_Title"),
            Content = new CategoryTagManageView(),
            Width = 900,
            Height = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            ShowInTaskbar = false
        };
        dlg.SourceInitialized += (_, _) => RemoveIcon(dlg);
        dlg.ShowDialog();
    }

    private static void RemoveIcon(Window window)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        var extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_DLGMODALFRAME);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_DLGMODALFRAME = 0x0001;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int nIndex, int dwNewLong);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int width, int height, uint flags);

    private void DetectDuplicates_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new DuplicateResultDialog
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
    }

    private void ConfigureShortcuts_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ShortcutSettingsDialog
        {
            Owner = Window.GetWindow(this)
        };
        dlg.ShowDialog();
    }

    #region 自动备份

    private void InitBackupSettings()
    {
        var interval = AppSettings.BackupIntervalDays;
        for (var i = 0; i < BackupIntervalCombo.Items.Count; i++)
            if (BackupIntervalCombo.Items[i] is ComboBoxItem ci && ci.Tag is string s && int.TryParse(s, out var v) && v == interval)
            { BackupIntervalCombo.SelectedIndex = i; break; }

        var maxCount = AppSettings.MaxBackupCount;
        for (var i = 0; i < MaxBackupCombo.Items.Count; i++)
            if (MaxBackupCombo.Items[i] is ComboBoxItem ci && ci.Tag is string s && int.TryParse(s, out var v) && v == maxCount)
            { MaxBackupCombo.SelectedIndex = i; break; }

        RefreshBackupHistory();
    }

    private void RefreshBackupHistory()
    {
        var history = BackupService.GetBackupHistory();
        BackupHistoryList.ItemsSource = history;
    }

    private void BackupInterval_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (BackupIntervalCombo.SelectedItem is ComboBoxItem ci && ci.Tag is string s && int.TryParse(s, out var v))
            AppSettings.BackupIntervalDays = v;
    }

    private void MaxBackup_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (MaxBackupCombo.SelectedItem is ComboBoxItem ci && ci.Tag is string s && int.TryParse(s, out var v))
            AppSettings.MaxBackupCount = v;
    }

    private void ManualBackup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            BackupService.CreateBackup();
            RefreshBackupHistory();
            AppMessageBox.ShowInfo(LanguageManager.GetString("Backup_Success"));
        }
        catch (Exception ex) { AppMessageBox.ShowError(ex.Message); }
    }

    private void OpenBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!System.IO.Directory.Exists(BackupService.BackupDirectory))
                System.IO.Directory.CreateDirectory(BackupService.BackupDirectory);
            System.Diagnostics.Process.Start("explorer.exe", BackupService.BackupDirectory);
        }
        catch (Exception ex) { AppMessageBox.ShowError(ex.Message); }
    }

    private void RestoreBackupItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string path) return;
        if (!AppMessageBox.Confirm(LanguageManager.GetString("Backup_ConfirmRestore"), LanguageManager.GetString("Msg_Confirm"))) return;
        try
        {
            BackupService.RestoreBackup(path);
            AppMessageBox.ShowInfo(LanguageManager.GetString("Backup_RestoreSuccess"));
        }
        catch (Exception ex) { AppMessageBox.ShowError(ex.Message); }
    }

    private void DeleteBackupItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string path) return;
        try
        {
            BackupService.DeleteBackup(path);
            RefreshBackupHistory();
        }
        catch (Exception ex) { AppMessageBox.ShowError(ex.Message); }
    }

    private void BackupHistoryList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ListBox listBox) return;
        e.Handled = true;

        // Re-raise the event as a bubbling MouseWheelEvent on the parent ScrollViewer
        var parent = VisualTreeHelper.GetParent(listBox);
        while (parent != null)
        {
            if (parent is ScrollViewer sv)
            {
                var e2 = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                {
                    RoutedEvent = UIElement.MouseWheelEvent,
                    Source = sender
                };
                sv.RaiseEvent(e2);
                break;
            }
            parent = VisualTreeHelper.GetParent(parent);
        }
    }

    #endregion

    #region AI 设置

    private void InitAISettings()
    {
        AiApiKeyBox.Text = AppSettings.AiApiKey ?? "";
        AiEndpointBox.Text = AppSettings.AiApiEndpoint;
        AiModelBox.Text = AppSettings.AiModel;

        var provider = AppSettings.AiProvider;
        for (var i = 0; i < AiProviderCombo.Items.Count; i++)
            if (AiProviderCombo.Items[i] is ComboBoxItem ci && ci.Tag is string s && s == provider)
            { AiProviderCombo.SelectedIndex = i; break; }
    }

    private void AiProvider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (AiProviderCombo.SelectedItem is not ComboBoxItem ci || ci.Tag is not string tag) return;

        switch (tag)
        {
            case "openai":
                AiEndpointBox.Text = "https://api.openai.com/v1";
                AiModelBox.Text = "gpt-4o-mini";
                break;
            case "deepseek":
                AiEndpointBox.Text = "https://api.deepseek.com";
                AiModelBox.Text = "deepseek-v4-flash";
                break;
            case "zhipu":
                AiEndpointBox.Text = "https://open.bigmodel.cn/api/paas/v4";
                AiModelBox.Text = "glm-4-flash";
                break;
            case "qwen":
                AiEndpointBox.Text = "https://dashscope.aliyuncs.com/compatible-mode/v1";
                AiModelBox.Text = "qwen-plus";
                break;
            case "baidu":
                AiEndpointBox.Text = "https://qianfan.baidubce.com/v2";
                AiModelBox.Text = "ernie-speed-128k";
                break;
            case "moonshot":
                AiEndpointBox.Text = "https://api.moonshot.cn/v1";
                AiModelBox.Text = "kimi-k2.5";
                break;
            case "doubao":
                AiEndpointBox.Text = "https://ark.cn-beijing.volces.com/api/v3";
                AiModelBox.Text = "doubao-seed-1-6-251015";
                break;
            case "ollama":
                AiEndpointBox.Text = "http://localhost:11434/v1";
                AiModelBox.Text = "qwen2.5:latest";
                break;
            case "custom":
                break;
        }
    }

    private void SaveAIFromUI()
    {
        AppSettings.AiProvider = (AiProviderCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        AppSettings.AiApiKey = AiApiKeyBox.Text?.Trim();
        AppSettings.AiApiEndpoint = AiEndpointBox.Text?.Trim() ?? "https://api.openai.com/v1";
        AppSettings.AiModel = AiModelBox.Text?.Trim() ?? "gpt-4o-mini";
    }

    private void SaveAI_Click(object sender, RoutedEventArgs e)
    {
        SaveAIFromUI();
        AppMessageBox.ShowInfo(LanguageManager.GetString("Msg_AISaved"), LanguageManager.GetString("Settings_AI"));
    }

    private async void TestAI_Click(object sender, RoutedEventArgs e)
    {
        SaveAIFromUI();

        try
        {
            var svc = new EasyMovie.Tools.AIChat.AIChatService();
            var result = new System.Text.StringBuilder();
            await foreach (var chunk in svc.ChatStreamAsync("你好，请简单介绍一下你自己。", "你好", new()))
            {
                result.Append(chunk);
                if (result.Length > 100) break;
            }
            var text = result.ToString();
            if (text.StartsWith("❌"))
                AppMessageBox.ShowError(LanguageManager.GetString("Msg_AITestFailed") + "\n" + text);
            else
                AppMessageBox.ShowInfo(LanguageManager.GetString("Msg_AITestSuccess") + "\n\n" +
                    (text.Length > 200 ? text[..200] + "..." : text),
                    LanguageManager.GetString("Settings_AI"));
        }
        catch (Exception ex)
        {
            AppMessageBox.ShowError(LanguageManager.GetString("Msg_AITestFailed") + "\n" + ex.Message);
        }
    }

    #endregion

    #region 文件夹监控

    private void InitFolderMonitor()
    {
        FolderMonitorToggle.IsChecked = AppSettings.FolderMonitorEnabled;
        UpdateFolderMonitorStatus();
        RefreshMonitoredFolderList();
    }

    private void UpdateFolderMonitorStatus()
    {
        if (App.FolderWatcher.IsRunning)
            FolderMonitorStatus.Text = $"监控中 ({AppSettings.MonitoredFolders.Count} 个目录)";
        else
            FolderMonitorStatus.Text = "未启用";
    }

    private void RefreshMonitoredFolderList()
    {
        MonitoredFolderList.ItemsSource = null;
        MonitoredFolderList.ItemsSource = AppSettings.MonitoredFolders;
    }

    private void FolderMonitorToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (FolderMonitorToggle.IsChecked == null) return;
        AppSettings.FolderMonitorEnabled = FolderMonitorToggle.IsChecked.Value;
        App.RestartFolderWatcher();
        UpdateFolderMonitorStatus();
    }

    // ── 自动同步在线信息 ──
    private void InitAutoSync()
    {
        AutoSyncToggle.IsChecked = AppSettings.MetadataAutoSyncEnabled;
        AutoSyncIntervalBox.Text = AppSettings.MetadataAutoSyncIntervalHours.ToString();
    }

    private void InitReleaseReminder()
    {
        ReleaseReminderToggle.IsChecked = AppSettings.ReleaseReminderEnabled;
        ReleaseReminderNowPlayingToggle.IsChecked = AppSettings.ReleaseReminderIncludeNowPlaying;
    }

    private void ReleaseReminderToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (ReleaseReminderToggle.IsChecked != null)
            AppSettings.ReleaseReminderEnabled = ReleaseReminderToggle.IsChecked.Value;
        if (ReleaseReminderNowPlayingToggle.IsChecked != null)
            AppSettings.ReleaseReminderIncludeNowPlaying = ReleaseReminderNowPlayingToggle.IsChecked.Value;
    }

    private void AutoSyncToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (AutoSyncToggle.IsChecked == null) return;
        AppSettings.MetadataAutoSyncEnabled = AutoSyncToggle.IsChecked.Value;
        App.ApplyMetadataAutoSyncSetting();
    }

    private async void SyncNow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn) btn.IsEnabled = false;
        try
        {
            // 间隔值随手动触发一并保存，便于下次启动沿用
            if (int.TryParse(AutoSyncIntervalBox.Text, out var hrs) && hrs > 0)
                AppSettings.MetadataAutoSyncIntervalHours = hrs;

            var progress = new Progress<string>(s => AutoSyncStatus.Text = s);
            AutoSyncStatus.Text = LanguageManager.GetString("Sync_Running") ?? "正在同步…";
            await App.RunMetadataSyncNow(progress);
        }
        catch (Exception ex)
        {
            AutoSyncStatus.Text = "同步失败：" + ex.Message;
        }
        finally
        {
            if (sender is Button b) b.IsEnabled = true;
        }
    }

    private void AddMonitoredFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new OpenFolderDialog { Title = "选择要监控的文件夹" };
            if (dlg.ShowDialog() != true) return;

            var path = dlg.FolderName;
            if (!AppSettings.MonitoredFolders.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                AppSettings.MonitoredFolders.Add(path);
                AppSettings.SaveSettings();
                RefreshMonitoredFolderList();
                App.RestartFolderWatcher();
                UpdateFolderMonitorStatus();
            }
        }
        catch (Exception ex) { Log.Error(ex, "SettingsView 操作异常"); }
    }

    private void RemoveMonitoredFolder_Click(object sender, RoutedEventArgs e)
    {
        var toRemove = MonitoredFolderList.SelectedItems.Cast<string>().ToList();
        if (toRemove.Count == 0) return;

        foreach (var path in toRemove)
            AppSettings.MonitoredFolders.Remove(path);

        AppSettings.SaveSettings();
        RefreshMonitoredFolderList();
        App.RestartFolderWatcher();
        UpdateFolderMonitorStatus();
    }

    private async void ScanNow_Click(object sender, RoutedEventArgs e)
    {
        if (!AppSettings.FolderMonitorEnabled || AppSettings.MonitoredFolders.Count == 0)
        {
            AppMessageBox.ShowInfo("请先启用文件夹监控并添加监控目录", "提示");
            return;
        }

        var foundFiles = new List<string>();
        foreach (var folder in AppSettings.MonitoredFolders)
        {
            if (!Directory.Exists(folder)) continue;
            try
            {
                var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                    .Where(f => FolderWatcherService.VideoExtensions
                        .Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    .ToList();
                foundFiles.AddRange(files);
            }
            catch (Exception ex) { Log.Error(ex, "SettingsView 操作异常"); }
        }

        if (foundFiles.Count == 0)
        {
            AppMessageBox.ShowInfo("监控目录中未发现视频文件", "扫描结果");
            return;
        }

        // 排除已在数据库中的文件
        using var ctx = DbHelper.CreateContext();
        var existingPaths = ctx.Movies
            .Where(m => m.FilePath != null)
            .Select(m => m.FilePath!)
            .AsEnumerable()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var newFiles = foundFiles.Where(f => !existingPaths.Contains(f)).ToList();

        if (newFiles.Count == 0)
        {
            AppMessageBox.ShowInfo($"发现 {foundFiles.Count} 个视频文件，但均已导入", "扫描结果");
            return;
        }

        if (!AppMessageBox.Confirm(
            $"发现 {newFiles.Count} 个未导入的视频文件，是否立即导入？\n\n" +
            string.Join("\n", newFiles.Take(10).Select(Path.GetFileName)) +
            (newFiles.Count > 10 ? $"\n... 还有 {newFiles.Count - 10} 个文件" : ""),
            "扫描结果"))
        {
            return;
        }

        // 批量导入
        try
        {
            var ms = new MovieService(new MovieRepository(ctx), new TagRepository(ctx));
            var importService = new FolderImportService();
            var imported = 0;

            foreach (var file in newFiles)
            {
                try
                {
                    var (title, year) = importService.ParseFileName(file);
                    var movie = new Movie
                    {
                        Title = title,
                        Year = year ?? 0,
                        FilePath = file,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    await ms.AddAsync(movie);
                    imported++;
                }
                catch (Exception ex) { Log.Error(ex, "SettingsView 操作异常"); }
            }

            if (Application.Current.MainWindow is MainWindow mw)
                mw.SetStatus($"已导入 {imported} 个新电影", false);

            AppMessageBox.ShowInfo($"成功导入 {imported} 个电影", "扫描结果");
        }
        catch (Exception ex)
        {
            AppMessageBox.ShowError($"导入失败: {ex.Message}");
        }
    }

    private void AutoAddFolders_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var ctx = DbHelper.CreateContext();
            var directories = ctx.Movies
                .Where(m => m.FilePath != null && m.FilePath != "")
                .Select(m => m.FilePath!)
                .AsEnumerable()
                .Select(fp => Path.GetDirectoryName(fp))
                .Where(dir => dir != null)
                .Select(dir => Path.GetDirectoryName(dir))  // 再上一级
                .Where(dir => dir != null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Cast<string>()
                .ToList();

            var added = 0;
            foreach (var dir in directories)
            {
                if (!AppSettings.MonitoredFolders.Contains(dir, StringComparer.OrdinalIgnoreCase))
                {
                    AppSettings.MonitoredFolders.Add(dir);
                    added++;
                }
            }

            AppSettings.SaveSettings();
            RefreshMonitoredFolderList();
            if (added > 0)
            {
                App.RestartFolderWatcher();
                AppMessageBox.ShowInfo($"已添加 {added} 个目录到监控列表", "文件夹监控");
            }
            else
            {
                AppMessageBox.ShowInfo("所有电影目录已在监控列表中", "文件夹监控");
            }
        }
        catch (Exception ex)
        {
            AppMessageBox.ShowError($"提取失败: {ex.Message}");
        }
    }

    #endregion

    #region 离线预种子（豆瓣 CSV → cache.db）

    private void SelectSeedFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var d = new OpenFileDialog { Filter = "CSV 文件|*.csv|所有文件|*.*", Title = "选择豆瓣 movies.csv" };
            if (d.ShowDialog() == true) SeedFilePathBox.Text = d.FileName;
        }
        catch (Exception ex) { Log.Error(ex, "SettingsView 选择种子文件异常"); }
    }

    private async void ImportSeed_Click(object sender, RoutedEventArgs e)
    {
        var path = SeedFilePathBox.Text?.Trim();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            AppMessageBox.ShowInfo("请先选择有效的豆瓣 movies.csv 文件", "导入离线种子");
            return;
        }
        if (sender is Button btn) btn.IsEnabled = false;
        try
        {
            double minRating = 0;
            long minVotes = 0;
            if (double.TryParse(SeedMinRatingBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var r)) minRating = r;
            if (long.TryParse(SeedMinVotesBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) minVotes = v;

            SeedProgressText.Text = "正在导入，请稍候…（仅本地读取）";
            var progress = new Progress<SeedImporter.SeedProgress>(p => SeedProgressText.Text = $"{p.Message}（已处理 {p.Done} 行）");
            var report = await SeedImporter.ImportDoubanCsvAsync(path, minRating, minVotes, progress);

            if (!string.IsNullOrEmpty(report.Error))
            {
                AppMessageBox.ShowError(report.Error);
                SeedProgressText.Text = "导入失败：" + report.Error;
            }
            else
            {
                AppMessageBox.ShowInfo(
                    $"导入完成：共读取 {report.TotalRows} 行，新增 {report.Inserted} 条，跳过(空/重复) {report.Skipped} 条，过滤(低分/冷门) {report.Filtered} 条，为已有条目补图 {report.PosterFilled} 条。\n缓存库现有约 {LocalMovieCache.Count()} 部影片元数据。",
                    "导入离线种子");
                SeedProgressText.Text = $"导入完成：新增 {report.Inserted} 条，缓存库约 {LocalMovieCache.Count()} 部";
            }
        }
        catch (Exception ex)
        {
            AppMessageBox.ShowError(ex.Message);
            SeedProgressText.Text = "导入失败：" + ex.Message;
        }
        finally
        {
            if (sender is Button b) b.IsEnabled = true;
        }
    }

    #endregion

    #region 慢慢补全 2020+ 元数据（豆瓣·慢速防封）

    /// <summary>从用户片库筛出 Year>=2020 且离线缓存覆盖不足的影片，作为补全队列。</summary>
    private List<(string Title, int? Year)> BuildBackfillQueue()
    {
        var queue = new List<(string, int?)>();
        LocalMovieCache.EnsureReady();
        // 用独立上下文，避免与 UI 线程上的 _context 跨线程争用
        using var ctx = DbHelper.CreateContext();
        var movies = ctx.Movies
            .Where(m => m.Year >= 2020)
            .Select(m => new { m.Title, m.Year })
            .AsEnumerable()
            .ToList();
        foreach (var mv in movies)
        {
            // 片库标题带发布标签（如"年会不能停 Johnny Keep Walking EAC3"），
            // cache.db 存的是干净标题。必须先用解析层清洗，否则 Lookup 键不匹配、
            // 已补全的影片会永远重复入队、队列永不缩小。
            var cleanTitle = DoubanApiClient.ExtractChineseKeyword(mv.Title);
            if (string.IsNullOrWhiteSpace(cleanTitle))
                cleanTitle = DoubanApiClient.ExtractEnglishHint(mv.Title) ?? mv.Title.Trim();
            var hit = LocalMovieCache.Lookup(new Movie { Title = cleanTitle, Year = mv.Year });
            // 覆盖判断：必须有"硬数据"（评分或海报）才算覆盖。
            // 导演/演员字段可能来自不可信的 seed 数据（如 14 万集中"满江红导演=左几"是错的），
            // 仅非空不可信——若只按"导演非空"判定，脏数据会挡住补全，正确信息进不来。
            bool covered = hit != null &&
                           (hit.Rating.HasValue || !string.IsNullOrEmpty(hit.PosterUrl));
            if (!covered) queue.Add((mv.Title, mv.Year));
        }
        return queue;
    }

    private void BackfillPreview_Click(object sender, RoutedEventArgs e)
    {
        BackfillPreviewText.Text = "统计中…";
        _ = Task.Run(() =>
        {
            try
            {
                var q = BuildBackfillQueue();
                Dispatcher.Invoke(() => BackfillPreviewText.Text = $"待补全约 {q.Count} 部（2020+ 且离线缓存覆盖不足）");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "统计待补全异常");
                Dispatcher.Invoke(() => BackfillPreviewText.Text = "统计失败：" + ex.Message);
            }
        });
    }

    private async void StartBackfill_Click(object sender, RoutedEventArgs e)
    {
        if (_backfillCts != null) return; // 已在运行
        if (sender is Button b) b.IsEnabled = false;
        _backfillCts = new CancellationTokenSource();
        var ct = _backfillCts.Token;
        var progress = new Progress<string>(msg => BackfillProgressText.Text = msg);
        try
        {
            // 先在后台构建队列（避免界面卡顿）
            BackfillProgressText.Text = "正在统计待补全影片…";
            var queue = await Task.Run(BuildBackfillQueue, ct);
            if (queue.Count == 0)
            {
                BackfillProgressText.Text = "没有需要补全的 2020+ 影片（离线缓存已覆盖或片库无 2020+ 影片）。";
                return;
            }
            BackfillProgressText.Text = $"开始慢慢补全，共 {queue.Count} 部（节奏很慢、遇封控即停）…";
            var report = await DoubanBackfillService.RunAsync(queue, progress, ct);
            if (ct.IsCancellationRequested)
                BackfillProgressText.Text = $"已手动停止。完成 {report.Done}/{report.Total}（补全 {report.Filled}，跳过 {report.Skipped}）。";
            else if (report.StoppedByThrottle)
                BackfillProgressText.Text = $"⚠ {report.Error} 本次完成 {report.Done}/{report.Total}（补全 {report.Filled}）。可稍后点“开始”继续。";
            else
                BackfillProgressText.Text = $"完成：{report.Done}/{report.Total}（补全 {report.Filled}，跳过 {report.Skipped}）。{(report.Error ?? "")}";
        }
        catch (OperationCanceledException)
        {
            BackfillProgressText.Text = "已停止。";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "慢慢补全异常");
            BackfillProgressText.Text = "补全异常：" + ex.Message;
        }
        finally
        {
            _backfillCts?.Dispose();
            _backfillCts = null;
            if (sender is Button bb) bb.IsEnabled = true;
        }
    }

    private void StopBackfill_Click(object sender, RoutedEventArgs e)
    {
        _backfillCts?.Cancel();
        BackfillProgressText.Text = "正在停止…";
    }

    #endregion

}
