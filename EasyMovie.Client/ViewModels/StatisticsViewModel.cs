using EasyMovie.Core.Interfaces;

namespace EasyMovie.Client.ViewModels;

/// <summary>
/// 统计 ViewModel（增量 MVVM）：承载 IStatisticsService，供代码后置渐进调用。
/// </summary>
public class StatisticsViewModel
{
    public StatisticsViewModel(IStatisticsService statsService)
    {
        StatisticsService = statsService;
    }

    public IStatisticsService StatisticsService { get; }
}
