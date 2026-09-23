using System.Globalization;
using System.IO;
using System.Windows;
using EasyMovie.Client.Converters;
using EasyMovie.Core.Enums;
using FluentAssertions;

namespace EasyMovie.Client.Tests;

/// <summary>
/// 值转换器冒烟测试。只覆盖**无 Dispatcher / 无 Application 依赖**的纯逻辑分支
/// （位图解码类转换器需要 WPF 成像栈，故意不在此测）。
/// 这些转换器直接驱动 UI 可见性与配色，回归代价高但此前零覆盖。
/// </summary>
public class ConvertersTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    private static object Conv(System.Windows.Data.IValueConverter c, object? value, object? parameter = null)
        => c.Convert(value!, typeof(object), parameter!, Culture);

    // ── NullToVisibilityConverter：null → Visible（用于"暂无海报"占位） ──

    [Fact]
    public void NullToVisibility_Null_ShouldBeVisible()
        => Conv(new NullToVisibilityConverter(), null).Should().Be(Visibility.Visible);

    [Fact]
    public void NullToVisibility_NonNull_ShouldBeCollapsed()
        => Conv(new NullToVisibilityConverter(), "x").Should().Be(Visibility.Collapsed);

    // ── NullToCollapsedConverter：与上面相反 ──

    [Fact]
    public void NullToCollapsed_Null_ShouldBeCollapsed()
        => Conv(new NullToCollapsedConverter(), null).Should().Be(Visibility.Collapsed);

    [Fact]
    public void NullToCollapsed_NonNull_ShouldBeVisible()
        => Conv(new NullToCollapsedConverter(), 42).Should().Be(Visibility.Visible);

    // ── Bool 系列 ──

    [Theory]
    [InlineData(true, Visibility.Visible)]
    [InlineData(false, Visibility.Collapsed)]
    [InlineData(null, Visibility.Collapsed)]   // 非 bool 一律折叠
    public void BoolToVisibility_ShouldMap(bool? input, Visibility expected)
        => Conv(new BoolToVisibilityConverter(), input).Should().Be(expected);

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void InverseBool_ShouldNegate(bool input, bool expected)
        => Conv(new InverseBoolConverter(), input).Should().Be(expected);

    [Fact]
    public void InverseBool_NonBool_ShouldPassThroughUnchanged()
        => Conv(new InverseBoolConverter(), "abc").Should().Be("abc");

    [Theory]
    [InlineData(true, "★")]
    [InlineData(false, "☆")]
    public void BoolToStar_ShouldMap(bool input, string expected)
        => Conv(new BoolToStarConverter(), input).Should().Be(expected);

    // ── WatchStatus 三兄弟（枚举 → 文案/配色/可见性，逻辑集中且易错） ──

    [Theory]
    [InlineData(WatchStatus.NotWatched, "未看")]
    [InlineData(WatchStatus.WantToWatch, "🕐 想看")]
    [InlineData(WatchStatus.Watched, "✅ 已看")]
    public void WatchStatus_ShouldMapToText(WatchStatus status, string expected)
        => Conv(new WatchStatusConverter(), status).Should().Be(expected);

    [Fact]
    public void WatchStatus_NonEnum_ShouldReturnEmpty()
        => Conv(new WatchStatusConverter(), "whatever").Should().Be("");

    [Fact]
    public void WatchStatusColor_NotWatched_ShouldBeGrey()
    {
        var brush = Conv(new WatchStatusColorConverter(), WatchStatus.NotWatched)
            .Should().BeOfType<System.Windows.Media.SolidColorBrush>().Subject;
        brush.Color.Should().Be(System.Windows.Media.Color.FromRgb(0xAA, 0xAA, 0xAA));
    }

    [Fact]
    public void WatchStatusColor_Watched_ShouldBeGreen()
    {
        var brush = Conv(new WatchStatusColorConverter(), WatchStatus.Watched)
            .Should().BeOfType<System.Windows.Media.SolidColorBrush>().Subject;
        brush.Color.Should().Be(System.Windows.Media.Color.FromRgb(0x66, 0xBB, 0x6A));
    }

    [Theory]
    [InlineData(WatchStatus.NotWatched, Visibility.Collapsed)]  // 未看不显示徽标
    [InlineData(WatchStatus.WantToWatch, Visibility.Visible)]
    [InlineData(WatchStatus.Watched, Visibility.Visible)]
    public void WatchStatusBadgeVisibility_ShouldOnlyHideNotWatched(WatchStatus status, Visibility expected)
        => Conv(new WatchStatusBadgeVisibilityConverter(), status).Should().Be(expected);

    [Fact]
    public void WatchStatusBadgeVisibility_NonEnum_ShouldCollapse()
        => Conv(new WatchStatusBadgeVisibilityConverter(), null).Should().Be(Visibility.Collapsed);

    // ── FilePathIconConverter：三种分支（空路径 / 存在 / 不存在） ──

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FilePathIcon_EmptyPath_ShouldBeDash(string? path)
        => Conv(new FilePathIconConverter(), path).Should().Be("-");

    [Fact]
    public void FilePathIcon_ExistingFile_ShouldBeClapper()
    {
        var tmp = Path.GetTempFileName();
        try { Conv(new FilePathIconConverter(), tmp).Should().Be("🎬"); }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void FilePathIcon_MissingFile_ShouldBeWarning()
        => Conv(new FilePathIconConverter(), @"Z:\definitely\not\here.mkv").Should().Be("⚠️");

    // ── StringToBrushConverter：可解析色值 + 非法色值/空值回退（绝不抛异常） ──

    [Fact]
    public void StringToBrush_ValidHex_ShouldParse()
    {
        var brush = Conv(new StringToBrushConverter(), "#F44336")
            .Should().BeOfType<System.Windows.Media.SolidColorBrush>().Subject;
        brush.Color.Should().Be(System.Windows.Media.Color.FromRgb(0xF4, 0x43, 0x36));
    }

    [Fact]
    public void StringToBrush_Null_ShouldFallBackToIndigo()
    {
        var brush = Conv(new StringToBrushConverter(), null)
            .Should().BeOfType<System.Windows.Media.SolidColorBrush>().Subject;
        brush.Color.Should().Be(System.Windows.Media.Color.FromRgb(0x5C, 0x6B, 0xC0));
    }

    [Fact]
    public void StringToBrush_InvalidHex_ShouldFallBackNotThrow()
    {
        var brush = Conv(new StringToBrushConverter(), "not-a-color")
            .Should().BeOfType<System.Windows.Media.SolidColorBrush>().Subject;
        brush.Color.Should().Be(System.Windows.Media.Color.FromRgb(0x5C, 0x6B, 0xC0));
    }

    // ── PlaybackProgressConverter（IMultiValueConverter）：续播进度换算 + 钳制 ──
    // 这是本组里**纯计算量最大**的一处，边界（runtime 缺失 / 超长 / 负值）最易错。

    private static GridLength Progress(long posMs, int? runtimeMinutes)
        => (GridLength)new PlaybackProgressConverter().Convert(
            new object[] { posMs, runtimeMinutes! }, typeof(GridLength), null!, Culture);

    [Fact]
    public void Progress_HalfPlayed_ShouldBeHalfStar()
    {
        // 30 分钟长的片子看了 15 分钟 → 0.5
        Progress(15 * 60 * 1000, 30).Value.Should().BeApproximately(0.5, 1e-6);
    }

    [Fact]
    public void Progress_NoRuntime_ShouldBeZeroStar()
    {
        Progress(60_000, null).Value.Should().Be(0);
        Progress(60_000, 0).Value.Should().Be(0);      // runtime<=0 同样归零
        Progress(60_000, -5).Value.Should().Be(0);
    }

    [Fact]
    public void Progress_PositionBeyondRuntime_ShouldClampToZeroOrOne()
    {
        Progress(999 * 60 * 1000, 10).Value.Should().Be(1.0);   // 远超总时长 → 钳到 1
        Progress(-5000, 10).Value.Should().Be(0.0);             // 负位置 → 钳到 0
    }

    [Fact]
    public void Progress_MalformedInput_ShouldReturnZeroStar()
    {
        var conv = new PlaybackProgressConverter();
        // 参数个数不对 / 首个参数不是 long，都必须安全降级到 0
        ((GridLength)conv.Convert(new object[] { 1 }, typeof(GridLength), null!, Culture)).Value.Should().Be(0);
        ((GridLength)conv.Convert(new object[] { "x", 30 }, typeof(GridLength), null!, Culture)).Value.Should().Be(0);
    }

    // ── PosterImageConverter：无海报时必须返回 UnsetValue（让 XAML 回退占位），且不做解码 ──

    [Theory]
    [InlineData(null)]
    [InlineData(new byte[0])]
    public void PosterImage_NoData_ShouldReturnUnsetValue(byte[]? data)
        => Conv(new PosterImageConverter(), data).Should().Be(DependencyProperty.UnsetValue);
}
