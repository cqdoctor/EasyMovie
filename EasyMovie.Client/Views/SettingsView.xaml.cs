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
        UpdateButtonStyles();
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

    private void SaveNetwork_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.TmdbApiKey = TmdbKeyBox.Text?.Trim();
        AppSettings.HttpProxy = ProxyBox.Text?.Trim();
        AppSettings.DoubanCookie = DoubanCookieBox.Text?.Trim();
        MessageBox.Show("网络设置已保存", "设置");
    }

    #region 导入导出

    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = FolderPathBox.Text?.Trim();
            try { var dlg = new OpenFolderDialog { Title = "选择包含视频文件的文件夹" }; if (dlg.ShowDialog() == true) { path = dlg.FolderName; FolderPathBox.Text = path; } } catch { }
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) { MessageBox.Show("请先选择或输入有效的文件夹路径。"); return; }
            using var ctx = DbHelper.CreateContext();
            var ms = new MovieService(new MovieRepository(ctx), new TagRepository(ctx));
            var r = await new FolderImportService(new DoubanApiClient()).ImportFolderAsync(path, RecursiveCheck.IsChecked == true, ms);
            MessageBox.Show($"导入 {r.Imported} 部, 跳过 {r.Skipped}", "文件夹导入");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message); }
    }

    private async void ExportCsv_Click(object sender, RoutedEventArgs e) { var d = new SaveFileDialog { Filter = "CSV|*.csv", FileName = $"export_{DateTime.Now:yyyyMMdd}.csv" }; if (d.ShowDialog() != true) return; try { await _importExportService.ExportMoviesToCsvAsync(d.FileName); MessageBox.Show("导出完成"); } catch (Exception ex) { MessageBox.Show(ex.Message); } }
    private async void ExportJson_Click(object sender, RoutedEventArgs e) { var d = new SaveFileDialog { Filter = "JSON|*.json", FileName = $"export_{DateTime.Now:yyyyMMdd}.json" }; if (d.ShowDialog() != true) return; try { await _importExportService.ExportMoviesToJsonAsync(d.FileName); MessageBox.Show("导出完成"); } catch (Exception ex) { MessageBox.Show(ex.Message); } }
    private async void ExportFullBackup_Click(object sender, RoutedEventArgs e) { var d = new SaveFileDialog { Filter = "JSON|*.json", FileName = $"backup_{DateTime.Now:yyyyMMdd_HHmm}.json" }; if (d.ShowDialog() != true) return; try { await _importExportService.ExportFullDataToJsonAsync(d.FileName); MessageBox.Show("备份完成"); } catch (Exception ex) { MessageBox.Show(ex.Message); } }
    private async void ImportCsv_Click(object sender, RoutedEventArgs e) { var d = new OpenFileDialog { Filter = "CSV|*.csv" }; if (d.ShowDialog() != true) return; try { var r = await _importExportService.ImportMoviesFromCsvAsync(d.FileName); MessageBox.Show($"导入 {r.SuccessCount} 部"); } catch (Exception ex) { MessageBox.Show(ex.Message); } }
    private async void ImportJson_Click(object sender, RoutedEventArgs e) { var d = new OpenFileDialog { Filter = "JSON|*.json" }; if (d.ShowDialog() != true) return; try { var r = await _importExportService.ImportMoviesFromJsonAsync(d.FileName); MessageBox.Show($"导入 {r.SuccessCount} 部"); } catch (Exception ex) { MessageBox.Show(ex.Message); } }
    private async void RestoreBackup_Click(object sender, RoutedEventArgs e) { if (MessageBox.Show("覆盖所有数据？", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return; var d = new OpenFileDialog { Filter = "JSON|*.json" }; if (d.ShowDialog() != true) return; try { var r = await _importExportService.ImportFullDataFromJsonAsync(d.FileName); MessageBox.Show($"还原 {r.SuccessCount} 部"); } catch (Exception ex) { MessageBox.Show(ex.Message); } }
    private async void BackupDbFile_Click(object sender, RoutedEventArgs e) { var d = new SaveFileDialog { Filter = "DB|*.db", FileName = $"EasyMovie_{DateTime.Now:yyyyMMdd_HHmm}.db" }; if (d.ShowDialog() != true) return; try { await _importExportService.BackupDatabaseAsync(d.FileName); MessageBox.Show("备份完成"); } catch (Exception ex) { MessageBox.Show(ex.Message); } }
    private async void RestoreDbFile_Click(object sender, RoutedEventArgs e) { if (MessageBox.Show("替换数据库？需重启生效", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return; var d = new OpenFileDialog { Filter = "DB|*.db" }; if (d.ShowDialog() != true) return; try { await _importExportService.RestoreDatabaseAsync(d.FileName); MessageBox.Show("还原完成，请重启应用"); } catch (Exception ex) { MessageBox.Show(ex.Message); } }

    #endregion
}
