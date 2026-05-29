using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
        ConfigureShortcutsBtn.Content = LanguageManager.GetString("Settings_ConfigureShortcuts");
        UpdateButtonStyles();
        UpdateLanguageStyles();
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
        MessageBox.Show(LanguageManager.GetString("Msg_NetworkSaved"), LanguageManager.GetString("Nav_Settings"));
    }

    #region 导入导出

    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = FolderPathBox.Text?.Trim();
            try { var dlg = new OpenFolderDialog { Title = LanguageManager.GetString("Msg_SelectFolder") }; if (dlg.ShowDialog() == true) { path = dlg.FolderName; FolderPathBox.Text = path; } } catch { }
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) { MessageBox.Show(LanguageManager.GetString("Msg_InvalidFolder")); return; }
            using var ctx = DbHelper.CreateContext();
            var ms = new MovieService(new MovieRepository(ctx), new TagRepository(ctx));
            var r = await new FolderImportService(new DoubanApiClient()).ImportFolderAsync(path, RecursiveCheck.IsChecked == true, ms);
            MessageBox.Show(string.Format(LanguageManager.GetString("Msg_FolderImportResult"), r.Imported, r.Skipped), LanguageManager.GetString("Settings_ImportExport"));
        }
        catch (Exception ex) { MessageBox.Show(ex.Message); }
    }

    private async void ExportCsv_Click(object sender, RoutedEventArgs e) { var d = new SaveFileDialog { Filter = "CSV|*.csv", FileName = $"export_{DateTime.Now:yyyyMMdd}.csv" }; if (d.ShowDialog() != true) return; try { await _importExportService.ExportMoviesToCsvAsync(d.FileName); MessageBox.Show(LanguageManager.GetString("Msg_ExportDone")); } catch (Exception ex) { MessageBox.Show(ex.Message); } }
    private async void ExportJson_Click(object sender, RoutedEventArgs e) { var d = new SaveFileDialog { Filter = "JSON|*.json", FileName = $"export_{DateTime.Now:yyyyMMdd}.json" }; if (d.ShowDialog() != true) return; try { await _importExportService.ExportMoviesToJsonAsync(d.FileName); MessageBox.Show(LanguageManager.GetString("Msg_ExportDone")); } catch (Exception ex) { MessageBox.Show(ex.Message); } }
    private async void ExportFullBackup_Click(object sender, RoutedEventArgs e) { var d = new SaveFileDialog { Filter = "JSON|*.json", FileName = $"backup_{DateTime.Now:yyyyMMdd_HHmm}.json" }; if (d.ShowDialog() != true) return; try { await _importExportService.ExportFullDataToJsonAsync(d.FileName); MessageBox.Show(LanguageManager.GetString("Msg_BackupDone")); } catch (Exception ex) { MessageBox.Show(ex.Message); } }
    private async void ImportCsv_Click(object sender, RoutedEventArgs e) { var d = new OpenFileDialog { Filter = "CSV|*.csv" }; if (d.ShowDialog() != true) return; try { var r = await _importExportService.ImportMoviesFromCsvAsync(d.FileName); MessageBox.Show(string.Format(LanguageManager.GetString("Msg_ImportCount"), r.SuccessCount)); } catch (Exception ex) { MessageBox.Show(ex.Message); } }
    private async void ImportJson_Click(object sender, RoutedEventArgs e) { var d = new OpenFileDialog { Filter = "JSON|*.json" }; if (d.ShowDialog() != true) return; try { var r = await _importExportService.ImportMoviesFromJsonAsync(d.FileName); MessageBox.Show(string.Format(LanguageManager.GetString("Msg_ImportCount"), r.SuccessCount)); } catch (Exception ex) { MessageBox.Show(ex.Message); } }
    private async void RestoreBackup_Click(object sender, RoutedEventArgs e) { if (MessageBox.Show(LanguageManager.GetString("Msg_ConfirmOverwrite"), LanguageManager.GetString("Msg_Confirm"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return; var d = new OpenFileDialog { Filter = "JSON|*.json" }; if (d.ShowDialog() != true) return; try { var r = await _importExportService.ImportFullDataFromJsonAsync(d.FileName); MessageBox.Show(string.Format(LanguageManager.GetString("Msg_RestoreCount"), r.SuccessCount)); } catch (Exception ex) { MessageBox.Show(ex.Message); } }
    private async void BackupDbFile_Click(object sender, RoutedEventArgs e) { var d = new SaveFileDialog { Filter = "DB|*.db", FileName = $"EasyMovie_{DateTime.Now:yyyyMMdd_HHmm}.db" }; if (d.ShowDialog() != true) return; try { await _importExportService.BackupDatabaseAsync(d.FileName); MessageBox.Show(LanguageManager.GetString("Msg_BackupDone")); } catch (Exception ex) { MessageBox.Show(ex.Message); } }
    private async void RestoreDbFile_Click(object sender, RoutedEventArgs e) { if (MessageBox.Show(LanguageManager.GetString("Msg_ConfirmReplaceDb"), LanguageManager.GetString("Msg_Confirm"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return; var d = new OpenFileDialog { Filter = "DB|*.db" }; if (d.ShowDialog() != true) return; try { await _importExportService.RestoreDatabaseAsync(d.FileName); MessageBox.Show(LanguageManager.GetString("Msg_RestartRequired")); } catch (Exception ex) { MessageBox.Show(ex.Message); } }

    #endregion

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
}
