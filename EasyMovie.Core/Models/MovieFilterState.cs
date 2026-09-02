using System;

namespace EasyMovie.Core.Models;

/// <summary>
/// 电影库列表的「分页状态 + 翻页编排」。
///
/// 于 2026-09-02 从 MovieListView.xaml.cs 的零散 private 字段
/// （<c>_currentPage</c> / <c>_totalCount</c> / <c>PageSize</c>）与翻页点击逻辑
/// （<c>FirstPage_Click</c> / <c>PrevPage_Click</c> / <c>NextPage_Click</c> /
/// <c>LastPage_Click</c> / <c>JumpToPage</c>）中抽出。
///
/// 这是 B2（整体抽 <c>MovieFilterState</c> / <c>MovieListViewModel</c>）的**第一步切片**：
/// 先把不含 WPF 类型、可单元测试的分页与翻页守卫逻辑隔离出来。后续切片（筛选值读取仍来自控件、
/// 视图模式三标志、<c>LoadMoviesAsync</c> 加载编排）在此基础上累加。
///
/// 全部为逐字节复刻原实现语义，含已知不完美之处（见各成员注释）。锁住现状、不擅自改语义——
/// 要改语义是另一次独立变更（参照 MovieQueryBuilder 的抽取范式）。
/// </summary>
public class MovieFilterState
{
    /// <summary>每页条数（原 <c>PageSize</c> 常量）。保留为实例属性以便通过 <c>_filterState.PageSize</c> 统一访问。</summary>
    public int PageSize => 20;

    private int _currentPage = 1;
    private int _totalCount;

    /// <summary>当前页码，从 1 开始（原默认 1）。setter 不做夹紧——直接赋值是原实现语义，越界防护统一由 <see cref="GoToPage"/> 负责。</summary>
    public int CurrentPage
    {
        get => _currentPage;
        set => _currentPage = value;
    }

    /// <summary>总命中数（一次 <c>SearchAsync</c> 返回的 total）。原 <c>_totalCount</c>。</summary>
    public int TotalCount
    {
        get => _totalCount;
        set => _totalCount = value;
    }

    /// <summary>
    /// 总页数 = Ceiling(TotalCount / PageSize)。原实现里 <c>JumpToPage</c> / 翻页点击中各处重复计算的 <c>totalPages</c>。
    /// 已知行为：TotalCount 为 0 时返回 0（原实现即如此，<c>Math.Max(1, totalPages)</c> 的「至少 1 页」
    /// 守卫只在 <c>LoadMoviesAsync</c> 的 PageInfo 显示层做，不在翻页逻辑里）。因此 TotalPages 可能为 0，
    /// 调用方（如 GoToPage 的夹紧上界）需知晓此边界。锁住现状，不擅自修正。
    /// </summary>
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);

    // ───────────────── 翻页守卫（原点击逻辑里的 if 条件） ─────────────────

    /// <summary>能否往前翻：CurrentPage &gt; 1。对应 FirstPage_Click / PrevPage_Click 的 <c>_currentPage &gt; 1</c>。</summary>
    public bool CanGoPrev => _currentPage > 1;

    /// <summary>能否往后翻：CurrentPage &lt; TotalPages。对应 NextPage_Click / LastPage_Click 的 <c>_currentPage &lt; tp</c>。</summary>
    public bool CanGoNext => _currentPage < TotalPages;

    // ───────────────── 翻页动作（原点击逻辑里的赋值；调用方需先判对应守卫） ─────────────────

    /// <summary>回到首页。对应 FirstPage_Click 的 <c>_currentPage = 1</c>。</summary>
    public void GoToFirst() => _currentPage = 1;

    /// <summary>上一页（CurrentPage--）。对应 PrevPage_Click；调用方需先判 <see cref="CanGoPrev"/>。</summary>
    public void GoToPrev() => _currentPage--;

    /// <summary>下一页（CurrentPage++）。对应 NextPage_Click；调用方需先判 <see cref="CanGoNext"/>。</summary>
    public void GoToNext() => _currentPage++;

    /// <summary>末页（CurrentPage = TotalPages）。对应 LastPage_Click；调用方需先判 <see cref="CanGoNext"/>。</summary>
    public void GoToLast() => _currentPage = TotalPages;

    /// <summary>
    /// 跳到指定页并夹紧到 [1, TotalPages]。对应 JumpToPage 的 page 修正逻辑。
    /// 已知行为：page 越界时静默夹紧，不抛异常；page&lt;1 落到 1，page&gt;TotalPages 落到 TotalPages。
    /// </summary>
    public void GoToPage(int page)
    {
        if (page < 1) page = 1;
        if (page > TotalPages) page = TotalPages;
        _currentPage = page;
    }

    /// <summary>重置到首页（筛选/快速筛选变化时调用）。对应各处 <c>_currentPage = 1</c>。</summary>
    public void ResetToFirstPage() => _currentPage = 1;

    // ───────────────── 视图模式（原 _isCardView / _isPosterView / _isCollectionView 三互斥标志） ─────────────────

    private ViewMode _viewMode = ViewMode.Table;

    /// <summary>当前视图呈现模式。原三互斥 bool 标志的等价表示（全 false = Table）。</summary>
    public ViewMode ViewMode
    {
        get => _viewMode;
        set => _viewMode = value;
    }

    /// <summary>切到表格视图。对应 TableViewBtn_Click。</summary>
    public void SetTable() => _viewMode = ViewMode.Table;
    /// <summary>切到卡片视图。对应 CardViewBtn_Click。</summary>
    public void SetCard() => _viewMode = ViewMode.Card;
    /// <summary>切到海报墙。对应 PosterViewBtn_Click。</summary>
    public void SetPoster() => _viewMode = ViewMode.Poster;
    /// <summary>切到合集视图。对应 CollectionView_Click。</summary>
    public void SetCollection() => _viewMode = ViewMode.Collection;

    /// <summary>
    /// 视图模式循环切换：Table → Card → Poster → Collection → Table。
    /// 逐字节复刻原 <c>CycleView()</c> 的四分支 if/else（当前 Table 时落 Card，Card→Poster，
    /// Poster→Collection，其余即 Collection→Table）。
    /// </summary>
    public void CycleViewMode() => _viewMode = _viewMode switch
    {
        ViewMode.Table => ViewMode.Card,
        ViewMode.Card => ViewMode.Poster,
        ViewMode.Poster => ViewMode.Collection,
        ViewMode.Collection => ViewMode.Table,
        _ => ViewMode.Table,
    };
}
