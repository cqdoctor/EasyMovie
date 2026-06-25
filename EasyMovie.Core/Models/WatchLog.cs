namespace EasyMovie.Core.Models;

public class WatchLog
{
    public int Id { get; set; }

    public int MovieId { get; set; }
    public Movie Movie { get; set; } = null!;

    public DateTime WatchDate { get; set; } = DateTime.Today;

    public int? Rating { get; set; }

    public string? Location { get; set; }

    public string? Companion { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
