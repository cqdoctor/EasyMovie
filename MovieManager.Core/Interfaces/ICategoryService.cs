using MovieManager.Core.Models;

namespace MovieManager.Core.Interfaces;

/// <summary>
/// 分类业务服务接口
/// </summary>
public interface ICategoryService
{
    Task<Category?> GetByIdAsync(int id);
    Task<List<Category>> GetAllAsync();
    Task<List<Category>> GetRootCategoriesAsync();
    Task<List<Category>> GetChildrenAsync(int parentId);
    Task<List<Category>> GetCategoryTreeAsync();
    Task<Category> AddAsync(Category category);
    Task<Category> UpdateAsync(Category category);
    Task<bool> DeleteAsync(int id);
    Task<bool> CanDeleteAsync(int id);
    Task<int> GetMovieCountAsync(int categoryId);
}
