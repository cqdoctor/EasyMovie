using System.Text.RegularExpressions;

namespace EasyMovie.Core.Helpers;

/// <summary>
/// 在线搜索用的关键词提纯（#10 后续瓶颈：不是限流，是搜索词太脏）。
/// </summary>
/// <remarks>
/// 实测背景：主库 2020+ 影片里 <b>188/243（77%）</b> 的片名是「中文译名 + 英文原名」的混合形态，
/// 例如 <c>困兽Death Stranding EAC3</c>、<c>三大队 Endless Journey EAC3</c>。
/// 把这种整串丢给豆瓣，搜索会直接返回 0 条候选（实测 6 部混合片名中 2 部为 0 候选），
/// 后续 PickBestMatch 再怎么保守也无从匹配——这是 2020+ 补全率上不去的第二个根因（第一个是限流，已修）。
///
/// A/B 实测（同一时间窗、真实客户端）：
///   现行混合关键词 → 6 部中 4 部有候选，候选总数 8；
///   提纯后的中文核心 → 6 部中 6 部有候选，候选总数 14，且首条全部为目标影片。
///
/// 剥离策略与 <c>ExtractChineseKeyword</c> 的关键差异：
///   后者会把所有非汉字字符（含「Ⅱ」「之」以外的分隔符、罗马数字）一并丢掉，
///   例如「一狱Ⅱ劫数难逃」被压成「一狱劫数难逃」，与豆瓣真实片名对不上。
///   这里只剥离**拉丁词串**（≥1 个字母的连续字母段），保留汉字与罗马数字等其余字符。
/// </remarks>
public static class SearchKeywordPurifier
{
    /// <summary>连续拉丁词串（含常见的撇号/连字符/点，如 "Don't"、"Spider-Man"）。</summary>
    private static readonly Regex LatinRuns = new(@"[A-Za-z][A-Za-z'’.\-]*", RegexOptions.Compiled);

    /// <summary>提纯后的最短长度：低于此值不采用（单字片名如「爱」「杀」极易误匹配）。</summary>
    private const int MinCoreLength = 2;

    /// <summary>
    /// 尝试从关键词中提取「中文核心」：仅当原词同时含汉字与拉丁字母、且剥离拉丁词串后
    /// 仍留有至少 <see cref="MinCoreLength"/> 个字符时返回 true。
    /// </summary>
    /// <param name="keyword">已清洗过的搜索关键词</param>
    /// <param name="core">提纯结果（仅当返回 true 时有效）</param>
    public static bool TryExtractChineseCore(string? keyword, out string core)
    {
        core = string.Empty;
        if (string.IsNullOrWhiteSpace(keyword)) return false;
        var k = Regex.Replace(keyword.Trim(), @"\s+", " ");
        if (!ContainsHan(k) || !ContainsLatin(k)) return false;

        // 只剥离拉丁词串，然后**只保留含汉字的词元**。
        // 只保留含汉字词元这一步很关键，实测它会同时解决两类残渣：
        //   - 「地师传人Tomb Making Notes EAC3」剥掉 EAC 后残留孤立数字「3」；
        //   - 「一狱Ⅱ劫数难逃 Imprisoned Ⅱ ...」里英文原名的「Ⅱ」会成为独立词元。
        // 而汉字词元内部的罗马数字（「一狱Ⅱ劫数难逃」）会被整体保留，与豆瓣真实片名一致。
        var stripped = LatinRuns.Replace(k, " ");
        var core_ = string.Join(" ", stripped
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(ContainsHan));

        if (core_.Length < MinCoreLength || !ContainsHan(core_)) return false;
        if (string.Equals(core_, k, StringComparison.Ordinal)) return false;   // 没变化就不必重试

        core = core_;
        return true;
    }

    private static bool ContainsHan(string s)
    {
        foreach (var c in s)
            if (c >= 0x4e00 && c <= 0x9fff) return true;
        return false;
    }

    private static bool ContainsLatin(string s)
    {
        foreach (var c in s)
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z') return true;
        return false;
    }
}
