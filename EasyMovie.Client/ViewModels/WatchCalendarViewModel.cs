using EasyMovie.Data;

namespace EasyMovie.Client.ViewModels;

/// <summary>
/// 观影日历 ViewModel（增量 MVVM）：承载 WatchLogService，供代码后置渐进调用。
/// </summary>
public class WatchCalendarViewModel
{
    public WatchCalendarViewModel(WatchLogService watchLogService)
    {
        WatchLogService = watchLogService;
    }

    public WatchLogService WatchLogService { get; }
}
