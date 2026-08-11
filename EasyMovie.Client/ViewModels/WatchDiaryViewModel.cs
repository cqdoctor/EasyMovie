using EasyMovie.Data;

namespace EasyMovie.Client.ViewModels;

/// <summary>
/// WatchDiaryView 的视图模型：持有通过 DI 解析的 WatchLogService，
/// 便于在视图代码后置以外复用并消除手动 new 依赖。
/// </summary>
public class WatchDiaryViewModel
{
    public WatchLogService WatchLogService { get; }

    public WatchDiaryViewModel(WatchLogService watchLogService)
    {
        WatchLogService = watchLogService;
    }
}
