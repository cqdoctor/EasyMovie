using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using EasyMovie.Data;

namespace EasyMovie.Data.Migrations;

/// <summary>
/// 设计时 DbContext 工厂，用于 dotnet ef migrations 命令
/// </summary>
public class MovieDbContextFactory : IDesignTimeDbContextFactory<MovieDbContext>
{
    public MovieDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<MovieDbContext>();
        optionsBuilder.UseSqlite("Data Source=EasyMovie.db");
        return new MovieDbContext(optionsBuilder.Options);
    }
}
