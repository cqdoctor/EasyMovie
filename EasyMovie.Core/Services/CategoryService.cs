using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;

namespace EasyMovie.Core.Services;

/// <summary>
/// 分类业务服务
/// </summary>
public class CategoryService : ICategoryService
{
    private readonly ICategoryRepository _categoryRepo;

    public CategoryService(ICategoryRepository categoryRepo)
    {
        _categoryRepo = categoryRepo;
    }

    public async Task<Category?> GetByIdAsync(int id)
    {
        return await _categoryRepo.GetByIdAsync(id);
    }

    public async Task<List<Category>> GetAllAsync()
    {
        return await _categoryRepo.GetAllAsync();
    }

    public async Task<List<Category>> GetRootCategoriesAsync()
    {
        return await _categoryRepo.GetRootCategoriesAsync();
    }

    public async Task<List<Category>> GetChildrenAsync(int parentId)
    {
        return await _categoryRepo.GetChildrenAsync(parentId);
    }

    /// <summary>
    /// 获取完整分类树
    /// </summary>
    public async Task<List<Category>> GetCategoryTreeAsync()
    {
        var allCategories = await _categoryRepo.GetAllAsync();

        // 构建树结构
        var lookup = allCategories.ToLookup(c => c.ParentId);
        foreach (var category in allCategories)
        {
            category.Children = lookup[category.Id].ToList();
        }

        return lookup[null].ToList(); // 返回根节点
    }

    public async Task<Category> AddAsync(Category category)
    {
        if (string.IsNullOrWhiteSpace(category.Name))
            throw new ArgumentException("分类名称不能为空");

        // 验证父分类存在
        if (category.ParentId.HasValue)
        {
            if (!await _categoryRepo.ExistsAsync(category.ParentId.Value))
                throw new InvalidOperationException($"父分类 ID {category.ParentId} 不存在");
        }

        return await _categoryRepo.AddAsync(category);
    }

    public async Task<Category> UpdateAsync(Category category)
    {
        if (string.IsNullOrWhiteSpace(category.Name))
            throw new ArgumentException("分类名称不能为空");

        if (!await _categoryRepo.ExistsAsync(category.Id))
            throw new InvalidOperationException($"分类 ID {category.Id} 不存在");

        // 不能把自己设为父分类
        if (category.ParentId == category.Id)
            throw new InvalidOperationException("不能将自身设为父分类");

        // 检测循环引用：沿父链向上遍历，确保不会形成环
        if (category.ParentId.HasValue)
        {
            var visited = new HashSet<int> { category.Id };
            var currentId = category.ParentId.Value;
            while (currentId != 0)
            {
                if (!visited.Add(currentId))
                    throw new InvalidOperationException("不能创建循环的父分类关系");
                var parent = await _categoryRepo.GetByIdAsync(currentId);
                if (parent == null) break;
                currentId = parent.ParentId ?? 0;
            }
        }

        return await _categoryRepo.UpdateAsync(category);
    }

    public async Task<bool> DeleteAsync(int id)
    {
        return await _categoryRepo.DeleteAsync(id);
    }

    public async Task<bool> CanDeleteAsync(int id)
    {
        var category = await _categoryRepo.GetByIdAsync(id);
        if (category == null) return false;

        // 有子分类不能删除
        if (category.Children.Any()) return false;

        // 有电影关联不能删除
        if (await _categoryRepo.HasMoviesAsync(id)) return false;

        return true;
    }

    public async Task<int> GetMovieCountAsync(int categoryId)
    {
        if (!await _categoryRepo.ExistsAsync(categoryId))
            throw new InvalidOperationException($"分类 ID {categoryId} 不存在");

        return await _categoryRepo.GetMovieCountAsync(categoryId);
    }

    public async Task<Category> GetOrCreateByNameAsync(string name, int? parentId = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("分类名称不能为空");

        if (int.TryParse(name, out _))
            throw new ArgumentException("分类名称不能为纯数字");

        // 过滤豆瓣等来源的垃圾分类名
        var junkKeywords = new[] { "人收藏", "人评论", "人看", "人想看", "人看过", "人评价", "人关注", "人推荐" };
        if (junkKeywords.Any(j => name.Contains(j)))
            throw new ArgumentException("分类名称包含无效关键词: " + name);

        var all = await _categoryRepo.GetAllAsync();
        var existing = all.FirstOrDefault(c => c.Name == name && c.ParentId == parentId);
        if (existing != null) return existing;

        return await _categoryRepo.AddAsync(new Category { Name = name, ParentId = parentId });
    }
}
