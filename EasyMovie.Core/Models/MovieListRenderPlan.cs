namespace EasyMovie.Core.Models;

/// <summary>
/// 一次 <c>SearchAsync</c> 结果应该灌进哪个控件容器。
/// 于 2026-09-03 从 MovieListView.LoadMoviesAsync 的 if/else 链中抽出（B2 切片4）。
/// </summary>
public enum MovieListItemsTarget
{
    /// <summary>表格视图（MovieDataGrid）。卡片视图走的是 RenderCardView 方法，不在本枚举覆盖范围。</summary>
    DataGrid,
    /// <summary>卡片视图（CardList），需调用 <c>RenderCardView</c> 而非直接赋 ItemsSource。</summary>
    CardList,
    /// <summary>海报墙（PosterWall）。</summary>
    PosterWall,
}

/// <summary>
/// 五个容器的显隐结果。于 2026-09-03 从 LoadMoviesAsync 的 5 条三元表达式中抽出（B2 切片4）。
/// 用 bool 而非 WPF 的 <c>Visibility</c> 枚举，因为 Core 不引用 WPF 类型（测试项目也未启用 UseWPF）。
/// </summary>
/// <param name="Table">MovieDataGrid 是否显示</param>
/// <param name="Card">CardList 是否显示</param>
/// <param name="Poster">PosterWall 是否显示</param>
/// <param name="Collection">CollectionScrollViewer 是否显示</param>
/// <param name="Empty">EmptyLabel（「没有匹配的电影」提示）是否显示</param>
public readonly record struct MovieListVisibility(
    bool Table, bool Card, bool Poster, bool Collection, bool Empty);

/// <summary>
/// 电影库列表「渲染决策」的纯计算部分。于 2026-09-03 抽出（B2 切片4），
/// 把 LoadMoviesAsync 里无法测试的 WPF 三元表达式变成可单测的纯函数。
///
/// 这是 B2 中**风险最高、最需要测试锁定**的一块：视图模式（Table/Card/Poster/Collection）
/// 刚从三个互斥 bool 标志重构为 <see cref="ViewMode"/> 枚举（切片2），而 GUI 尚未真机验证。
/// 显隐矩阵一旦算错，用户看到的就是整块空白。
///
/// 全部为逐字节复刻原实现语义，含已知怪异之处（见各方法注释）。锁住现状、不擅自改语义。
/// </summary>
public static class MovieListRenderPlan
{
    /// <summary>
    /// 计算五个容器的显隐。
    /// 逐字节复刻原实现：
    /// <code>
    /// MovieDataGrid.Visibility = (mode == Table) &amp;&amp; hasMovies ? Visible : Collapsed;
    /// CardList.Visibility      = (mode == Card)  &amp;&amp; hasMovies ? Visible : Collapsed;
    /// PosterWall.Visibility    = (mode == Poster) &amp;&amp; hasMovies ? Visible : Collapsed;
    /// EmptyLabel.Visibility    = hasMovies || (mode == Collection) ? Collapsed : Visible;
    /// CollectionScrollViewer.Visibility = (mode == Collection) ? Visible : Collapsed;
    /// </code>
    /// 已知怪异（原实现即如此，本次只锁定）：<b>合集视图（Collection）不参与空态判断</b>——
    /// 即便一部电影都没有，也不显示「没有匹配的电影」，而是显示空的合集面板。
    /// </summary>
    public static MovieListVisibility ResolveVisibility(ViewMode mode, bool hasMovies)
        => new(
            Table: mode == ViewMode.Table && hasMovies,
            Card: mode == ViewMode.Card && hasMovies,
            Poster: mode == ViewMode.Poster && hasMovies,
            Collection: mode == ViewMode.Collection,
            Empty: !hasMovies && mode != ViewMode.Collection);

    /// <summary>
    /// 结果集该灌进哪个容器。
    /// 逐字节复刻原实现 <c>if (card) RenderCardView() else if (poster) PosterWall.ItemsSource = ... else MovieDataGrid.ItemsSource = ...</c>。
    /// 已知怪异（原实现即如此）：<b>合集视图没有独立分支，落进 else 即灌给 MovieDataGrid</b>，
    /// 而 MovieDataGrid 在合集视图下是隐藏的（见 <see cref="ResolveVisibility"/>）。
    /// 即合集视图时数据被塞进一个不可见的 DataGrid——目前无害（合集面板自己另有一套数据源），
    /// 但这是潜在的理解陷阱，故在此显式记录。
    /// </summary>
    public static MovieListItemsTarget ResolveItemsTarget(ViewMode mode)
        => mode switch
        {
            ViewMode.Card => MovieListItemsTarget.CardList,
            ViewMode.Poster => MovieListItemsTarget.PosterWall,
            _ => MovieListItemsTarget.DataGrid,
        };
}
