namespace MovieManager.Core.Models;

/// <summary>
/// 电影-标签 多对多关联
/// </summary>
public class MovieTag
{
    public int MovieId { get; set; }
    public Movie Movie { get; set; } = null!;

    public int TagId { get; set; }
    public Tag Tag { get; set; } = null!;
}
