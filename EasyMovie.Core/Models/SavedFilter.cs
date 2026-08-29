using System.Text.Json;
using Serilog;

namespace EasyMovie.Core.Models;

/// <summary>
/// 电影库「已保存的筛选条件」及其本地持久化。
///
/// 于 2026-08-29 从 MovieListView.xaml.cs 的私有嵌套类 SavedFilter 抽出
/// （2557 行 code-behind 的一部分），以便纳入单元测试保护。
///
/// 持久化行为与原实现一致：读写 %LocalAppData%/EasyMovie/saved_filters.json，
/// 读取失败（文件缺失或 JSON 损坏）时返回空列表而不抛异常。
///
/// 相较原实现新增的可测试性改动（**唯一的行为差异，向后兼容**）：
/// LoadAll/SaveAll 增加可选参数 path，便于测试注入临时目录。
/// 不传时行为与原来完全一致（仍写用户的 saved_filters.json）。
/// 这样测试就不会触碰真实的用户数据文件。
/// </summary>
public class SavedFilter
{
    public string Name { get; set; } = "";
    public string? Keyword { get; set; }
    public int? CategoryId { get; set; }
    public string? Status { get; set; }
    public int? YearFrom { get; set; }
    public int? YearTo { get; set; }
    public int? RatingMin { get; set; }
    public int? RatingMax { get; set; }
    public List<string>? Countries { get; set; }
    public List<string>? Languages { get; set; }
    public int? RuntimeMin { get; set; }
    public int? RuntimeMax { get; set; }
    public List<string>? Directors { get; set; }
    public string? SortBy { get; set; }
    public bool SortDesc { get; set; }

    /// <summary>默认存储位置（不传 path 时使用）。</summary>
    public static string DefaultSavePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EasyMovie", "saved_filters.json");

    /// <summary>
    /// 读取已保存的筛选条件。文件缺失或 JSON 损坏时返回空列表（不抛异常）。
    /// </summary>
    /// <param name="path">仅测试用：指定替代的存储路径。</param>
    public static List<SavedFilter> LoadAll(string? path = null)
    {
        var p = path ?? DefaultSavePath;
        try
        {
            if (!File.Exists(p)) return new List<SavedFilter>();
            var json = File.ReadAllText(p);
            return JsonSerializer.Deserialize<List<SavedFilter>>(json) ?? new List<SavedFilter>();
        }
        catch (Exception ex) { Log.Error(ex, "加载已保存筛选失败"); return new List<SavedFilter>(); }
    }

    /// <summary>
    /// 写入筛选条件（覆盖写）。目录不存在时自动创建；写入失败仅记日志，不抛异常。
    /// </summary>
    /// <param name="path">仅测试用：指定替代的存储路径。</param>
    public static void SaveAll(List<SavedFilter> filters, string? path = null)
    {
        var p = path ?? DefaultSavePath;
        try
        {
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(filters, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(p, json);
        }
        catch (Exception ex) { Log.Error(ex, "保存筛选条件失败"); }
    }
}
