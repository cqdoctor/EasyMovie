using System;
using System.IO;
using Microsoft.EntityFrameworkCore;
using MovieManager.Data;

namespace MovieManager.Client;

public static class DbHelper
{
    private static readonly string DbDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MovieManager");
    private static readonly string DbPath = Path.Combine(DbDir, "MovieManager.db");

    public static string ConnectionString => $"Data Source={DbPath}";

    public static MovieDbContext CreateContext()
    {
        if (!Directory.Exists(DbDir)) Directory.CreateDirectory(DbDir);
        var options = new DbContextOptionsBuilder<MovieDbContext>().UseSqlite(ConnectionString).Options;
        var context = new MovieDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }
}
