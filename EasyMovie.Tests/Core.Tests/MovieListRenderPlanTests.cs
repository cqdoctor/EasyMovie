using EasyMovie.Core.Models;
using Xunit;

namespace EasyMovie.Tests.Core.Tests;

/// <summary>
/// MovieListRenderPlan 行为锁定测试（B2 切片4，2026-09-03）。
///
/// 这是 B2 四个切片里**风险最高**的一块：视图模式刚从三个互斥 bool 标志重构为
/// <see cref="ViewMode"/> 枚举（切片2），而 GUI 尚未真机验证；显隐矩阵算错的直接后果
/// 就是用户看到整块空白。此前这 5 条三元表达式写在 code-behind 里，零测试覆盖。
///
/// 测试策略：把 4 种视图模式 × 2 种数据有无 = 8 种组合**全枚举**，逐格断言五个容器的
/// 显隐；再用两条专项测试锁住原实现的两处怪异行为（Collection 不参与空态、
/// Collection 的数据被灌进隐藏的 DataGrid），防止后续重构被"顺手修好"而无人察觉。
/// </summary>
public class MovieListRenderPlanTests
{
    // ══════════════════════════════════════════════════════════════
    // 显隐矩阵：4 模式 × 2 数据状态
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Visibility_Table_WithMovies_ShowsOnlyGrid()
    {
        var v = MovieListRenderPlan.ResolveVisibility(ViewMode.Table, true);
        Assert.True(v.Table);
        Assert.False(v.Card);
        Assert.False(v.Poster);
        Assert.False(v.Collection);
        Assert.False(v.Empty);
    }

    [Fact]
    public void Visibility_Table_NoMovies_ShowsOnlyEmptyLabel()
    {
        var v = MovieListRenderPlan.ResolveVisibility(ViewMode.Table, false);
        Assert.False(v.Table);
        Assert.False(v.Card);
        Assert.False(v.Poster);
        Assert.False(v.Collection);
        Assert.True(v.Empty);
    }

    [Fact]
    public void Visibility_Card_WithMovies_ShowsOnlyCardList()
    {
        var v = MovieListRenderPlan.ResolveVisibility(ViewMode.Card, true);
        Assert.False(v.Table);
        Assert.True(v.Card);
        Assert.False(v.Poster);
        Assert.False(v.Collection);
        Assert.False(v.Empty);
    }

    [Fact]
    public void Visibility_Card_NoMovies_ShowsOnlyEmptyLabel()
    {
        var v = MovieListRenderPlan.ResolveVisibility(ViewMode.Card, false);
        Assert.False(v.Table);
        Assert.False(v.Card);
        Assert.False(v.Poster);
        Assert.False(v.Collection);
        Assert.True(v.Empty);
    }

    [Fact]
    public void Visibility_Poster_WithMovies_ShowsOnlyPosterWall()
    {
        var v = MovieListRenderPlan.ResolveVisibility(ViewMode.Poster, true);
        Assert.False(v.Table);
        Assert.False(v.Card);
        Assert.True(v.Poster);
        Assert.False(v.Collection);
        Assert.False(v.Empty);
    }

    [Fact]
    public void Visibility_Poster_NoMovies_ShowsOnlyEmptyLabel()
    {
        var v = MovieListRenderPlan.ResolveVisibility(ViewMode.Poster, false);
        Assert.False(v.Table);
        Assert.False(v.Card);
        Assert.False(v.Poster);
        Assert.False(v.Collection);
        Assert.True(v.Empty);
    }

    [Fact]
    public void Visibility_Collection_WithMovies_ShowsCollectionPanel()
    {
        var v = MovieListRenderPlan.ResolveVisibility(ViewMode.Collection, true);
        Assert.False(v.Table);
        Assert.False(v.Card);
        Assert.False(v.Poster);
        Assert.True(v.Collection);
        Assert.False(v.Empty);
    }

    /// <summary>
    /// 怪异行为锁定①：合集视图**不参与空态判断**——一部电影都没有时也不显示
    /// 「没有匹配的电影」提示，只显示空的合集面板。原实现即如此。
    /// 若日后判定这是 bug，改实现的同时必须改本测试，让改动显式可见。
    /// </summary>
    [Fact]
    public void Visibility_Collection_NoMovies_StillHidesEmptyLabel()
    {
        var v = MovieListRenderPlan.ResolveVisibility(ViewMode.Collection, false);
        Assert.True(v.Collection);
        Assert.False(v.Empty);
    }

    /// <summary>
    /// 不变量：Table/Card/Poster 三种模式下，任意时刻「有数据」与「空提示」恰好显示一个，
    /// 不会出现既空着又没提示的死局面（合集视图不受此约束，见上面的怪异行为①）。
    /// </summary>
    [Theory]
    [InlineData(ViewMode.Table)]
    [InlineData(ViewMode.Card)]
    [InlineData(ViewMode.Poster)]
    public void Visibility_NonCollectionModes_EmptyLabelIsComplementOfContent(ViewMode mode)
    {
        var withMovies = MovieListRenderPlan.ResolveVisibility(mode, true);
        var without = MovieListRenderPlan.ResolveVisibility(mode, false);

        Assert.False(withMovies.Empty);
        Assert.True(without.Empty);
        // 有数据时恰好一个内容容器可见
        Assert.Equal(1, CountVisible(withMovies));
        // 无数据时内容容器全隐藏
        Assert.Equal(0, CountVisible(without));
    }

    private static int CountVisible(MovieListVisibility v)
        => (v.Table ? 1 : 0) + (v.Card ? 1 : 0) + (v.Poster ? 1 : 0) + (v.Collection ? 1 : 0);

    // ══════════════════════════════════════════════════════════════
    // 结果集投递目标
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void ItemsTarget_Card_GoesToCardList()
        => Assert.Equal(MovieListItemsTarget.CardList,
            MovieListRenderPlan.ResolveItemsTarget(ViewMode.Card));

    [Fact]
    public void ItemsTarget_Poster_GoesToPosterWall()
        => Assert.Equal(MovieListItemsTarget.PosterWall,
            MovieListRenderPlan.ResolveItemsTarget(ViewMode.Poster));

    [Fact]
    public void ItemsTarget_Table_GoesToDataGrid()
        => Assert.Equal(MovieListItemsTarget.DataGrid,
            MovieListRenderPlan.ResolveItemsTarget(ViewMode.Table));

    /// <summary>
    /// 怪异行为锁定②：合集视图**没有独立分支**，落进 else 即把结果集灌给 MovieDataGrid，
    /// 而 MovieDataGrid 在合集视图下是隐藏的。目前无害（合集面板另有一套数据源），
    /// 但这是理解陷阱，显式记录在此。
    /// </summary>
    [Fact]
    public void ItemsTarget_Collection_FallsThroughToHiddenDataGrid()
        => Assert.Equal(MovieListItemsTarget.DataGrid,
            MovieListRenderPlan.ResolveItemsTarget(ViewMode.Collection));

    /// <summary>枚举值超出已知范围时回落到 DataGrid，不抛异常（原 if/else 链的 else 分支语义）。</summary>
    [Fact]
    public void ItemsTarget_UnknownValue_FallsBackToDataGrid()
        => Assert.Equal(MovieListItemsTarget.DataGrid,
            MovieListRenderPlan.ResolveItemsTarget((ViewMode)99));

    // ══════════════════════════════════════════════════════════════
    // MovieFilterState.DisplayTotalPages：显示层的「至少 1 页」夹紧
    // ══════════════════════════════════════════════════════════════

    /// <summary>命中 0 条时 TotalPages 为 0，但显示给用户的必须是 1（否则出现「第 1/0 页」）。</summary>
    [Fact]
    public void DisplayTotalPages_ZeroCount_ClampsToOne()
    {
        var s = new MovieFilterState { TotalCount = 0 };
        Assert.Equal(0, s.TotalPages);
        Assert.Equal(1, s.DisplayTotalPages);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(20, 1)]
    [InlineData(21, 2)]
    [InlineData(289, 15)]
    [InlineData(290, 15)]
    [InlineData(291, 15)]
    public void DisplayTotalPages_MatchesCeilingWithLowerBoundOne(int totalCount, int expected)
    {
        var s = new MovieFilterState { TotalCount = totalCount };
        Assert.Equal(expected, s.DisplayTotalPages);
    }

    /// <summary>DisplayTotalPages 永不小于 1，也永不小于 TotalPages。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(999)]
    public void DisplayTotalPages_NeverBelowOne(int totalCount)
    {
        var s = new MovieFilterState { TotalCount = totalCount };
        Assert.True(s.DisplayTotalPages >= 1);
        Assert.True(s.DisplayTotalPages >= s.TotalPages);
    }
}
