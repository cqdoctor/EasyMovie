using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;

namespace EasyMovie.Client.ViewModels;

/// <summary>
/// 分类管理 ViewModel（增量 MVVM 试点）：承载分类树状态与加载命令，
/// 并转发 ICategoryService 供代码后置渐进调用。后续可把 XAML 绑定逐步迁到本 ViewModel。
/// </summary>
public partial class CategoryManageViewModel : ObservableObject
{
    private readonly ICategoryService _categoryService;

    public CategoryManageViewModel(ICategoryService categoryService)
    {
        _categoryService = categoryService;
    }

    // 供后续 XAML 绑定逐步迁移使用
    [ObservableProperty]
    private ObservableCollection<Category> _categories = new();

    [ObservableProperty]
    private Category? _selectedCategory;

    // 转发 ICategoryService，供代码后置渐进调用（后续可改为 XAML 绑定到命令）
    public Task<Category?> GetByIdAsync(int id) => _categoryService.GetByIdAsync(id);
    public Task<List<Category>> GetChildrenAsync(int parentId) => _categoryService.GetChildrenAsync(parentId);
    public Task<List<Category>> GetCategoryTreeAsync() => _categoryService.GetCategoryTreeAsync();
    public Task<bool> CanDeleteAsync(int id) => _categoryService.CanDeleteAsync(id);
    public Task<Category> AddAsync(Category category) => _categoryService.AddAsync(category);
    public Task<Category> UpdateAsync(Category category) => _categoryService.UpdateAsync(category);
    public Task<bool> DeleteAsync(int id) => _categoryService.DeleteAsync(id);

    [RelayCommand]
    public async Task LoadAsync()
    {
        var tree = await _categoryService.GetCategoryTreeAsync();
        Categories = new ObservableCollection<Category>(tree);
    }
}
