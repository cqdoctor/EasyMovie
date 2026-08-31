using System.Text.RegularExpressions;

namespace EasyMovie.Core.Helpers;

/// <summary>
/// 从 HTML 碎片中清洗出纯文本。
///
/// 背景：早期从网页抓取人名/类型等字段时，会把 HTML 属性残留一起写进数据库，
/// 例如 "1338249-gary-dauberman'&gt;加里·道伯曼&lt;"。本类负责还原成 "加里·道伯曼"。
///
/// 本实现于 2026-08-29 从 MovieListView.xaml.cs 的私有方法 CleanHtmlFragment
/// 原样抽取（行为逐字节一致），以便纳入单元测试保护——该文件是 2500+ 行的
/// code-behind，此前完全没有自动化测试覆盖。
/// 抽取时未改动任何逻辑；行为由 Tests/Core.Tests/TextCleanerTests.cs 锁定。
/// </summary>
public static class TextCleaner
{
    /// <summary>任意 HTML 标签（含 <c>&lt;br/&gt;</c> 这类自闭合标签）。</summary>
    private static readonly Regex AnyHtmlTag = new("<[^>]+>", RegexOptions.Compiled);

    /// <summary>HTML 标签（含不完整的 &lt;a&gt;、&lt;/a&gt;）。</summary>
    private static readonly Regex HtmlTag = new("</?[a-zA-Z][^>]*>", RegexOptions.Compiled);

    /// <summary>HTML 属性残留，如 "123-name'&gt;张三" 或 "123-name\"&gt;张三"。</summary>
    private static readonly Regex AttributeResidue = new(@"[\d\-a-zA-Z_/]+['" + "\"" + @">]+", RegexOptions.Compiled);

    /// <summary>
    /// 去掉所有 HTML 标签并 Trim（不解码实体、不处理属性残留）。
    ///
    /// 与 <see cref="CleanHtmlFragment"/> 的区别：本方法只做"剥标签"这一件事，
    /// 适用于简介 / 演员 / 国家这类本来就是正文的字段；
    /// <see cref="CleanHtmlFragment"/> 还会清理属性残留并剔除疑似属性的值，用于人名等短字段。
    ///
    /// 于 2026-08-31 合并自 4 处逐字节相同的私有实现（MovieListView.StripHtmlTags 与
    /// TmdbApiClient / OmdbApiClient / BaiduBaikeApiClient 的 StripHtml），行为未变。
    /// </summary>
    public static string StripHtml(string? value)
        => string.IsNullOrEmpty(value) ? value! : AnyHtmlTag.Replace(value, "").Trim();

    /// <summary>残留的引号、尖括号。</summary>
    private static readonly Regex QuoteAndBracket = new("[<>\"']", RegexOptions.Compiled);

    /// <summary>看起来像 HTML 属性/URL 的值：纯英文数字与符号串，且不含中文。</summary>
    private static readonly Regex LooksLikeAttribute = new(@"^[\d\-a-zA-Z_=./&?]+$", RegexOptions.Compiled);

    /// <summary>
    /// 清洗 HTML 标签碎片。
    /// </summary>
    /// <example>
    /// CleanHtmlFragment("1338249-gary-dauberman'&gt;加里·道伯曼&lt;") → "加里·道伯曼"
    /// </example>
    /// <returns>清洗后的纯文本；若输入为空或清洗后无有效内容则返回空串。</returns>
    public static string CleanHtmlFragment(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input ?? string.Empty;

        // 移除所有 HTML 标签 <...>
        var result = HtmlTag.Replace(input, "");
        // 移除 HTML 属性残留
        result = AttributeResidue.Replace(result, "");
        // 移除残留的引号、尖括号
        result = QuoteAndBracket.Replace(result, "");
        result = result.Trim(' ', ',', '/', '-', '=');

        if (string.IsNullOrWhiteSpace(result)) return "";
        // 过滤掉看起来像 HTML 属性/URL 的值
        if (LooksLikeAttribute.IsMatch(result)) return "";

        return result.Trim();
    }
}
