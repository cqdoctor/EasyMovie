using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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
/// byte[] (PosterData) → BitmapImage，null → DependencyProperty.UnsetValue
/// </summary>
public class PosterImageConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is byte[] data && data.Length > 0)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new MemoryStream(data);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch { }
        }
        return DependencyProperty.UnsetValue;
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
            catch { }
        }
        // null 或空字符串时返回靛蓝色
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#5C6BC0"));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
