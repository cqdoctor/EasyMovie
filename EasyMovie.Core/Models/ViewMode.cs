namespace EasyMovie.Core.Models;

/// <summary>
/// 电影库列表的视图呈现模式，互斥。于 2026-09-02 从 MovieListView.xaml.cs 的
/// <c>_isCardView</c> / <c>_isPosterView</c> / <c>_isCollectionView</c> 三个互斥 bool 标志抽出，
/// 归入 B2（整体抽 MovieFilterState / MovieListViewModel）的视图模式切片。
/// </summary>
public enum ViewMode
{
    /// <summary>表格视图（原三者全 false）。</summary>
    Table,
    /// <summary>卡片视图（原 _isCardView）。</summary>
    Card,
    /// <summary>海报墙（原 _isPosterView）。</summary>
    Poster,
    /// <summary>合集视图（原 _isCollectionView）。</summary>
    Collection,
}
