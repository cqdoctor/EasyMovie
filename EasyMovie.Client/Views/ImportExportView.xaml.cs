﻿﻿﻿using System;
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
using EasyMovie.Client.ViewModels;
using Microsoft.Extensions.DependencyInjection;

using Serilog;

namespace EasyMovie.Client.Views;

public partial class ImportExportView : UserControl
{
    private readonly MovieDbContext _context;
    private bool _disposed;
    private readonly ImportExportViewModel _vm;

    public ImportExportView()
    {
        InitializeComponent();
        _context = DbHelper.CreateContext();
        // 通过 DI 容器解析 ViewModel；DI 不可用时回退手工创建，行为等价
        _vm = App.Services?.GetService<ImportExportViewModel>()
              ?? new ImportExportViewModel(new ImportExportService(_context));
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        _disposed = true;
        Unloaded -= OnUnloaded;
        _context.Dispose();
    }

    private void Log(string m) => LogBox.Dispatcher.Invoke(() => { LogBox.Text += $"[{DateTime.Now:HH:mm:ss}] {m}\n"; LogBox.ScrollToEnd(); });

    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = FolderPathBox.Text?.Trim();
            try { var dlg = new OpenFolderDialog { Title = LanguageManager.GetString("Msg_SelectFolder") }; if (dlg.ShowDialog() == true) path = dlg.FolderName; } catch (Exception ex) { Serilog.Log.Error(ex, "ImportExportView 操作异常"); }
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) { AppMessageBox.ShowInfo(LanguageManager.GetString("Msg_InvalidFolder")); return; }
            Log(string.Format(LanguageManager.GetString("ImportExport_Scanning"), path));
            using var ctx = DbHelper.CreateContext();
            var ms = new MovieService(new MovieRepository(ctx), new TagRepository(ctx));
            var r = await new FolderImportService(new DoubanApiClient()).ImportFolderAsync(path, RecursiveCheck.IsChecked == true, ms);
            Log(string.Format(LanguageManager.GetString("Msg_FolderImportResult"), r.Imported, r.Skipped));
            foreach (var e2 in r.Errors) Log($"   {e2}");
        }
        catch (Exception ex) { Log(string.Format(LanguageManager.GetString("ImportExport_Failed"), ex.Message)); AppMessageBox.ShowError(ex.Message); }
    }

    private async void ExportCsv_Click(object sender, RoutedEventArgs e) { var d = new SaveFileDialog { Filter = "CSV|*.csv", FileName = $"export_{DateTime.Now:yyyyMMdd}.csv" }; if (d.ShowDialog() != true) return; try { await _vm.ImportExportService.ExportMoviesToCsvAsync(d.FileName); Log("OK"); } catch (Exception ex) { Log(ex.Message); } }
    private async void ExportJson_Click(object sender, RoutedEventArgs e) { var d = new SaveFileDialog { Filter = "JSON|*.json", FileName = $"export_{DateTime.Now:yyyyMMdd}.json" }; if (d.ShowDialog() != true) return; try { await _vm.ImportExportService.ExportMoviesToJsonAsync(d.FileName); Log("OK"); } catch (Exception ex) { Log(ex.Message); } }
    private async void ExportExcel_Click(object sender, RoutedEventArgs e) { var d = new SaveFileDialog { Filter = "Excel|*.xlsx", FileName = $"export_{DateTime.Now:yyyyMMdd}.xlsx" }; if (d.ShowDialog() != true) return; try { await _vm.ImportExportService.ExportMoviesToExcelAsync(d.FileName); Log("OK"); } catch (Exception ex) { Log(ex.Message); } }
    private async void ExportHtml_Click(object sender, RoutedEventArgs e) { var d = new SaveFileDialog { Filter = "HTML|*.html", FileName = $"export_{DateTime.Now:yyyyMMdd}.html" }; if (d.ShowDialog() != true) return; try { await _vm.ImportExportService.ExportMoviesToHtmlAsync(d.FileName); Log("OK"); } catch (Exception ex) { Log(ex.Message); } }
    private async void ExportFullBackup_Click(object sender, RoutedEventArgs e) { var d = new SaveFileDialog { Filter = "JSON|*.json", FileName = $"backup_{DateTime.Now:yyyyMMdd_HHmm}.json" }; if (d.ShowDialog() != true) return; try { await _vm.ImportExportService.ExportFullDataToJsonAsync(d.FileName); Log("OK"); } catch (Exception ex) { Log(ex.Message); } }
    private async void ImportCsv_Click(object sender, RoutedEventArgs e) { var d = new OpenFileDialog { Filter = "CSV|*.csv" }; if (d.ShowDialog() != true) return; try { var r = await _vm.ImportExportService.ImportMoviesFromCsvAsync(d.FileName); Log($"{r.SuccessCount} {LanguageManager.GetString("Msg_MoviesUnit")}"); } catch (Exception ex) { Log(ex.Message); } }
    private async void ImportJson_Click(object sender, RoutedEventArgs e) { var d = new OpenFileDialog { Filter = "JSON|*.json" }; if (d.ShowDialog() != true) return; try { var r = await _vm.ImportExportService.ImportMoviesFromJsonAsync(d.FileName); Log($"{r.SuccessCount} {LanguageManager.GetString("Msg_MoviesUnit")}"); } catch (Exception ex) { Log(ex.Message); } }
    private async void RestoreBackup_Click(object sender, RoutedEventArgs e) { if (!AppMessageBox.Confirm(LanguageManager.GetString("Msg_ConfirmOverwrite"))) return; var d = new OpenFileDialog { Filter = "JSON|*.json" }; if (d.ShowDialog() != true) return; try { var r = await _vm.ImportExportService.ImportFullDataFromJsonAsync(d.FileName); Log($"{r.SuccessCount} {LanguageManager.GetString("Msg_MoviesUnit")}"); } catch (Exception ex) { Log(ex.Message); } }
    private async void BackupDbFile_Click(object sender, RoutedEventArgs e) { var d = new SaveFileDialog { Filter = "DB|*.db", FileName = $"EasyMovie_{DateTime.Now:yyyyMMdd_HHmm}.db" }; if (d.ShowDialog() != true) return; try { await _vm.ImportExportService.BackupDatabaseAsync(d.FileName); Log("OK"); } catch (Exception ex) { Log(ex.Message); } }
    private async void RestoreDbFile_Click(object sender, RoutedEventArgs e) { if (!AppMessageBox.Confirm(LanguageManager.GetString("Msg_ConfirmReplaceDb"))) return; var d = new OpenFileDialog { Filter = "DB|*.db" }; if (d.ShowDialog() != true) return; try { await _vm.ImportExportService.RestoreDatabaseAsync(d.FileName); Log(LanguageManager.GetString("Msg_RestartRequired")); } catch (Exception ex) { Log(ex.Message); } }
    private void ClearLog_Click(object sender, RoutedEventArgs e) { LogBox.Text = ""; }
}
