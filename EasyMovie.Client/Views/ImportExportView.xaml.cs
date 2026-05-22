using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Services;
using EasyMovie.Data;
using EasyMovie.Data.Repositories;
using EasyMovie.Tools.ImportExport;
using EasyMovie.Tools.MovieApi;

namespace EasyMovie.Client.Views;

public partial class ImportExportView : UserControl
{
    private readonly MovieDbContext _context;
    private readonly IImportExportService _importExportService;

    public ImportExportView()
    {
        InitializeComponent();
        _context = DbHelper.CreateContext();
        _importExportService = new ImportExportService(_context);
        Unloaded += (s, e) => _context.Dispose();
    }

    private void Log(string m) => LogBox.Dispatcher.Invoke(() => { LogBox.Text += $"[{DateTime.Now:HH:mm:ss}] {m}\n"; LogBox.ScrollToEnd(); });

    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = FolderPathBox.Text?.Trim();
            try { var dlg = new OpenFolderDialog { Title = "选择包含视频文件的文件夹" }; if (dlg.ShowDialog() == true) path = dlg.FolderName; } catch { }
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) { MessageBox.Show("请先选择或输入有效的文件夹路径。"); return; }
            Log($"扫描: {path}...");
            using var ctx = DbHelper.CreateContext();
            var ms = new MovieService(new MovieRepository(ctx), new TagRepository(ctx));
            var r = await new FolderImportService(new DoubanApiClient()).ImportFolderAsync(path, RecursiveCheck.IsChecked == true, ms);
            Log($"导入 {r.Imported} 部, 跳过 {r.Skipped}");
            foreach (var e2 in r.Errors) Log($"   {e2}");
        }
        catch (Exception ex) { Log($"失败: {ex.Message}"); MessageBox.Show(ex.Message); }
    }

    private async void ExportCsv_Click(object sender, RoutedEventArgs e) { var d = new SaveFileDialog { Filter = "CSV|*.csv", FileName = $"export_{DateTime.Now:yyyyMMdd}.csv" }; if (d.ShowDialog() != true) return; try { await _importExportService.ExportMoviesToCsvAsync(d.FileName); Log("OK"); } catch (Exception ex) { Log(ex.Message); } }
    private async void ExportJson_Click(object sender, RoutedEventArgs e) { var d = new SaveFileDialog { Filter = "JSON|*.json", FileName = $"export_{DateTime.Now:yyyyMMdd}.json" }; if (d.ShowDialog() != true) return; try { await _importExportService.ExportMoviesToJsonAsync(d.FileName); Log("OK"); } catch (Exception ex) { Log(ex.Message); } }
    private async void ExportFullBackup_Click(object sender, RoutedEventArgs e) { var d = new SaveFileDialog { Filter = "JSON|*.json", FileName = $"backup_{DateTime.Now:yyyyMMdd_HHmm}.json" }; if (d.ShowDialog() != true) return; try { await _importExportService.ExportFullDataToJsonAsync(d.FileName); Log("OK"); } catch (Exception ex) { Log(ex.Message); } }
    private async void ImportCsv_Click(object sender, RoutedEventArgs e) { var d = new OpenFileDialog { Filter = "CSV|*.csv" }; if (d.ShowDialog() != true) return; try { var r = await _importExportService.ImportMoviesFromCsvAsync(d.FileName); Log($"{r.SuccessCount} 部"); } catch (Exception ex) { Log(ex.Message); } }
    private async void ImportJson_Click(object sender, RoutedEventArgs e) { var d = new OpenFileDialog { Filter = "JSON|*.json" }; if (d.ShowDialog() != true) return; try { var r = await _importExportService.ImportMoviesFromJsonAsync(d.FileName); Log($"{r.SuccessCount} 部"); } catch (Exception ex) { Log(ex.Message); } }
    private async void RestoreBackup_Click(object sender, RoutedEventArgs e) { if (MessageBox.Show("覆盖所有数据？", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return; var d = new OpenFileDialog { Filter = "JSON|*.json" }; if (d.ShowDialog() != true) return; try { var r = await _importExportService.ImportFullDataFromJsonAsync(d.FileName); Log($"{r.SuccessCount} 部"); } catch (Exception ex) { Log(ex.Message); } }
    private async void BackupDbFile_Click(object sender, RoutedEventArgs e) { var d = new SaveFileDialog { Filter = "DB|*.db", FileName = $"EasyMovie_{DateTime.Now:yyyyMMdd_HHmm}.db" }; if (d.ShowDialog() != true) return; try { await _importExportService.BackupDatabaseAsync(d.FileName); Log("OK"); } catch (Exception ex) { Log(ex.Message); } }
    private async void RestoreDbFile_Click(object sender, RoutedEventArgs e) { if (MessageBox.Show("替换数据库？", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return; var d = new OpenFileDialog { Filter = "DB|*.db" }; if (d.ShowDialog() != true) return; try { await _importExportService.RestoreDatabaseAsync(d.FileName); Log("请重启"); } catch (Exception ex) { Log(ex.Message); } }
    private void ClearLog_Click(object sender, RoutedEventArgs e) { LogBox.Text = ""; }
}
