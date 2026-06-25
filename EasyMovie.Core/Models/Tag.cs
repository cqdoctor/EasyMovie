namespace EasyMovie.Core.Models;

/// <summary>
/// 电影标签
/// </summary>
public class Tag
{
    public int Id { get; set; }

    /// <summary>标签名称</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>标签颜色（Hex 格式，如 #FF5722）</summary>
    public string? Color { get; set; }

    // ── 导航 ──
    public ICollection<MovieTag> MovieTags { get; set; } = new List<MovieTag>();

    // ── 时间戳 ──
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
