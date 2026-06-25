using Pinyin4net;

namespace EasyMovie.Core;

/// <summary>
/// 拼音搜索索引辅助类：生成搜索索引（全拼+首字母）
/// </summary>
public static class PinyinIndexHelper
{
    /// <summary>
    /// 为文本生成搜索索引，包含：全拼、首字母
    /// 例如 "老板娘3" → "laobanniang3 lbn3"
    /// </summary>
    public static string BuildSearchIndex(params string?[] fields)
    {
        var parts = new List<string>();
        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field)) continue;
            // 全拼
            var fullPinyin = GetFullPinyin(field);
            if (!string.IsNullOrEmpty(fullPinyin)) parts.Add(fullPinyin);
            // 首字母
            var firstLetters = GetFirstLetters(field);
            if (!string.IsNullOrEmpty(firstLetters)) parts.Add(firstLetters);
        }
        return string.Join(" ", parts.Distinct());
    }

    /// <summary>
    /// 获取全拼（小写，无音调）
    /// </summary>
    public static string GetFullPinyin(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var format = new Pinyin4net.Format.HanyuPinyinOutputFormat();
        format.CaseType = Pinyin4net.Format.HanyuPinyinCaseType.LOWERCASE;
        format.ToneType = Pinyin4net.Format.HanyuPinyinToneType.WITHOUT_TONE;
        format.VCharType = Pinyin4net.Format.HanyuPinyinVCharType.WITH_V;

        var result = new System.Text.StringBuilder();
        foreach (var c in text)
        {
            if (c >= 0x4e00 && c <= 0x9fff)
            {
                var pyArray = PinyinHelper.ToHanyuPinyinStringArray(c, format);
                if (pyArray != null && pyArray.Length > 0) result.Append(pyArray[0]);
            }
            else if (char.IsLetterOrDigit(c))
            {
                result.Append(char.ToLower(c));
            }
            else if (c == ' ' || c == '/' || c == '·' || c == '-' || c == '_')
            {
                result.Append(' ');
            }
        }
        return result.ToString();
    }

    /// <summary>
    /// 获取首字母（小写）
    /// </summary>
    public static string GetFirstLetters(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var format = new Pinyin4net.Format.HanyuPinyinOutputFormat();
        format.CaseType = Pinyin4net.Format.HanyuPinyinCaseType.LOWERCASE;
        format.ToneType = Pinyin4net.Format.HanyuPinyinToneType.WITHOUT_TONE;

        var result = new System.Text.StringBuilder();
        foreach (var c in text)
        {
            if (c >= 0x4e00 && c <= 0x9fff)
            {
                var pyArray = PinyinHelper.ToHanyuPinyinStringArray(c, format);
                if (pyArray != null && pyArray.Length > 0 && pyArray[0].Length > 0)
                    result.Append(pyArray[0][0]);
            }
            else if (char.IsLetter(c))
            {
                result.Append(char.ToLower(c));
            }
            else if (char.IsDigit(c))
            {
                result.Append(c);
            }
        }
        return result.ToString();
    }
}
