using EasyMovie.Core.Interfaces;

namespace EasyMovie.Client.ViewModels;

/// <summary>
/// 在线搜索 ViewModel（增量 MVVM）：承载 IMovieService 与 ICategoryService，供代码后置渐进调用。
/// </summary>
public class OnlineSearchViewModel
{
    public OnlineSearchViewModel(IMovieService movieService, ICategoryService categoryService)
    {
        MovieService = movieService;
        CategoryService = categoryService;
    }

    public IMovieService MovieService { get; }
    public ICategoryService CategoryService { get; }
}
