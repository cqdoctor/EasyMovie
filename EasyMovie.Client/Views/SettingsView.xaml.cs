using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Services;
using EasyMovie.Data;
using EasyMovie.Data.Repositories;
using EasyMovie.Tools.ImportExport;
using EasyMovie.Tools.MovieApi;

namespace EasyMovie.Client.Views;

public partial class SettingsView : UserControl
{
    private readonly MovieDbContext _context;
    private readonly IImportExportService _importExportService;

    public SettingsView()
    {
        InitializeComponent();
        _context = DbHelper.CreateContext();
        _importExportService = new ImportExportService(_context);
        TmdbKeyBox.Text = AppSettings.TmdbApiKey ?? "";
        ProxyBox.Text = AppSettings.HttpProxy ?? "";
        DoubanCookieBox.Text = AppSettings.DoubanCookie ?? "";
        UpdateButtonStyles();
        UpdateLanguageStyles();
        InitBackupSettings();
        InitAISettings();
    }

    private void SystemTheme_Click(object sender, RoutedEventArgs e) { App.SetTheme(AppThemeMode.System); UpdateButtonStyles(); }
    private void DarkTheme_Click(object sender, RoutedEventArgs e) { App.SetTheme(AppThemeMode.Dark); UpdateButtonStyles(); }
    private void LightTheme_Click(object sender, RoutedEventArgs e) { App.SetTheme(AppThemeMode.Light); UpdateButtonStyles(); }

    private void UpdateButtonStyles()
    {
        var mode = AppSettings.Theme;
        SystemThemeBtn.Opacity = mode == AppThemeMode.System ? 1.0 : 0.5;
        DarkThemeBtn.Opacity = mode == AppThemeMode.Dark ? 1.0 : 0.5;
        LightThemeBtn.Opacity = mode == AppThemeMode.Light ? 1.0 : 0.5;
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
        AppMessageBox.ShowInfo(LanguageManager.GetString("Msg_NetworkSaved"), LanguageManager.GetString("Nav_Settings"));
    }

    #region 导入导出

    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = FolderPathBox.Text?.Trim();
            try { var dlg = new OpenFolderDialog { Title = LanguageManager.GetString("Msg_SelectFolder") }; if (dlg.ShowDialog() == true) { path = dlg.FolderName; FolderPathBox.Text = path; } } catch { }
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) { AppMessageBox.ShowInfo(LanguageManager.GetString("Msg_InvalidFolder")); return; }
            using var ctx = DbHelper.CreateContext();
            var ms = new MovieService(new MovieRepository(ctx), new TagRepository(ctx));
            var r = await new FolderImportService(new DoubanApiClient()).ImportFolderAsync(path, RecursiveCheck.IsChecked == true, ms);
            AppMessageBox.ShowInfo(string.Format(LanguageManager.GetString("Msg_FolderImportResult"), r.Imported, r.Skipped), LanguageManager.GetString("Settings_ImportExport"));
        }
        catch (Exception ex) { AppMessageBox.ShowError(ex.Message); }
    }

    private async void ExportCsv_Click(object sender, RoutedEventArgs e) { var d = new SaveFileDialog { Filter = "CSV|*.csv", FileName = $"export_{DateTime.Now:yyyyMMdd}.csv" }; if (d.ShowDialog() != true) return; try { await _importExportService.ExportMoviesToCsvAsync(d.FileName); AppMessageBox.ShowInfo(LanguageManager.GetString("Msg_ExportDone")); } catch (Exception ex) { AppMessageBox.ShowError(ex.Message); } }
    private async void ExportJson_Click(object sender, RoutedEventArgs e) { var d = new SaveFileDialog { Filter = "JSON|*.json", FileName = $"export_{DateTime.Now:yyyyMMdd}.json" }; if (d.ShowDialog() != true) return; try { await _importExportService.ExportMoviesToJsonAsync(d.FileName); AppMessageBox.ShowInfo(LanguageManager.GetString("Msg_ExportDone")); } catch (Exception ex) { AppMessageBox.ShowError(ex.Message); } }
    private async void ExportFullBackup_Click(object sender, RoutedEventArgs e) { var d = new SaveFileDialog { Filter = "JSON|*.json", FileName = $"backup_{DateTime.Now:yyyyMMdd_HHmm}.json" }; if (d.ShowDialog() != true) return; try { await _importExportService.ExportFullDataToJsonAsync(d.FileName); AppMessageBox.ShowInfo(LanguageManager.GetString("Msg_BackupDone")); } catch (Exception ex) { AppMessageBox.ShowError(ex.Message); } }
    private async void ImportCsv_Click(object sender, RoutedEventArgs e) { var d = new OpenFileDialog { Filter = "CSV|*.csv" }; if (d.ShowDialog() != true) return; try { var r = await _importExportService.ImportMoviesFromCsvAsync(d.FileName); AppMessageBox.ShowInfo(string.Format(LanguageManager.GetString("Msg_ImportCount"), r.SuccessCount)); } catch (Exception ex) { AppMessageBox.ShowError(ex.Message); } }
    private async void ImportJson_Click(object sender, RoutedEventArgs e) { var d = new OpenFileDialog { Filter = "JSON|*.json" }; if (d.ShowDialog() != true) return; try { var r = await _importExportService.ImportMoviesFromJsonAsync(d.FileName); AppMessageBox.ShowInfo(string.Format(LanguageManager.GetString("Msg_ImportCount"), r.SuccessCount)); } catch (Exception ex) { AppMessageBox.ShowError(ex.Message); } }
    private async void RestoreBackup_Click(object sender, RoutedEventArgs e) { if (!AppMessageBox.Confirm(LanguageManager.GetString("Msg_ConfirmOverwrite"), LanguageManager.GetString("Msg_Confirm"))) return; var d = new OpenFileDialog { Filter = "JSON|*.json" }; if (d.ShowDialog() != true) return; try { var r = await _importExportService.ImportFullDataFromJsonAsync(d.FileName); AppMessageBox.ShowInfo(string.Format(LanguageManager.GetString("Msg_RestoreCount"), r.SuccessCount)); } catch (Exception ex) { AppMessageBox.ShowError(ex.Message); } }
    private async void BackupDbFile_Click(object sender, RoutedEventArgs e) { var d = new SaveFileDialog { Filter = "DB|*.db", FileName = $"EasyMovie_{DateTime.Now:yyyyMMdd_HHmm}.db" }; if (d.ShowDialog() != true) return; try { await _importExportService.BackupDatabaseAsync(d.FileName); AppMessageBox.ShowInfo(LanguageManager.GetString("Msg_BackupDone")); } catch (Exception ex) { AppMessageBox.ShowError(ex.Message); } }
    private async void RestoreDbFile_Click(object sender, RoutedEventArgs e) { if (!AppMessageBox.Confirm(LanguageManager.GetString("Msg_ConfirmReplaceDb"), LanguageManager.GetString("Msg_Confirm"))) return; var d = new OpenFileDialog { Filter = "DB|*.db" }; if (d.ShowDialog() != true) return; try { await _importExportService.RestoreDatabaseAsync(d.FileName); AppMessageBox.ShowInfo(LanguageManager.GetString("Msg_RestartRequired")); } catch (Exception ex) { AppMessageBox.ShowError(ex.Message); } }

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

    private void SaveAI_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.AiProvider = (AiProviderCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        AppSettings.AiApiKey = AiApiKeyBox.Text?.Trim();
        AppSettings.AiApiEndpoint = AiEndpointBox.Text?.Trim() ?? "https://api.openai.com/v1";
        AppSettings.AiModel = AiModelBox.Text?.Trim() ?? "gpt-4o-mini";
        AppMessageBox.ShowInfo(LanguageManager.GetString("Msg_AISaved"), LanguageManager.GetString("Settings_AI"));
    }

    private async void TestAI_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.AiProvider = (AiProviderCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        AppSettings.AiApiKey = AiApiKeyBox.Text?.Trim();
        AppSettings.AiApiEndpoint = AiEndpointBox.Text?.Trim() ?? "https://api.openai.com/v1";
        AppSettings.AiModel = AiModelBox.Text?.Trim() ?? "gpt-4o-mini";

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
}
