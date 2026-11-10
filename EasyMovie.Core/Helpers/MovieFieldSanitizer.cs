using System;
using System.Text.RegularExpressions;

namespace EasyMovie.Core.Helpers;

/// <summary>
/// 影片字段写入前的护栏（C 第一期：写入侧，不处理存量数据）。
///
/// 背景：<c>MovieService.AddAsync/UpdateAsync</c> 会对年份与空标题做校验，但 CSV 导入、
/// JSON 导入/还原、元数据回写等路径直接写 <c>DbContext</c>，绕过了这层校验，
/// 导致 <c>Year=1000</c>、<c>Title</c> 带 <c>EAC3/Atmos</c> 等发布标签的脏值入库。
/// 这里把「年份是否合理」与「标题清洗」收敛成两个纯函数，供各写入路径共用。
/// </summary>
public static class MovieFieldSanitizer
{
    /// <summary>电影诞生前的年份下限（1888 年《朗德海花园场景》被视为最早影片）。</summary>
    private const int MinYear = 1888;

    /// <summary>
    /// 发布标签正则：与 <c>FileNameParser</c> 原先的私有实现完全一致（本次连同清洗逻辑一并下沉到 Core，
    /// Tools 侧改为调用此处，避免两处漂移）。允许标签内部出现空格或点（如 "H 264"、"DDP 5 1"）。
    /// </summary>
    private static readonly Regex ReleaseTags = new Regex(
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
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// 年份是否可信。合法区间 <c>1888 .. 当前年+1</c>；<c>0</c> 表示「未知」，同样视为合法
    /// （项目内既有约定：<c>Year == 0</c> 即未知，多处逻辑按此分支）。
    /// </summary>
    public static bool IsPlausibleYear(int year)
    {
        if (year == 0) return true;
        return year >= MinYear && year <= DateTime.Now.Year + 1;
    }

    /// <summary>
    /// 剥除发布标签并压缩空白，返回清洗结果（**可能为空**）。
    /// 供 <c>FileNameParser.StripTags</c> 等需要「拿到纯清洗结果」的调用方复用。
    /// </summary>
    public static string StripReleaseTags(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text ?? string.Empty;
        var cleaned = ReleaseTags.Replace(text, " ");
        return Spaces.Replace(cleaned, " ").Trim();
    }

    /// <summary>
    /// 标题清洗：剥发布标签（EAC3 / Atmos / TrueHD / AC3 / DTS / DTSHD 等）与多余空白。
    /// **清洗结果为空时回退原值**，绝不写入空标题（宁可保留含标签的原名，也不能把片名洗没）。
    /// </summary>
    public static string SanitizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return title ?? string.Empty;
        var cleaned = StripReleaseTags(title);
        return cleaned.Length == 0 ? title! : cleaned;
    }
}
