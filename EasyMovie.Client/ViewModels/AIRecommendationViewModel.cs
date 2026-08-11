using EasyMovie.Data;
using EasyMovie.Tools.AIChat;

namespace EasyMovie.Client.ViewModels;

/// <summary>
/// AIRecommendationView 的视图模型：持有通过 DI 解析的 MovieDbContext 与 AIChatService，
/// 用于预构建系统提示词与流式对话。
/// </summary>
public class AIRecommendationViewModel
{
    public MovieDbContext Context { get; }
    public AIChatService AiService { get; }

    public AIRecommendationViewModel(MovieDbContext context, AIChatService aiService)
    {
        Context = context;
        AiService = aiService;
    }
}
