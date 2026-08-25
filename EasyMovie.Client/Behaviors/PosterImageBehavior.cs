using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EasyMovie.Client.Helpers;
using EasyMovie.Core.Models;
using Serilog;

namespace EasyMovie.Client.Behaviors;

/// <summary>
/// 海报异步加载行为：将 Image.Source 的填充改为后台线程解码，避免列表/海报墙翻页时
/// 在主线程同步解码大图造成的卡顿。解码走 PosterCache.GetThumbnailAsync（磁盘缓存优先 + 固定尺寸）。
/// 用法：
///   &lt;Image behaviors:PosterImageBehavior.Movie="{Binding}"
///          behaviors:PosterImageBehavior.DecodeWidth="185"
///          behaviors:PosterImageBehavior.DecodeHeight="278" /&gt;
/// 通过 Token 防止虚拟化回收后旧图错位/闪烁。
/// </summary>
public static class PosterImageBehavior
{
    private static readonly DependencyProperty MovieProperty =
        DependencyProperty.RegisterAttached("Movie", typeof(Movie), typeof(PosterImageBehavior), new PropertyMetadata(null, OnChanged));
    private static readonly DependencyProperty DecodeWidthProperty =
        DependencyProperty.RegisterAttached("DecodeWidth", typeof(int), typeof(PosterImageBehavior), new PropertyMetadata(0, OnChanged));
    private static readonly DependencyProperty DecodeHeightProperty =
        DependencyProperty.RegisterAttached("DecodeHeight", typeof(int), typeof(PosterImageBehavior), new PropertyMetadata(0, OnChanged));

    // 每次属性变化递增，异步回填时校验，避免回收后旧图错位
    private static readonly DependencyProperty TokenProperty =
        DependencyProperty.RegisterAttached("Token", typeof(int), typeof(PosterImageBehavior), new PropertyMetadata(0));
    // 合并同一帧内多次属性赋值的挂起标记
    private static readonly DependencyProperty PendingProperty =
        DependencyProperty.RegisterAttached("Pending", typeof(bool), typeof(PosterImageBehavior), new PropertyMetadata(false));

    public static Movie GetMovie(DependencyObject o) => (Movie)o.GetValue(MovieProperty);
    public static void SetMovie(DependencyObject o, Movie v) => o.SetValue(MovieProperty, v);
    public static int GetDecodeWidth(DependencyObject o) => (int)o.GetValue(DecodeWidthProperty);
    public static void SetDecodeWidth(DependencyObject o, int v) => o.SetValue(DecodeWidthProperty, v);
    public static int GetDecodeHeight(DependencyObject o) => (int)o.GetValue(DecodeHeightProperty);
    public static void SetDecodeHeight(DependencyObject o, int v) => o.SetValue(DecodeHeightProperty, v);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image img) return;
        img.SetValue(TokenProperty, (int)img.GetValue(TokenProperty) + 1);
        if (!(bool)img.GetValue(PendingProperty))
        {
            img.SetValue(PendingProperty, true);
            // 合并不完整的多次属性赋值（XAML 一行多属性会触发多次回调），等本帧结束再读取最终值一次性加载
            img.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                img.SetValue(PendingProperty, false);
                _ = BeginLoadAsync(img);
            }));
        }
    }

    private static async Task BeginLoadAsync(Image img)
    {
        int token = (int)img.GetValue(TokenProperty);
        var movie = GetMovie(img);
        int w = GetDecodeWidth(img);
        int h = GetDecodeHeight(img);

        if (movie == null)
        {
            img.Source = null;
            return;
        }

        img.Source = null; // 立即清空，避免回收时旧图闪烁
        try
        {
            var src = await PosterCache.GetThumbnailAsync(movie.Id, movie.PosterData, w, h);
            // 仅在仍是本批请求时回填，防止旧异步结果覆盖新图
            if ((int)img.GetValue(TokenProperty) == token && src != null)
                img.Source = src;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PosterImageBehavior 异步加载海报失败(已忽略)");
        }
    }
}
