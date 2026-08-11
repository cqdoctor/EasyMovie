using EasyMovie.Core.Interfaces;

namespace EasyMovie.Client.ViewModels;

/// <summary>
/// 设置 ViewModel（增量 MVVM）：承载 IImportExportService，供代码后置渐进调用。
/// </summary>
public class SettingsViewModel
{
    public SettingsViewModel(IImportExportService importExportService)
    {
        ImportExportService = importExportService;
    }

    public IImportExportService ImportExportService { get; }
}
