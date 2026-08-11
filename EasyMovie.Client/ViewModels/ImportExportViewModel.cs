using EasyMovie.Core.Interfaces;

namespace EasyMovie.Client.ViewModels;

/// <summary>
/// 导入导出 ViewModel（增量 MVVM）：承载 IImportExportService，供代码后置渐进调用。
/// </summary>
public class ImportExportViewModel
{
    public ImportExportViewModel(IImportExportService importExportService)
    {
        ImportExportService = importExportService;
    }

    public IImportExportService ImportExportService { get; }
}
