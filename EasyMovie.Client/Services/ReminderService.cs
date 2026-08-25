using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EasyMovie.Client.Helpers;
using EasyMovie.Core;
using EasyMovie.Core.Enums;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;
using EasyMovie.Tools.MovieApi;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace EasyMovie.Client.Services;

/// <summary>
/// 上映提醒：把本地"想看"清单与豆瓣正在热映/即将上映做标题匹配。
/// 只读，绝不修改任何个人数据（WatchStatus / Rating / 笔记等）。
/// 网络异常或解析失败时静默返回空列表，不影响其它功能。
/// </summary>
public static class ReminderService
{
    /// <summary>匹配结果：本地电影 + 资讯侧的海报/评分（仅用于展示，不写回数据库）</summary>
    public class UpcomingReminder
    {
        public Movie Movie { get; set; } = null!;
        public string? PosterUrl { get; set; }
        public double? Rating { get; set; }
        public string? Source { get; set; }
    }

    /// <summary>取"想看"清单中命中当前热映/即将上映的电影（标题归一化匹配）。</summary>
    public static async Task<List<UpcomingReminder>> GetUpcomingWatchlistAsync()
    {
        var result = new List<UpcomingReminder>();
        try
        {
            List<Movie> watchlist;
            using (var ctx = DbHelper.CreateContext())
            {
                watchlist = await ctx.Movies
                    .Where(m => m.WatchStatus == WatchStatus.WantToWatch)
                    .AsNoTracking()
                    .ToListAsync();
            }

            if (watchlist.Count == 0) return result;

            var news = new MovieNewsService();
            var items = new List<MovieNewsItem>();
            var coming = await news.GetComingSoonAsync();
            if (coming.Success) items.AddRange(coming.Items);
            if (AppSettings.ReleaseReminderEnabled && AppSettings.ReleaseReminderIncludeNowPlaying)
            {
                var now = await news.GetNowPlayingAsync();
                if (now.Success) items.AddRange(now.Items);
            }

            // 以归一化标题为键建索引（取每个标题的首个资讯项）
            var index = items
                .Where(i => !string.IsNullOrWhiteSpace(i.Title))
                .GroupBy(i => Normalize(i.Title!))
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            foreach (var m in watchlist)
            {
                var key = Normalize(m.Title ?? "");
                if (string.IsNullOrEmpty(key)) continue;
                if (index.TryGetValue(key, out var hit))
                {
                    result.Add(new UpcomingReminder
                    {
                        Movie = m,
                        PosterUrl = hit.PosterUrl,
                        Rating = hit.Rating,
                        Source = hit.Source
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "上映提醒检查失败（已忽略）");
        }
        return result;
    }

    /// <summary>归一化标题用于匹配：去空白/标点/数字/罗马数字、转小写。宁可漏报不误报。</summary>
    private static string Normalize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c)) continue;
            if (char.IsPunctuation(c) || char.IsSymbol(c)) continue;
            if (c is >= '0' and <= '9') continue;
            // 只移除罗马数字“符号”（Ⅰ-Ⅹ）。原代码会一并删除所有普通拉丁字母 I/V/X，
            // 导致 "Avatar"→"atar"、"X战警"→"战警" 等正常片名被变形，严重拉低匹配召回。
            // 现在仅处理罗马数字符号、普通字母保留；既覆盖“沙丘2 / 沙丘Ⅱ”又不会误伤正常片名。
            if (c is 'Ⅰ' or 'Ⅱ' or 'Ⅲ' or 'Ⅳ' or 'Ⅴ' or 'Ⅵ' or 'Ⅶ' or 'Ⅷ' or 'Ⅸ' or 'Ⅹ') continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
