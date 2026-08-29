using EasyMovie.Core.Enums;

namespace EasyMovie.Core.Models;

/// <summary>
/// 推荐算法专用的窄投影行：只包含打分手���的标量字段。
///
/// 为什么需要它：推荐服务最终只返回 topN（默认 20）部电影，但原实现通过
/// GetAllAsync() 把全部影片连同 PosterData 一起读进内存（实测 96 KB/部，
/// 2000 部 ≈ 188 MB），只为最后挑 20 部——99% 的海报数据被读完即弃。
///
/// 正确做法是两阶段：先用本窄投影算出 topN 的 Id 与分数，
/// 再只对这 topN 部查询含海报的完整实体。
/// </summary>
public class MovieRecommendRow
{
    public int Id { get; set; }
    public int Year { get; set; }
    public int? Rating { get; set; }
    public WatchStatus WatchStatus { get; set; }
    public bool IsFavorite { get; set; }
    public string? Director { get; set; }
    public string? Country { get; set; }
    public int? CategoryId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>影片与标签的关联行（推荐算法按标签打分时需要）。</summary>
public class MovieTagLink
{
    public int MovieId { get; set; }
    public int TagId { get; set; }
}

/// <summary>
/// 推荐算法所需的全部轻量数据，一次查询取回，避免为每条影片做导航属性 JOIN。
/// </summary>
public class RecommendationData
{
    /// <summary>不含海报的窄投影影片行，按 CreatedAt 降序（与 GetAllAsync 顺序一致）。</summary>
    public List<MovieRecommendRow> Movies { get; set; } = new();

    /// <summary>影片-标签关联（独立于主查询，避免 JOIN 笛卡尔积）。</summary>
    public List<MovieTagLink> TagLinks { get; set; } = new();

    /// <summary>分类 Id → 名称（小表，用于生成推荐理由）。</summary>
    public Dictionary<int, string> CategoryNames { get; set; } = new();

    /// <summary>标签 Id → 名称（小表，用于生成推荐理由）。</summary>
    public Dictionary<int, string> TagNames { get; set; } = new();
}
