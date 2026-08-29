using EasyMovie.Core.Helpers;
using Xunit;

namespace EasyMovie.Tests.Core.Tests;

/// <summary>
/// CategoryNameValidator 行为锁定测试。
///
/// 目的：把 MovieListView.xaml.cs 的分类名校验逻辑抽出时，用测试钉住现有行为。
/// 这些断言描述的是**抽取前的真实行为**，不是理想行为——若将来要改变语义，
/// 应显式修改本测试并说明理由。
/// </summary>
public class CategoryNameValidatorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void IsValidCategoryName_EmptyOrWhitespace_ReturnsFalse(string? name)
    {
        Assert.False(CategoryNameValidator.IsValidCategoryName(name));
    }

    /// <summary>纯整数串是抓取页的计数值，不是分类名。</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("2020")]
    [InlineData("-5")]
    [InlineData("12345")]
    public void IsValidCategoryName_PureInteger_ReturnsFalse(string name)
    {
        Assert.False(CategoryNameValidator.IsValidCategoryName(name));
    }

    [Theory]
    [InlineData("科幻")]
    [InlineData("剧情")]
    [InlineData("美国")]
    [InlineData("中国大陆")]
    [InlineData("动作片")]
    public void IsValidCategoryName_NormalName_ReturnsTrue(string name)
    {
        Assert.True(CategoryNameValidator.IsValidCategoryName(name));
    }

    /// <summary>抓取页的计数噪声，如「12345人收藏」。</summary>
    [Theory]
    [InlineData("12345人收藏")]
    [InlineData("人评论")]
    [InlineData("人看过")]
    [InlineData("人想看")]
    [InlineData("人评价")]
    [InlineData("人关注")]
    [InlineData("人推荐")]
    [InlineData("人看")]
    public void IsValidCategoryName_ContainsJunkCounter_ReturnsFalse(string name)
    {
        Assert.False(CategoryNameValidator.IsValidCategoryName(name));
    }

    /// <summary>
    /// 已知行为（不理想，但此处锁定现状以免抽取过程改坏）：
    /// 小数串「12.5」会被判为合法，因为 int.TryParse 无法解析小数。
    /// 原实现只拦截整数，不拦截小数。
    /// </summary>
    [Fact]
    public void IsValidCategoryName_DecimalString_IsAccepted_KnownBehavior()
    {
        Assert.True(CategoryNameValidator.IsValidCategoryName("12.5"));
    }

    /// <summary>
    /// 已知行为：本方法自身不做 Trim，首尾空白不影响判定。
    /// 调用方（MovieListView 的两处）均已在传入前 Trim，故此行为无实际影响，
    /// 但搬移时不应擅自加入 Trim，否则会改变独立调用时的语义。
    /// </summary>
    [Fact]
    public void IsValidCategoryName_DoesNotTrim_KnownBehavior()
    {
        Assert.True(CategoryNameValidator.IsValidCategoryName("  科幻  "));
        Assert.False(CategoryNameValidator.IsValidCategoryName("  人看过  "));
    }
}
