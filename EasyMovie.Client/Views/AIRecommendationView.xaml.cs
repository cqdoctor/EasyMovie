using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EasyMovie.Client.ViewModels;
using EasyMovie.Core;
using EasyMovie.Core.Enums;
using EasyMovie.Data;
using EasyMovie.Tools.AIChat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EasyMovie.Client.Views;

public partial class AIRecommendationView : UserControl
{
    private readonly AIRecommendationViewModel _vm;
    private readonly AIChatService _aiService;
    private readonly List<ChatMessage> _chatHistory = new();
    private bool _isStreaming;
    private string? _cachedSystemPrompt;
    private CancellationTokenSource? _streamCts;

    public AIRecommendationView()
    {
        InitializeComponent();
        _vm = App.Services?.GetService<AIRecommendationViewModel>()
              ?? new AIRecommendationViewModel(DbHelper.CreateContext(), new AIChatService());
        _aiService = _vm.AiService;
        Loaded += async (_, _) =>
        {
            await PreBuildSystemPromptAsync();
            Dispatcher.BeginInvoke(UpdateUIState, DispatcherPriority.Background);
        };
    }

    private void UserControl_Unloaded(object sender, RoutedEventArgs e)
    {
        // 取消正在进行的流式请求
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = null;
    }

    /// <summary>预构建系统提示词（启动时异步执行，避免每次请求都查库）</summary>
    private async Task PreBuildSystemPromptAsync()
    {
        try
        {
            // 影库概况由 AiLibrarySummaryService 准备：SQL 计数 + 窄投影 + 小表内存分组，
            // 全程不读取 PosterData。原实现在这里全量加载（含 24 MB 海报 + 标签 JOIN），
            // 而生成的 prompt 里一个字节的海报都不需要。
            // 语义由 Tests/Core.Tests/AiLibrarySummaryTests.cs 的 Oracle 逐字段守护。
            var summary = await new AiLibrarySummaryService(_vm.Context).BuildAsync();

            _cachedSystemPrompt = $"""
你是 EasyMovie 的 AI 电影推荐助手。你了解用户的电影库，可以根据用户的偏好智能推荐电影。

## 用户的电影库概况
- 总电影数: {summary.Total} 部
- 已看: {summary.Watched} 部
- 想看: {summary.WantToWatch} 部
- 收藏: {summary.Favorites} 部

### 类型分布
{string.Join("\n", summary.Categories)}

### 最爱导演
{string.Join("\n", summary.TopDirectors)}

### 常用标签
{string.Join("\n", summary.Tags)}

### 已看且评分高的电影 (Top 15)
{string.Join("\n", summary.WatchedTop)}

### 用户标记"想看"的电影
{string.Join("\n", summary.WantWatchList)}

### 库中高分未看 (Top 20)
{string.Join("\n", summary.UnwatchedHighRated)}

## 你的任务
1. 根据用户用自然语言描述的需求，从上述电影库中推荐合适的电影
2. 推荐时说明推荐理由（如：同导演、同类型、评分高、符合用户口味等）
3. 如果用户想看库中没有的类型，可以从高分未看或同类电影中推荐
4. 回复格式友好，使用中文，适当使用 emoji 让对话更生动
5. 如果库中没有合适的电影，诚实告知并建议可以从"在线搜索"添加新电影
6. 回答简洁，每次最多推荐 5 部电影

## 注意事项
- 只推荐用户库中已有的电影
- 不要编造电影信息
- 推荐时列出电影名称、年份、评分、导演
""";
        }
        catch
        {
            _cachedSystemPrompt = "你是 EasyMovie 的 AI 电影推荐助手，帮助用户从电影库中推荐合适的电影。";
        }
    }

    private void UpdateUIState()
    {
        var configured = !string.IsNullOrWhiteSpace(AppSettings.AiApiEndpoint) &&
                         !string.IsNullOrWhiteSpace(AppSettings.AiModel);

        WelcomePanel.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
        NotConfiguredPanel.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
        MessageInput.IsEnabled = configured;
        SendBtn.IsEnabled = configured;
    }

    private async void SendBtn_Click(object sender, RoutedEventArgs e) => await SendMessageAsync();
    private async void MessageInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            await SendMessageAsync();
        }
    }

    private async void Suggestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string text)
        {
            MessageInput.Text = text;
            await SendMessageAsync();
        }
    }

    private async Task SendMessageAsync()
    {
        if (_isStreaming) return;

        var message = MessageInput.Text?.Trim();
        if (string.IsNullOrWhiteSpace(message)) return;

        WelcomePanel.Visibility = Visibility.Collapsed;
        MessageInput.Text = "";
        MessageInput.IsEnabled = false;
        SendBtn.IsEnabled = false;
        _isStreaming = true;

        AddMessageBubble("user", message);
        _chatHistory.Add(new ChatMessage { Role = "user", Content = message });

        var aiBubble = CreateAIBubble("");
        ChatPanel.Children.Add(aiBubble);
        ScrollToBottom();

        var aiContent = "";
        var hasError = false;

        try
        {
            var systemPrompt = _cachedSystemPrompt ?? "你是 EasyMovie 的 AI 电影推荐助手。";
            await foreach (var chunk in _aiService.ChatStreamAsync(systemPrompt, message, _chatHistory))
            {
                aiContent += chunk;

                // 检测错误前缀
                if (!hasError && chunk.StartsWith("❌"))
                    hasError = true;

                UpdateAIBubble(aiBubble, aiContent);
                ScrollToBottom();
            }
        }
        catch (Exception ex)
        {
            hasError = true;
            aiContent = $"❌ 请求失败: {ex.Message}\n\n请检查网络连接或 API 设置。";
            UpdateAIBubble(aiBubble, aiContent);
        }

        if (!hasError && !string.IsNullOrWhiteSpace(aiContent))
            _chatHistory.Add(new ChatMessage { Role = "assistant", Content = aiContent });

        _isStreaming = false;
        MessageInput.IsEnabled = true;
        SendBtn.IsEnabled = true;
        MessageInput.Focus();
    }

    #region UI Helpers

    private Brush SafeFindBrush(string resourceKey, Color fallback)
    {
        var brush = this.TryFindResource(resourceKey) as Brush
                   ?? Application.Current?.TryFindResource(resourceKey) as Brush
                   ?? new SolidColorBrush(fallback);
        if (!brush.IsFrozen)
            brush.Freeze();
        return brush;
    }

    private void AddMessageBubble(string role, string content)
    {
        var isUser = role == "user";
        var cardBg = SafeFindBrush("MaterialDesignCardBackground", Color.FromRgb(45, 45, 45));
        var bodyFg = SafeFindBrush("MaterialDesignBody", Colors.White);
        var darkBg = SafeFindBrush("MaterialDesignDarkBackground", Color.FromRgb(55, 71, 79));

        var bubble = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(isUser ? 60 : 0, 0, isUser ? 0 : 60, 12),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth = 520,
            Background = isUser ? darkBg : cardBg
        };

        var textBlock = new TextBlock
        {
            Text = content,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Foreground = isUser ? Brushes.White : bodyFg
        };

        bubble.Child = textBlock;
        ChatPanel.Children.Add(bubble);
        ScrollToBottom();
    }

    private Border CreateAIBubble(string initialContent)
    {
        var cardBg = SafeFindBrush("MaterialDesignCardBackground", Color.FromRgb(45, 45, 45));
        var bodyFg = SafeFindBrush("MaterialDesignBody", Colors.White);

        var bubble = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 60, 12),
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = 520,
            Background = cardBg,
            Tag = "aiBubble"
        };

        var textBlock = new TextBlock
        {
            Text = initialContent,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Foreground = bodyFg
        };

        bubble.Child = textBlock;
        return bubble;
    }

    private static void UpdateAIBubble(Border bubble, string content)
    {
        if (bubble.Child is TextBlock tb)
            tb.Text = content;
    }

    private void ScrollToBottom()
    {
        Dispatcher.BeginInvoke(() => ChatScrollViewer.ScrollToBottom(), DispatcherPriority.Background);
    }

    private void ClearChat_Click(object sender, RoutedEventArgs e)
    {
        _chatHistory.Clear();
        var toRemove = ChatPanel.Children.OfType<Border>()
            .Where(b => (b.Tag is string tag && tag == "aiBubble") ||
                        (b.HorizontalAlignment is HorizontalAlignment.Left or HorizontalAlignment.Right
                         && b != WelcomePanel && b != NotConfiguredPanel))
            .ToList();
        foreach (var b in toRemove) ChatPanel.Children.Remove(b);

        WelcomePanel.Visibility = Visibility.Visible;
        MessageInput.Focus();
    }

    private void GoToSettings_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mw)
            mw.NavigateTo("Settings");
    }

    #endregion
}