using EasyMovie.Data;

namespace EasyMovie.Client.ViewModels;

/// <summary>
/// DashboardView 的视图模型：持有通过 DI 解析的 MovieDbContext，
/// 用于仪表盘统计查询（上下文随视图生命周期，配合写串行化锁安全）。
/// </summary>
public class DashboardViewModel
{
    public MovieDbContext Context { get; }

    public DashboardViewModel(MovieDbContext context)
    {
        Context = context;
    }
}
