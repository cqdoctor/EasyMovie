using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using EasyMovie.Core.Helpers;

namespace EasyMovie.Tools.ImportExport
{
    /// <summary>
    /// 从文件名提取电影标题与年份。集中处理发布标签（流媒体 / 编码 / 音轨 / 质量 / 发布组），
    /// 允许标签内部出现空格或点（如 "H 264"、"DDP 5 1"），避免污染标题导致在线匹配错误。
    /// </summary>
    public static class FileNameParser
    {
        // 发布标签正则已下沉到 EasyMovie.Core.Helpers.MovieFieldSanitizer（那里同时供写入护栏使用），
        // 此处不再保留副本，避免两处规则漂移。

        private static readonly Regex YearRegex = new Regex(@"\b(18[8-9]\d|19\d{2}|20[0-2]\d|2030)\b");
        private static readonly Regex Brackets = new Regex(@"\[.*?\]|\(.*?\)");
        private static readonly Regex Separators = new Regex(@"[.\-_]+");
        private static readonly Regex Spaces = new Regex(@"\s+");

        // 仅当末尾是已知视频扩展名时才剥离，避免 Path.GetFileNameWithoutExtension
        // 把“最后一个点之后”的全部内容（含发布标签）误当成扩展名而截断标题。
        private static readonly HashSet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mkv", ".mp4", ".avi", ".m2ts", ".mov", ".wmv", ".flv", ".webm",
            ".mpg", ".mpeg", ".m4v", ".rmvb", ".vob", ".3gp", ".ts"
        };

        /// <summary>从文件名提取（标题, 年份）。年份不存在时返回 null。</summary>
        public static (string title, int? year) Parse(string fileName)
        {
            var name = StripKnownExtension(Path.GetFileName(fileName ?? string.Empty));
            name = Brackets.Replace(name, " ");
            name = Separators.Replace(name, " ");   // 先统一分隔符为空格，避免 ".AMZN." 前导点残留
            name = MovieFieldSanitizer.StripReleaseTags(name);
            name = Spaces.Replace(name, " ").Trim();

            int? year = null;
            var m = YearRegex.Match(name);
            if (m.Success)
            {
                year = int.Parse(m.Value, CultureInfo.InvariantCulture);
                name = Spaces.Replace(name.Replace(m.Value, ""), " ").Trim();
            }

            if (string.IsNullOrEmpty(name))
                name = StripKnownExtension(Path.GetFileName(fileName ?? string.Empty));

            return (name, year);
        }

        private static string StripKnownExtension(string fileName)
        {
            var ext = Path.GetExtension(fileName);
            if (ext.Length > 0 && VideoExtensions.Contains(ext))
                return fileName.Substring(0, fileName.Length - ext.Length);
            return fileName;
        }

        /// <summary>仅剥离发布标签（用于英文搜索提示词清洗等二级场景）。</summary>
        public static string StripTags(string text) => MovieFieldSanitizer.StripReleaseTags(text);
    }
}
