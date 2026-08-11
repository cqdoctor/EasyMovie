using EasyMovie.Tools.MovieApi;

namespace EasyMovie.Client.ViewModels;

/// <summary>
/// MovieNewsView 的视图模型：持有通过 DI 解析的 MovieNewsService，
/// 便于在视图代码后置以外复用并消除手动 new 依赖。
/// </summary>
public class MovieNewsViewModel
{
    public MovieNewsService NewsService { get; }

    public MovieNewsViewModel(MovieNewsService newsService)
    {
        NewsService = newsService;
    }
}
