using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;

namespace EasyMovie.Client.Helpers;

/// <summary>
/// 海报磁盘缓存层：按 Movie.Id 把海报字节落地到 LocalApplicationData/EasyMovie/Posters/{id}.jpg。
/// 作为 DB 中 PosterData 的“离线加速 / 下载去重”层；PosterData 始终保留为真相源与回退，
/// 本层任何读写失败都被吞掉，绝不影响主流程与其它功能。
/// </summary>
public static class PosterCache
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EasyMovie", "Posters");

    public static string PathFor(int id) => Path.Combine(CacheDir, $"{id}.jpg");

    public static bool Exists(int id) => File.Exists(PathFor(id));

    /// <summary>把海报字节写盘（覆盖式）。参数无效或失败均忽略。</summary>
    public static void Save(int id, byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return;
        try
        {
            Directory.CreateDirectory(CacheDir);
            File.WriteAllBytes(PathFor(id), bytes);
        }
        catch (Exception ex) { Log.Error(ex, "PosterCache 写盘失败(已忽略)"); }
    }

    /// <summary>读取磁盘缓存字节；不存在或损坏返回 null（调用方须回退 PosterData/PosterUrl）。</summary>
    public static byte[]? LoadBytes(int id)
    {
        try
        {
            var path = PathFor(id);
            if (File.Exists(path)) return File.ReadAllBytes(path);
        }
        catch (Exception ex) { Log.Error(ex, "PosterCache 读盘失败(已忽略)"); }
        return null;
    }

    /// <summary>从磁盘缓存构造 ImageSource；不存在或损坏返回 null。</summary>
    public static ImageSource? LoadImageSource(int id)
    {
        try
        {
            var bytes = LoadBytes(id);
            if (bytes == null) return null;
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = new MemoryStream(bytes);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch (Exception ex) { Log.Error(ex, "PosterCache 构造图像失败(已忽略)"); }
        return null;
    }

    /// <summary>
    /// 启动期后台调用：把 DB 中已有 PosterData 但磁盘缺失的海报写盘（只读 DB + 写文件，不删 DB）。
    /// 让历史数据也享受缓存，异常整体忽略。
    /// </summary>
    public static void MigrateFromDb()
    {
        try
        {
            using var ctx = DbHelper.CreateContext();
            // 先投影 Id + PosterData 并物化到内存，再用 Length 过滤（byte[].Length 无法翻译成 SQL）
            var posters = ctx.Movies
                .Where(m => m.PosterData != null)
                .Select(m => new { m.Id, m.PosterData })
                .AsEnumerable()
                .Where(m => m.PosterData!.Length > 0);

            foreach (var p in posters)
            {
                if (p.PosterData != null && !Exists(p.Id))
                    Save(p.Id, p.PosterData);
            }
        }
        catch (Exception ex) { Log.Error(ex, "PosterCache 迁移失败(已忽略)"); }
    }

    // ═══════════════ 异步缩略图解码（流畅度优化 A2）══════════════
    // 按 Movie.Id 缓存“固定尺寸缩略图”，解码在后台线程进行，绝不阻塞 UI 线程。
    // 列表/卡片/海报墙调用 GetThumbnailAsync 后回填 Image.Source，首屏与翻页不再卡顿。

    /// <summary>Id → (尺寸宽,高, 缩略图)。同一电影同尺寸只解码一次。</summary>
    private static readonly ConcurrentDictionary<int, (int w, int h, ImageSource img)> _thumbCache = new();
    private const int MaxThumbCache = 500;

    /// <summary>
    /// 异步获取电影海报的缩略图（固定解码尺寸，后台线程）。磁盘缓存优先；缺失则按 PosterData 后台解码。
    /// 返回 null 表示无海报/失败（调用方应保留占位）。
    /// </summary>
    public static async Task<ImageSource?> GetThumbnailAsync(int movieId, byte[]? posterData, int width, int height)
    {
        if (movieId <= 0) return null;
        // 1) 内存缩略图缓存命中
        if (_thumbCache.TryGetValue(movieId, out var hit) && hit.w == width && hit.h == height)
            return hit.img;

        // 2) 磁盘缓存优先（按 Id，文件名不含尺寸，固定尺寸解码）
        if (Exists(movieId))
        {
            var cached = await Task.Run(() =>
            {
                try
                {
                    var bytes = LoadBytes(movieId);
                    if (bytes == null) return (ImageSource?)null;
                    return Decode(bytes, width, height);
                }
                catch { return (ImageSource?)null; }
            });
            if (cached != null) { CacheThumb(movieId, width, height, cached); return cached; }
        }

        // 3) PosterData 后台解码
        if (posterData != null && posterData.Length > 0)
        {
            var decoded = await Task.Run(() =>
            {
                try { return Decode(posterData, width, height); }
                catch { return (ImageSource?)null; }
            });
            if (decoded != null) { CacheThumb(movieId, width, height, decoded); return decoded; }
        }

        return null;
    }

    private static ImageSource? Decode(byte[] data, int width, int height)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        if (width > 0) bmp.DecodePixelWidth = width;
        else if (height > 0) bmp.DecodePixelHeight = height;
        bmp.StreamSource = new MemoryStream(data);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static void CacheThumb(int id, int w, int h, ImageSource img)
    {
        if (_thumbCache.Count >= MaxThumbCache)
        {
            foreach (var k in _thumbCache.Keys) { if (_thumbCache.TryRemove(k, out _)) break; }
        }
        _thumbCache[id] = (w, h, img);
    }
}
