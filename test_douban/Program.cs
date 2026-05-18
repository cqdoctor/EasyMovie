using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json;

var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, UseCookies = false });
http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 Chrome/131.0.0.0");
http.DefaultRequestHeaders.Add("Cookie", "bid=6_QIGVTJLa4; ck=qcMB; dbcl2=191467457:1szF0XY3yko; push_doumail_num=0; push_noty_num=0");
http.DefaultRequestHeaders.Referrer = new Uri("https://movie.douban.com/");
var h = await http.GetStringAsync("https://movie.douban.com/subject_search?search_text=%E4%BF%9D%E6%8A%A4%E8%80%85");
var idx = h.IndexOf("window.__DATA__ = {"); idx = h.IndexOf('{', idx);
var depth = 0; var end = idx;
for (int i = idx; i < h.Length; i++) { if (h[i] == '{') depth++; else if (h[i] == '}') { depth--; if (depth == 0) { end = i + 1; break; } } }
var doc = JsonDocument.Parse(h.Substring(idx, end - idx));
var genres = new HashSet<string> { "动作", "剧情", "科幻", "喜剧", "爱情", "恐怖", "悬疑", "惊悚", "犯罪", "冒险", "奇幻", "动画", "纪录", "战争", "历史", "歌舞", "家庭", "传记", "武侠", "古装", "运动", "音乐", "伦理" };

foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray().Take(5))
{
    var title = item.GetProperty("title").GetString() ?? "";
    var abs = item.TryGetProperty("abstract", out var a) ? a.GetString() ?? "" : "";
    string eng = "";
    foreach (var part in abs.Split(" / ").Select(p => p.Trim()))
    {
        if (part.EndsWith("分钟") || genres.Contains(part) || int.TryParse(part, out _)) continue;
        if (part.Any(c => c >= 0x4e00 && c <= 0x9fff)) continue;
        if (part is "美国" or "中国" or "日本" or "韩国" or "英国" or "法国" or "德国" or "印度" or "加拿大" or "意大利" or "西班牙" or "俄罗斯" or "泰国" or "香港" or "台湾") continue;
        if (!string.IsNullOrEmpty(part) && part.Length > 1) { eng = part; break; }
    }
    Console.WriteLine($"{title}: eng=[{eng}] abs=[{abs}]");
}

var testTitle = "保护者 A Man of Reason AC3";
var cleaned = Regex.Replace(testTitle, @"[\u4e00-\u9fff]+", " ");
cleaned = Regex.Replace(cleaned, @"\b(?:4K|1080p|720p|2160p|BluRay|WEB-DL|HDRip|x26[45]|AAC|DTS|DD5\.1|HEVC|10bit|AC3|HDR|SDR|Remux|HC|H264|H265|PROPER|REPACK|EXTENDED|UNCUT)\b", " ", RegexOptions.IgnoreCase);
cleaned = Regex.Replace(cleaned, @"\b\d{4}\b", " ");
cleaned = Regex.Replace(cleaned, @"[.\-_]", " ");
cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
Console.WriteLine($"\nHint: [{cleaned}]");
