using EasyMovie.Core.Models;
using FluentAssertions;
using Xunit;

namespace EasyMovie.Tests.Core.Tests;

/// <summary>
/// MovieFilterState 行为锁定测试。
///
/// 目的：把 MovieListView.xaml.cs（2300+ 行 code-behind，Client 层此前零测试覆盖）里的
/// 分页状态与翻页守卫逻辑（FirstPage_Click / PrevPage_Click / NextPage_Click /
/// LastPage_Click / JumpToPage）抽成可单元测试的纯状态类时，用测试钉住现有行为，
/// 确保搬运过程中没有改坏。
///
/// 断言描述的是**抽取前的真实行为**，不是理想行为——其中若干条是已知缺陷（已在注释中标明），
/// 将来若要修正语义，应显式修改对应测试并说明理由。
/// </summary>
public class MovieFilterStateTests
{
    // ───────────────────────── 常量与总页数 ─────────────────────────

    [Fact]
    public void PageSize_Is20() => new MovieFilterState().PageSize.Should().Be(20);

    [Fact]
    public void TotalPages_ZeroCount_ReturnsZero()
    {
        // 锁住原实现：totalPages = Ceiling(0/20) = 0。显示层的 Math.Max(1, totalPages) 守卫
        // 在 LoadMoviesAsync 的 PageInfo 里，不在翻页逻辑里——所以 TotalPages 本身可为 0。
        var s = new MovieFilterState { TotalCount = 0 };
        s.TotalPages.Should().Be(0);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(20, 1)]
    [InlineData(21, 2)]
    [InlineData(40, 2)]
    [InlineData(41, 3)]
    [InlineData(95, 5)]
    [InlineData(100, 5)]
    public void TotalPages_CeilingOfCountOverPageSize(int total, int expectedPages)
    {
        var s = new MovieFilterState { TotalCount = total };
        s.TotalPages.Should().Be(expectedPages);
    }

    // ───────────────────────── 翻页守卫 ─────────────────────────

    [Fact]
    public void CanGoPrev_OnFirstPage_False()
    {
        var s = new MovieFilterState { TotalCount = 100 };
        s.CanGoPrev.Should().BeFalse();
    }

    [Fact]
    public void CanGoPrev_AfterNext_True()
    {
        var s = new MovieFilterState { TotalCount = 100 };
        s.GoToNext();
        s.CanGoPrev.Should().BeTrue();
    }

    [Fact]
    public void CanGoNext_BeforeLastPage_True()
    {
        var s = new MovieFilterState { TotalCount = 100 }; // 5 页
        s.CurrentPage = 4;
        s.CanGoNext.Should().BeTrue();
    }

    [Fact]
    public void CanGoNext_OnLastPage_False()
    {
        var s = new MovieFilterState { TotalCount = 100 };
        s.GoToLast();
        s.CanGoNext.Should().BeFalse();
        s.CurrentPage.Should().Be(5);
    }

    // ───────────────────────── 翻页动作 ─────────────────────────

    [Fact]
    public void GoToFirst_ResetsTo1()
    {
        var s = new MovieFilterState { TotalCount = 100 };
        s.CurrentPage = 3;
        s.GoToFirst();
        s.CurrentPage.Should().Be(1);
    }

    [Fact]
    public void GoToPrev_Decrements()
    {
        var s = new MovieFilterState { TotalCount = 100 };
        s.CurrentPage = 3;
        s.GoToPrev();
        s.CurrentPage.Should().Be(2);
    }

    [Fact]
    public void GoToNext_Increments()
    {
        var s = new MovieFilterState { TotalCount = 100 };
        s.CurrentPage = 3;
        s.GoToNext();
        s.CurrentPage.Should().Be(4);
    }

    [Fact]
    public void GoToLast_GoesToTotalPages()
    {
        var s = new MovieFilterState { TotalCount = 95 }; // 5 页
        s.GoToLast();
        s.CurrentPage.Should().Be(5);
    }

    // ───────────────────────── 跳转夹紧（对应 JumpToPage） ─────────────────────────

    [Fact]
    public void GoToPage_WithinRange_SetsExact()
    {
        var s = new MovieFilterState { TotalCount = 100 };
        s.GoToPage(3);
        s.CurrentPage.Should().Be(3);
    }

    [Fact]
    public void GoToPage_AboveMax_ClampsToLast()
    {
        var s = new MovieFilterState { TotalCount = 100 }; // 5 页
        s.GoToPage(999);
        s.CurrentPage.Should().Be(5);
    }

    [Fact]
    public void GoToPage_BelowMin_ClampsToOne()
    {
        var s = new MovieFilterState { TotalCount = 100 };
        s.GoToPage(0);
        s.CurrentPage.Should().Be(1);
        s.GoToPage(-5);
        s.CurrentPage.Should().Be(1);
    }

    // ───────────────────────── 筛选变化重置 ─────────────────────────

    [Fact]
    public void ResetToFirstPage_AfterNavigating_ReturnsTo1()
    {
        var s = new MovieFilterState { TotalCount = 100 };
        s.GoToLast();
        s.ResetToFirstPage();
        s.CurrentPage.Should().Be(1);
    }

    // ───────────────────────── 视图模式（对应 _isCardView/_isPosterView/_isCollectionView） ─────────────────────────

    [Fact]
    public void DefaultViewMode_IsTable()
        => new MovieFilterState().ViewMode.Should().Be(ViewMode.Table);

    [Fact]
    public void Setters_ConfigureViewMode()
    {
        var s = new MovieFilterState();
        s.SetTable(); s.ViewMode.Should().Be(ViewMode.Table);
        s.SetCard(); s.ViewMode.Should().Be(ViewMode.Card);
        s.SetPoster(); s.ViewMode.Should().Be(ViewMode.Poster);
        s.SetCollection(); s.ViewMode.Should().Be(ViewMode.Collection);
    }

    [Fact]
    public void CycleViewMode_TableToCard()
    {
        var s = new MovieFilterState { ViewMode = ViewMode.Table };
        s.CycleViewMode();
        s.ViewMode.Should().Be(ViewMode.Card);
    }

    [Fact]
    public void CycleViewMode_CardToPoster()
    {
        var s = new MovieFilterState { ViewMode = ViewMode.Card };
        s.CycleViewMode();
        s.ViewMode.Should().Be(ViewMode.Poster);
    }

    [Fact]
    public void CycleViewMode_PosterToCollection()
    {
        var s = new MovieFilterState { ViewMode = ViewMode.Poster };
        s.CycleViewMode();
        s.ViewMode.Should().Be(ViewMode.Collection);
    }

    [Fact]
    public void CycleViewMode_CollectionToTable()
    {
        var s = new MovieFilterState { ViewMode = ViewMode.Collection };
        s.CycleViewMode();
        s.ViewMode.Should().Be(ViewMode.Table);
    }
}
