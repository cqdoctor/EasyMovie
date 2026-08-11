using EasyMovie.Data;

namespace EasyMovie.Client.ViewModels;

/// <summary>
/// WatchHeatmapView 的视图模型：持有通过 DI 解析的 MovieDbContext，
/// 用于内联查询观看记录（上下文随视图生命周期，配合写串行化锁安全）。
/// </summary>
public class WatchHeatmapViewModel
{
    public MovieDbContext Context { get; }

    public WatchHeatmapViewModel(MovieDbContext context)
    {
        Context = context;
    }
}
