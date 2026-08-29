using EasyMovie.Core.Models;
using Xunit;

namespace EasyMovie.Tests.Core.Tests;

/// <summary>
/// SavedFilter 持久化行为锁定测试。
///
/// 所有用例都通过 path 参数注入临时目录，**绝不触碰用户真实的 saved_filters.json**
/// （%LocalAppData%/EasyMovie/saved_filters.json）。
///
/// 这些断言描述的是**抽取前的真实行为**，不是理想行为——若将来要改变语义，
/// 应显式修改本测试并说明理由。
/// </summary>
public class SavedFilterTests : IDisposable
{
    private const string RootDirName = "EasyMovieSavedFilterTests";
    private readonly string _dir;
    private readonly string _path;

    public SavedFilterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), RootDirName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "saved_filters.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
            // 顺带清掉空的根壳目录；并发测试可能仍在占用，失败即忽略
            var root = Path.Combine(Path.GetTempPath(), RootDirName);
            if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
                Directory.Delete(root);
        }
        catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void LoadAll_FileMissing_ReturnsEmptyList()
    {
        var filters = SavedFilter.LoadAll(Path.Combine(_dir, "nonexistent.json"));
        Assert.NotNull(filters);
        Assert.Empty(filters);
    }

    [Fact]
    public void SaveAll_ThenLoadAll_RoundTripsAllFields()
    {
        var original = new List<SavedFilter>
        {
            new()
            {
                Name = "我的动作片",
                Keyword = "速度与激情",
                CategoryId = 7,
                Status = "Watched",
                YearFrom = 2001,
                YearTo = 2024,
                RatingMin = 6,
                RatingMax = 10,
                Countries = new List<string> { "美国", "中国" },
                Languages = new List<string> { "英语", "普通话" },
                RuntimeMin = 90,
                RuntimeMax = 180,
                Directors = new List<string> { "林诣彬" },
                SortBy = "Rating",
                SortDesc = true
            }
        };

        SavedFilter.SaveAll(original, _path);
        var loaded = SavedFilter.LoadAll(_path);

        Assert.Single(loaded);
        var f = loaded[0];
        Assert.Equal("我的动作片", f.Name);
        Assert.Equal("速度与激情", f.Keyword);
        Assert.Equal(7, f.CategoryId);
        Assert.Equal("Watched", f.Status);
        Assert.Equal(2001, f.YearFrom);
        Assert.Equal(2024, f.YearTo);
        Assert.Equal(6, f.RatingMin);
        Assert.Equal(10, f.RatingMax);
        Assert.Equal(new[] { "美国", "中国" }, f.Countries);
        Assert.Equal(new[] { "英语", "普通话" }, f.Languages);
        Assert.Equal(90, f.RuntimeMin);
        Assert.Equal(180, f.RuntimeMax);
        Assert.Equal(new[] { "林诣彬" }, f.Directors);
        Assert.Equal("Rating", f.SortBy);
        Assert.True(f.SortDesc);
    }

    /// <summary>JSON 损坏时必须返回空列表而不是抛异常，否则一个坏文件会让整个筛选功能不可用。</summary>
    [Fact]
    public void LoadAll_CorruptedJson_ReturnsEmptyList_WithoutThrowing()
    {
        File.WriteAllText(_path, "{ this is not valid json ][");

        var filters = SavedFilter.LoadAll(_path);

        Assert.NotNull(filters);
        Assert.Empty(filters);
    }

    /// <summary>空数组反序列化后应为 null 或空列表，两种情况都要容错成空列表。</summary>
    [Fact]
    public void LoadAll_NullJson_ReturnsEmptyList()
    {
        File.WriteAllText(_path, "null");
        Assert.Empty(SavedFilter.LoadAll(_path));
    }

    [Fact]
    public void SaveAll_EmptyList_ThenLoadAll_ReturnsEmpty()
    {
        SavedFilter.SaveAll(new List<SavedFilter>(), _path);
        Assert.Empty(SavedFilter.LoadAll(_path));
    }

    /// <summary>目标目录不存在时 SaveAll 应自动创建。</summary>
    [Fact]
    public void SaveAll_MissingDirectory_CreatesIt()
    {
        var nested = Path.Combine(_dir, "a", "b", "saved_filters.json");

        SavedFilter.SaveAll(new List<SavedFilter> { new() { Name = "x" } }, nested);

        Assert.True(File.Exists(nested));
        Assert.Single(SavedFilter.LoadAll(nested));
    }

    /// <summary>保存会覆盖旧内容（不是追加），否则重复保存会累积重复项。</summary>
    [Fact]
    public void SaveAll_OverwritesPreviousContent()
    {
        SavedFilter.SaveAll(new List<SavedFilter>
            { new() { Name = "A" }, new() { Name = "B" } }, _path);
        SavedFilter.SaveAll(new List<SavedFilter> { new() { Name = "C" } }, _path);

        var loaded = SavedFilter.LoadAll(_path);
        Assert.Single(loaded);
        Assert.Equal("C", loaded[0].Name);
    }

    /// <summary>
    /// 默认路径应指向用户的 %LocalAppData%/EasyMovie/saved_filters.json。
    /// 本用例只校验路径结构，**不读写该文件**。
    /// </summary>
    [Fact]
    public void DefaultSavePath_PointsToLocalAppData()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EasyMovie", "saved_filters.json");

        Assert.Equal(expected, SavedFilter.DefaultSavePath);
    }
}
