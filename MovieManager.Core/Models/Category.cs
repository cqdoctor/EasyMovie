namespace MovieManager.Core.Models;

/// <summary>
/// 电影分类（支持多级层级）
/// </summary>
public class Category
{
    public int Id { get; set; }

    /// <summary>分类名称</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>分类描述</summary>
    public string? Description { get; set; }

    // ── 层级支持 ──
    /// <summary>父分类 ID（null 表示一级分类）</summary>
    public int? ParentId { get; set; }

    /// <summary>父分类</summary>
    public Category? Parent { get; set; }

    /// <summary>子分类</summary>
    public ICollection<Category> Children { get; set; } = new List<Category>();

    // ── 导航 ──
    /// <summary>该分类下的电影</summary>
    public ICollection<Movie> Movies { get; set; } = new List<Movie>();

    // ── 时间戳 ──
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
