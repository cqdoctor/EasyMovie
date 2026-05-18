using Microsoft.EntityFrameworkCore;
using MovieManager.Core.Models;
using MovieManager.Data.Configurations;

namespace MovieManager.Data;

public class MovieDbContext : DbContext
{
    public DbSet<Movie> Movies => Set<Movie>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<MovieTag> MovieTags => Set<MovieTag>();

    public MovieDbContext(DbContextOptions<MovieDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfiguration(new MovieConfiguration());
        modelBuilder.ApplyConfiguration(new CategoryConfiguration());
        modelBuilder.ApplyConfiguration(new TagConfiguration());
        modelBuilder.ApplyConfiguration(new MovieTagConfiguration());
    }
}
