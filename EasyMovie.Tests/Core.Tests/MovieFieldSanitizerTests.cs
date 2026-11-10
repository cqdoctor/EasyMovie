using System;
using EasyMovie.Core.Helpers;
using FluentAssertions;
using Xunit;

namespace EasyMovie.Tests.Core.Tests;

/// <summary>
/// 写入侧护栏（C 第一期）的纯函数契约：
///   IsPlausibleYear —— 0 视为未知（合法）；合法区间 1888..当前年+1；其余判脏。
///   SanitizeTitle   —— 剥发布标签；清洗为空时回退原值（绝不写空标题）。
/// </summary>
public class MovieFieldSanitizerTests
{
    [Theory]
    [InlineData(0, true)]      // 未知，合法
    [InlineData(1887, false)]  // 早于电影诞生
    [InlineData(1888, true)]   // 下边界
    [InlineData(1000, false)]  // 占位值
    [InlineData(1951, true)]   // 在合法区间内（历史片）
    public void IsPlausibleYear_Boundaries(int year, bool expected)
    {
        MovieFieldSanitizer.IsPlausibleYear(year).Should().Be(expected);
    }

    [Fact]
    public void IsPlausibleYear_CurrentYearPlusOne_IsValid_PlusTwoIsNot()
    {
        var current = DateTime.Now.Year;
        MovieFieldSanitizer.IsPlausibleYear(current).Should().BeTrue();
        MovieFieldSanitizer.IsPlausibleYear(current + 1).Should().BeTrue();   // 待映影片
        MovieFieldSanitizer.IsPlausibleYear(current + 2).Should().BeFalse();
    }

    [Theory]
    [InlineData("三大队 Endless Journey EAC3", "三大队 Endless Journey")]
    [InlineData("地师传人Tomb Making Notes EAC3", "地师传人Tomb Making Notes")]
    [InlineData("奥本海默 TrueHD", "奥本海默")]
    [InlineData("沙丘2 Dune: Part Two Atmos", "沙丘2 Dune: Part Two")]
    [InlineData("泰坦尼克号 DTS-HD", "泰坦尼克号")]
    [InlineData("黑客帝国 AC3", "黑客帝国")]
    // 多译名/中英拼接：只剥标签，不裁剪片名本身
    [InlineData("一狱Ⅱ劫数难逃 Imprisoned Ⅱ EAC3", "一狱Ⅱ劫数难逃 Imprisoned Ⅱ")]
    [InlineData("沙丘2 Dune: Part Two", "沙丘2 Dune: Part Two")]
    // 正常标题不变
    [InlineData("星际穿越", "星际穿越")]
    [InlineData("肖申克的救赎 The Shawshank Redemption", "肖申克的救赎 The Shawshank Redemption")]
    public void SanitizeTitle_StripsReleaseTags(string input, string expected)
    {
        MovieFieldSanitizer.SanitizeTitle(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("EAC3")]
    [InlineData("Atmos TrueHD")]
    [InlineData("DTS-HD AC3")]
    public void SanitizeTitle_FallsBackToOriginal_WhenEverythingLooksLikeATag(string input)
    {
        // 清洗结果为空时回退原值：宁可保留带标签的片名，也不能写入空标题
        MovieFieldSanitizer.SanitizeTitle(input).Should().Be(input);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SanitizeTitle_EmptyInput_ReturnsInputWithoutThrowing(string input)
    {
        MovieFieldSanitizer.SanitizeTitle(input).Should().Be(input);
    }

    [Fact]
    public void SanitizeTitle_ShouldCollapseWhitespace()
    {
        MovieFieldSanitizer.SanitizeTitle("  三大队    Endless   Journey  ").Should().Be("三大队 Endless Journey");
    }
}
