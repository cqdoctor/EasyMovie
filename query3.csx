#r "nuget: Microsoft.Data.Sqlite, 9.0.0"

using System.Net;
using System.Net.Http;

var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
var http = new HttpClient(handler);
http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
http.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");

var html = await http.GetStringAsync("https://www.themoviedb.org/movie/1169800");

// 搜索国家相关内容
var lines = html.Split('\n');
for (int i = 0; i < lines.Length; i++)
{
    var line = lines[i].Trim();
    if (line.Contains("Countr") || line.Contains("countr") || line.Contains("production") || line.Contains("Origin") || line.Contains("China") || line.Contains("Hong Kong") || line.Contains("国家"))
    {
        var start = Math.Max(0, i - 2);
        var end = Math.Min(lines.Length - 1, i + 2);
        for (int j = start; j <= end; j++)
            Console.WriteLine((j + 1) + ": " + lines[j].Trim());
        Console.WriteLine("---");
    }
}

// 也搜索 section/facts 相关
Console.WriteLine("\n=== Facts section ===");
var factsIdx = html.IndexOf("facts", StringComparison.OrdinalIgnoreCase);
if (factsIdx >= 0)
{
    var snippet = html.Substring(Math.Max(0, factsIdx - 100), Math.Min(500, html.Length - Math.Max(0, factsIdx - 100)));
    Console.WriteLine(snippet);
}
