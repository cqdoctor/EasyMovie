using EasyMovie.Data;

namespace EasyMovie.Client.ViewModels;

/// <summary>
/// 关系图谱 ViewModel（增量 MVVM）：承载 MovieDbContext，供代码后置渐进调用。
/// </summary>
public class MovieRelationViewModel
{
    public MovieRelationViewModel(MovieDbContext context)
    {
        Context = context;
    }

    public MovieDbContext Context { get; }
}
