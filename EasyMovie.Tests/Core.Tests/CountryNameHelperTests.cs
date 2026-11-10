using System.Linq;
using EasyMovie.Core.Helpers;
using Xunit;

namespace EasyMovie.Tests.Core.Tests;

/// <summary>
/// CountryNameHelper 契约测试。
///
/// 背景：豆瓣 rexxar 详情把 countries 数组用 ", " 拼接，历史上分类分配只按 '/'、'·'
/// 拆分，导致「冰岛, 德国」「越南, 法国, 新加坡, 西班牙」被整体建成一个分类。
/// 锁定：所有真实出现过的分隔符都要拆开；「中国香港」等复合地名必须整体保留；
/// 合拍片按既有语义取第一个合法国别。
/// </summary>
public class CountryNameHelperTests
{
    [Theory]
    [InlineData("冰岛, 德国", 2)]
    [InlineData("越南, 法国, 新加坡, 西班牙", 4)]
    [InlineData("中国大陆 / 中国香港", 2)]
    [InlineData("日本，韩国、泰国；新加坡|马来西亚", 5)]
    [InlineData("美国·英国", 2)]
    [InlineData(" 冰岛 , 德国 ", 2)]
    public void Split_按真实分隔符拆分(string input, int expected)
    {
        Assert.Equal(expected, CountryNameHelper.Split(input).Count());
    }

    [Theory]
    [InlineData("中国香港")]
    [InlineData("中国大陆")]
    [InlineData("中国台湾")]
    [InlineData("中国澳门")]
    public void Split_复合地名整体保留(string input)
    {
        var parts = CountryNameHelper.Split(input).ToList();
        Assert.Single(parts);
        Assert.Equal(input, parts[0]);
    }

    [Theory]
    [InlineData("冰岛, 德国", "冰岛")]
    [InlineData("越南, 法国, 新加坡, 西班牙", "越南")]
    [InlineData("美国/英国", "美国")]
    [InlineData("中国大陆 / 中国香港", "中国大陆")]
    [InlineData("中国香港", "中国香港")]
    public void FirstValid_合拍片取第一个合法国别(string input, string expected)
    {
        Assert.Equal(expected, CountryNameHelper.FirstValid(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FirstValid_空输入返回null(string? input)
    {
        Assert.Null(CountryNameHelper.FirstValid(input));
        Assert.Empty(CountryNameHelper.Split(input));
    }

    [Fact]
    public void FirstValid_纯数字段被剔除()
    {
        Assert.Null(CountryNameHelper.FirstValid("1234"));
    }

    [Theory]
    [InlineData("US", "美国")]
    [InlineData("CN", "中国大陆")]
    [InlineData("HK", "中国香港")]
    [InlineData("TW", "中国台湾")]
    [InlineData("JP", "日本")]
    [InlineData("KR", "韩国")]
    [InlineData("GB", "英国")]
    [InlineData("FR", "法国")]
    [InlineData("IN", "印度")]
    [InlineData("RU", "俄罗斯")]
    [InlineData("us", "美国")]
    [InlineData(" us ", "美国")]
    public void FromIso_常用产影国映射为中文(string code, string expected)
    {
        Assert.Equal(expected, CountryNameHelper.FromIso(code));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ZZ")]
    public void FromIso_缺失或未收录返回空串(string? code)
    {
        Assert.Equal("", CountryNameHelper.FromIso(code));
    }
}
