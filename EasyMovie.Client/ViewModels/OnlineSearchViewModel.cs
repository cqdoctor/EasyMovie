using EasyMovie.Core.Interfaces;

namespace EasyMovie.Client.ViewModels;

/// <summary>
/// 在线搜索 ViewModel（增量 MVVM）：承载影片/分类/标签三个服务，供代码后置渐进调用。
/// </summary>
public class OnlineSearchViewModel
{
    public OnlineSearchViewModel(
        IMovieService movieService,
        ICategoryService categoryService,
        ITagService? tagService = null)
    {
        MovieService = movieService;
        CategoryService = categoryService;
        TagService = tagService;
    }

    public IMovieService MovieService { get; }
    public ICategoryService CategoryService { get; }

    /// <summary>
    /// 自动打标用。缺省为 null 时搜索导入的影片不带标签（保持旧行为，不阻断导入）。
    /// </summary>
    public ITagService? TagService { get; }
}
