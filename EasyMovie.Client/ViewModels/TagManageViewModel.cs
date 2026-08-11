using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;

namespace EasyMovie.Client.ViewModels;

/// <summary>
/// 标签管理 ViewModel（增量 MVVM）：承载标签列表状态与命令，并转发 ITagService
/// 供代码后置渐进调用。后续可把 XAML 绑定逐步迁到本 ViewModel。
/// </summary>
public partial class TagManageViewModel : ObservableObject
{
    private readonly ITagService _tagService;

    public TagManageViewModel(ITagService tagService)
    {
        _tagService = tagService;
    }

    // 供后续 XAML 绑定逐步迁移使用
    [ObservableProperty]
    private ObservableCollection<Tag> _tags = new();

    [ObservableProperty]
    private Tag? _selectedTag;

    // 转发 ITagService，供代码后置渐进调用（后续可改为 XAML 绑定到命令）
    public Task<Tag?> GetByIdAsync(int id) => _tagService.GetByIdAsync(id);
    public Task<List<Tag>> GetAllAsync() => _tagService.GetAllAsync();
    public Task<Tag> AddAsync(Tag tag) => _tagService.AddAsync(tag);
    public Task<Tag> UpdateAsync(Tag tag) => _tagService.UpdateAsync(tag);
    public Task<bool> DeleteAsync(int id) => _tagService.DeleteAsync(id);
    public Task<List<Tag>> GetTagsForMovieAsync(int movieId) => _tagService.GetTagsForMovieAsync(movieId);
    public Task<int> GetMovieCountAsync(int tagId) => _tagService.GetMovieCountAsync(tagId);

    [RelayCommand]
    public async Task LoadAsync()
    {
        var all = await _tagService.GetAllAsync();
        Tags = new ObservableCollection<Tag>(all);
    }
}
