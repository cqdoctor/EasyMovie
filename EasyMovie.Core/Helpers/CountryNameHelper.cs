using System;
using System.Collections.Generic;
using System.Linq;

namespace EasyMovie.Core.Helpers;

/// <summary>
/// 国别字符串的拆分与「首个有效国别」提取。
///
/// 背景：豆瓣 rexxar 详情把 countries 数组用 ", " 拼接（如「冰岛, 德国」），
/// 而历史数据与网页解析用 " / "（如「中国大陆 / 中国香港」）。分类分配原先只按
/// '/'、'·' 拆分，导致逗号拼接的合拍片被整体建成一个分类。
///
/// 拆分集合以真实数据出现的形式为准：英文逗号、中文逗号、顿号、斜杠、间隔点、
/// 竖线、中英文分号。「中国香港」「中国大陆」「中国台湾」「中国澳门」内部不含
/// 分隔符，因此天然整体保留；合拍片按既有语义取第一个合法国别。
/// </summary>
public static class CountryNameHelper
{
    private static readonly string[] Separators = { ",", "，", "、", "/", "·", "|", "；", ";" };

    /// <summary>按全部分隔符拆分国别，去空、去首尾空白。空输入返回空序列。</summary>
    public static IEnumerable<string> Split(string? country)
    {
        if (string.IsNullOrWhiteSpace(country)) return Enumerable.Empty<string>();
        return country
            .Split(Separators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s));
    }

    /// <summary>合拍片取第一个合法国别；全为噪声时返回 null。</summary>
    public static string? FirstValid(string? country)
    {
        foreach (var name in Split(country))
            if (CategoryNameValidator.IsValidCategoryName(name)) return name;
        return null;
    }

    /// <summary>
    /// ISO-3166-1 alpha-2 → 中文国别（与豆瓣口径一致的常用产影国，40+ 项）。
    /// 只收录「豆瓣/本项目既有中文名」的写法，避免同一国家出现两个分类名。
    /// </summary>
    private static readonly Dictionary<string, string> IsoToChinese = new(StringComparer.OrdinalIgnoreCase)
    {
        ["US"] = "美国", ["CN"] = "中国大陆", ["HK"] = "中国香港", ["TW"] = "中国台湾", ["MO"] = "中国澳门",
        ["JP"] = "日本", ["KR"] = "韩国", ["KP"] = "朝鲜", ["IN"] = "印度", ["TH"] = "泰国",
        ["GB"] = "英国", ["FR"] = "法国", ["DE"] = "德国", ["IT"] = "意大利", ["ES"] = "西班牙",
        ["RU"] = "俄罗斯", ["CA"] = "加拿大", ["AU"] = "澳大利亚", ["NZ"] = "新西兰", ["BR"] = "巴西",
        ["AR"] = "阿根廷", ["MX"] = "墨西哥", ["CL"] = "智利", ["CO"] = "哥伦比亚", ["PE"] = "秘鲁",
        ["SE"] = "瑞典", ["NO"] = "挪威", ["DK"] = "丹麦", ["FI"] = "芬兰", ["IS"] = "冰岛",
        ["NL"] = "荷兰", ["BE"] = "比利时", ["CH"] = "瑞士", ["AT"] = "奥地利", ["IE"] = "爱尔兰",
        ["PT"] = "葡萄牙", ["GR"] = "希腊", ["PL"] = "波兰", ["CZ"] = "捷克", ["HU"] = "匈牙利",
        ["RO"] = "罗马尼亚", ["BG"] = "保加利亚", ["TR"] = "土耳其", ["IL"] = "以色列", ["IR"] = "伊朗",
        ["SA"] = "沙特阿拉伯", ["AE"] = "阿联酋", ["EG"] = "埃及", ["ZA"] = "南非", ["NG"] = "尼日利亚",
        ["SG"] = "新加坡", ["MY"] = "马来西亚", ["ID"] = "印度尼西亚", ["PH"] = "菲律宾", ["VN"] = "越南",
        ["UA"] = "乌克兰", ["RS"] = "塞尔维亚", ["HR"] = "克罗地亚", ["CU"] = "古巴"
    };

    /// <summary>ISO 国别码转中文；未收录返回空串（调用方再回退 production_countries 的英文名）。</summary>
    public static string FromIso(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "";
        return IsoToChinese.TryGetValue(code.Trim(), out var name) ? name : "";
    }
}
