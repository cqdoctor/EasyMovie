using System.Collections.Generic;
using System.Linq;
using EasyMovie.Core.Enums;

namespace EasyMovie.Core.Helpers;

/// <summary>
/// 电影库列表「查询条件解析」：把 WPF 控件里取出的原始值（ComboBoxItem.Tag / 滑块数值 /
/// 文本框内容）翻译成传给 <c>IMovieService.SearchAsync</c> 的参数。
///
/// 于 2026-08-31 从 MovieListView.xaml.cs 的
/// <c>GetFilterValues / GetYearFilter / GetSortInfo / GetAdvancedFilterValues / GetMultiSelectValues</c>
/// 中抽出纯逻辑部分，以便纳入单元测试保护。
///
/// 设计边界：**这里只有纯函数，不碰任何 WPF 类型**。控件读取仍留在 View（
/// <c>SearchBox.Text</c>、<c>xxxFilter.SelectedItem</c>、<c>RangeSlider.LowerValue</c> 等），
/// 因为测试项目面向 net10.0 且未启用 UseWPF，沾上 <c>System.Windows.Controls</c> 就编译不过。
/// 因此入参一律是 <see cref="object"/>（Tag 的静态类型），与本类在 View 中的调用形态一致。
///
/// 所有方法均为**逐字节复刻原实现**，包括已知的不完美之处（见各方法的「已知行为」注释）。
/// 锁住现状、不擅自改语义——要改语义是另一次独立变更。
/// </summary>
public static class MovieQueryBuilder
{
    /// <summary>排序默认值：新增时间倒序。原实现在 Tag 无法解析时回落到此值。</summary>
    public const string DefaultSortBy = "createdat";
    public const bool DefaultSortDesc = true;

    /// <summary>多选下拉中表示「全部」的哨兵 Tag，需从结果中剔除。</summary>
    public const string AllItemsTag = "_all";

    /// <summary>
    /// 关键词归一化：空白或 null 视为「不筛选」，否则去首尾空格。
    /// </summary>
    public static string? NormalizeKeyword(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>
    /// 从 ComboBoxItem.Tag 取分类 Id。Tag 非 int（未选中 / 是「全部分类」项）时为 null。
    /// </summary>
    public static int? ParseIdTag(object? tag) => tag is int id ? id : null;

    /// <summary>
    /// 从 ComboBoxItem.Tag 取观看状态。
    /// 已知行为：Tag 是其它字符串或类型时返回 null（不筛选），不抛异常。
    /// </summary>
    public static WatchStatus? ParseStatusTag(object? tag) => tag switch
    {
        "NotWatched" => WatchStatus.NotWatched,
        "WantToWatch" => WatchStatus.WantToWatch,
        "Watched" => WatchStatus.Watched,
        _ => null,
    };

    /// <summary>
    /// 解析排序 Tag，格式为 <c>"{字段}_{方向}"</c>，如 <c>"title_asc"</c>、<c>"createdat_desc"</c>。
    ///
    /// 已知行为（原样保留，测试已锁定）：
    /// 1. 以 '_' 分割后必须**恰好 2 段**，否则整体回落默认值——因此含下划线的字段名（如
    ///    "date_added_desc"）会被判为无效并静默回到默认排序。
    /// 2. 方向段只认**小写 "desc"**，"DESC" / "Desc" 一律按升序处理（大小写敏感）。
    /// </summary>
    public static (string sortBy, bool sortDesc) ParseSortTag(object? tag)
    {
        if (tag is string st)
        {
            var parts = st.Split('_');
            if (parts.Length == 2) return (parts[0], parts[1] == "desc");
        }
        return (DefaultSortBy, DefaultSortDesc);
    }

    /// <summary>
    /// 区间滑块下界：滑块停在最小值上视为「不限制」，返回 null。
    /// 已知行为：因此**无法表达「从最小年份开始」这个筛选意图**——把下界拖到最左端
    /// 等价于不加下界（会回落到年份下拉框的值）。判据是严格大于，故意不改成大于等于。
    /// </summary>
    public static int? LowerBound(double value, double minimum)
        => value > minimum ? (int)value : null;

    /// <summary>
    /// 区间滑块上界：滑块停在最大值上视为「不限制」，返回 null。
    /// 与 <see cref="LowerBound"/> 对称，判据是严格小于。
    /// </summary>
    public static int? UpperBound(double value, double maximum)
        => value < maximum ? (int)value : null;

    /// <summary>
    /// 多选控件取值：剔除「全部」哨兵项；一个都没选时返回 null（表示该维度不参与筛选）。
    /// </summary>
    /// <param name="tags">选中项的 Tag 序列；非字符串项会被忽略。</param>
    public static List<string>? NormalizeMultiSelect(IEnumerable<object?>? tags)
    {
        if (tags == null) return null;
        var items = tags
            .OfType<string>()
            .Where(s => s != AllItemsTag)
            .ToList();
        return items.Count > 0 ? items : null;
    }
}
