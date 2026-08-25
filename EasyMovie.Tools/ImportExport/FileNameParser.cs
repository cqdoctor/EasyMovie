using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace EasyMovie.Tools.ImportExport
{
    /// <summary>
    /// 从文件名提取电影标题与年份。集中处理发布标签（流媒体 / 编码 / 音轨 / 质量 / 发布组），
    /// 允许标签内部出现空格或点（如 "H 264"、"DDP 5 1"），避免污染标题导致在线匹配错误。
    /// </summary>
    public static class FileNameParser
    {
        // 综合发布标签，允许标签内部出现空格/点。覆盖常见流媒体、音轨、编码、质量与发布组。
        private static readonly Regex TagRegex = new Regex(
            @"(?i)\b(" +
            // 分辨率 / 质量
            "4K|8K|1080[pi]|720p|2160p|480p|" +
            // 片源 / 封装
            "BluRay|Blu-ray|BRRip|BDRip|WEB-?DL|WEBRip|WEB|DL|HDRip|HDTV|HDTVRip|DVDRip|DVD|Cam|HDCAM|TS|TC|SCR|R5|PPV|" +
            // 剪辑 / 版本
            "REMUX|Remux|PROPER|REPACK|EXTENDED|UNCUT|Director'?s\\.?\\s*Cut|Theatrical\\.?\\s*Cut|IMAX|OpenMatte|3D|" +
            // 编码 / 视频
            "x26[45]|H\\s*?\\.?\\s*26[45]|HEVC|AVC|XviD|DivX|" +
            // 音轨 / 音频
            "AAC|DTS(?:[\\s.-]*(?:HD|MA|ES|X))*|TrueHD|Atmos|Dolby(?:\\s*Vision)?|E?AC3|DDP\\s?5[\\s.]?1|DDP\\s?2[\\s.]?0|DD\\s?5\\.?1|DD\\s?2\\.?0|DD\\+?\\s?5[\\s.]?1|DD\\+?|FLAC|MP3|Opus|LPCM|5[\\s.]?1|7[\\s.]?1|2[\\s.]?0|" +
            // 色深 / HDR
            "10-?bit|8-?bit|HDR10\\+?|HDR|SDR|HLG|DV|" +
            // 流媒体平台
            "AMZN|NF|DSNP|HMAX|ATVP|Hulu|iTunes|iT|CR|FUNI|CORE|" +
            // 音轨语言 / 字幕
            "MULTi|Multi|Dual|Dual-Audio|Subbed|Dubbed|LiNE|SYNC|" +
            // 常见发布组
            "CH[ND]?|CHDWEB|YIFY|YTS|GGEZ|FGT|ETRG|RARBG|SPARKS|AMIABLE|CMRG|DDR|PSA|TBH|ION10|SMURF|NTb|WiKi|MkvCage|Cathay|LPD|Tigole|QxR|EVO|KOGI|HQC|CM8|OCUK|CRiSC|Silence|ToySenses|BSO|SA89|REAL|VICE|CAS|SKA|BCA|Nogroup|PTer|fov|bandi|viet|HDChina|CHDBits|mHD|ZmHD|CMCT|CtrlHD|NTG|MZABI|GENES|Zeus|HONE|orbitron|fgt|anoxmous|blax|jbd|dn|legion|TAoE|EVOLVE|TSCC|iNTERNAL|KOR|FRA|BHD|HDT|DECIBEL|TAYTO" +
            @")\b",
            RegexOptions.IgnoreCase);

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
            name = TagRegex.Replace(name, " ");      // 再剥离发布标签（允许标签内部空格/点）
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
        public static string StripTags(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text ?? string.Empty;
            var cleaned = TagRegex.Replace(text, " ");
            cleaned = Spaces.Replace(cleaned, " ").Trim();
            return cleaned;
        }
    }
}
