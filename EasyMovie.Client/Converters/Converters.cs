using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Collections.Concurrent;
using System.Security.Cryptography;

using Serilog;

namespace EasyMovie.Client.Converters;

/// <summary>
/// Null → Visible，非 Null → Collapsed
/// </summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value == null ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class BoolToStarColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00)) : new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class PlayButtonToolTipConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true
            ? EasyMovie.Client.LanguageManager.GetString("Tip_FileMissing")
            : EasyMovie.Client.LanguageManager.GetString("Tip_Play");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 非 Null → Visible，Null → Collapsed（反向）
/// </summary>
public class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value != null ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// WatchStatus 枚举 → 中文文本
/// </summary>
public class WatchStatusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Core.Enums.WatchStatus status)
        {
            return status switch
            {
                Core.Enums.WatchStatus.NotWatched => "未看",
                Core.Enums.WatchStatus.WantToWatch => "🕐 想看",
                Core.Enums.WatchStatus.Watched => "✅ 已看",
                _ => ""
            };
        }
        return "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class WatchStatusColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Core.Enums.WatchStatus status)
        {
            return status switch
            {
                Core.Enums.WatchStatus.NotWatched => new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                Core.Enums.WatchStatus.WantToWatch => new SolidColorBrush(Color.FromRgb(0x26, 0xA6, 0x9A)),
                Core.Enums.WatchStatus.Watched => new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A)),
                _ => new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA))
            };
        }
        return new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// WatchStatus → Visibility：仅“想看/已看”显示状态徽标，“未看”折叠。
/// </summary>
public class WatchStatusBadgeVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Core.Enums.WatchStatus status)
            return status == Core.Enums.WatchStatus.NotWatched ? Visibility.Collapsed : Visibility.Visible;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// bool → ⭐ / ☆
/// </summary>
public class BoolToStarConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? "★" : "☆";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// FilePath → 🎬 / -
/// </summary>
public class FilePathIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string path && !string.IsNullOrEmpty(path))
            return System.IO.File.Exists(path) ? "🎬" : "⚠️";
        return "-";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// byte[] (PosterData) → 固定尺寸缩略图 BitmapImage，null → DependencyProperty.UnsetValue。
/// 性能改进（针对“列表卡顿”）：
/// 1) 缓存 key 由“byte[] 引用相等”改为“内容 SHA256 哈希 + 目标尺寸”，解决 EF Core 每次查询都
///    new 出新 byte[] 实例导致引用缓存永不命中、每页都重复同步解码原图的问题；
/// 2) 支持 ConverterParameter="宽,高" 在解码阶段即缩到显示尺寸（DecodePixelWidth/Height），
///    避免在 UI 线程解码整张原图（列表密集处收益最大）；
/// 3) 线程安全缓存（ConcurrentDictionary）+ 容量上限。
/// </summary>
public class PosterImageConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, BitmapImage> _cache = new();
    private const int MaxCacheSize = 400;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is byte[] data && data.Length > 0)
        {
            ParseSize(parameter, out var w, out var h);
            var key = CacheKey(data, w, h);
            if (_cache.TryGetValue(key, out var cached))
                return cached;

            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                if (w > 0) image.DecodePixelWidth = w;
                else if (h > 0) image.DecodePixelHeight = h;
                image.StreamSource = new MemoryStream(data);
                image.EndInit();
                image.Freeze();

                if (_cache.Count >= MaxCacheSize)
                {
                    foreach (var k in _cache.Keys) { if (_cache.TryRemove(k, out _)) break; }
                }
                _cache[key] = image;
                return image;
            }
            catch (Exception ex) { Log.Error(ex, "Converters 海报转换异常"); }
        }
        return DependencyProperty.UnsetValue;
    }

    private static void ParseSize(object? parameter, out int w, out int h)
    {
        w = 0; h = 0;
        if (parameter is string s && s.Contains(','))
        {
            var parts = s.Split(',');
            int.TryParse(parts[0], out w);
            if (parts.Length > 1) int.TryParse(parts[1], out h);
        }
    }

    private static string CacheKey(byte[] data, int w, int h)
    {
        // 尺寸影响解码结果，必须纳入 key；内容哈希保证不同海报不串图
        byte[] hash;
        using (var sha = SHA256.Create())
            hash = sha.ComputeHash(data);
        return $"{w}x{h}:{System.Convert.ToHexString(hash)}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 颜色字符串 (如 "#F44336") → SolidColorBrush，null 时根据名称哈希分配颜色
/// </summary>
public class StringToBrushConverter : IValueConverter
{
    private static readonly string[] Palette = {
        "#F44336","#E91E63","#9C27B0","#673AB7","#3F51B5",
        "#2196F3","#03A9F4","#00BCD4","#009688","#4CAF50",
        "#8BC34A","#CDDC39","#FFEB3B","#FFC107","#FF9800",
        "#FF5722","#795548","#607D8B"
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string colorStr && !string.IsNullOrEmpty(colorStr))
        {
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorStr)); }
            catch (Exception ex) { Log.Error(ex, "Converters 转换异常"); }
        }
        // null 或空字符串时返回靛蓝色
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#5C6BC0"));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 续播进度 → GridLength（已播放比例，Star 单位）。
/// values[0] = PlaybackPosition(毫秒, long)，values[1] = Runtime(分钟, int?)。
/// 用于“继续观看”卡片底部进度条：第一列宽度 = 已播放比例，第二列 = 剩余。
/// </summary>
public class PlaybackProgressConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values?.Length == 2 && values[0] is long pos)
        {
            int? runtime = values[1] as int?;
            if (runtime == null || runtime <= 0)
                return new GridLength(0, GridUnitType.Star);

            double playedSeconds = pos / 1000.0;
            double totalSeconds = runtime.Value * 60.0;
            double pct = totalSeconds > 0 ? playedSeconds / totalSeconds : 0;
            pct = Math.Max(0, Math.Min(1, pct));
            return new GridLength(pct, GridUnitType.Star);
        }
        return new GridLength(0, GridUnitType.Star);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
