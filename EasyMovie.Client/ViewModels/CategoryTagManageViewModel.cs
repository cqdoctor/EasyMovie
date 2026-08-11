using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;

namespace EasyMovie.Client.ViewModels;

/// <summary>
/// 分类+标签管理 ViewModel（增量 MVVM）：聚合 ICategoryService 与 ITagService，
/// 供代码后置渐进调用。后续可把 XAML 绑定逐步迁到本 ViewModel。
/// </summary>
public class CategoryTagManageViewModel
{
    public CategoryTagManageViewModel(ICategoryService categoryService, ITagService tagService)
    {
        CategoryService = categoryService;
        TagService = tagService;
    }

    public ICategoryService CategoryService { get; }
    public ITagService TagService { get; }
}
