using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EasyMovie.Core;
using EasyMovie.Core.Enums;
using EasyMovie.Data;
using EasyMovie.Tools.AIChat;
using Microsoft.EntityFrameworkCore;

namespace EasyMovie.Client.Views;

public partial class AIRecommendationView : UserControl
{
    private readonly AIChatService _aiService = new();
    private readonly List<ChatMessage> _chatHistory = new();
    private bool _isStreaming;
    private string? _cachedSystemPrompt;

    public AIRecommendationView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            await PreBuildSystemPromptAsync();
            Dispatcher.BeginInvoke(UpdateUIState, DispatcherPriority.Background);
        };
    }

    /// <summary>预构建系统提示词（启动时异步执行，避免每次请求都查库）</summary>
    private async Task PreBuildSystemPromptAsync()
    {
        try
        {
            using var ctx = DbHelper.CreateContext();
            var movies = await ctx.Movies
                .Include(m => m.Category)
                .Include(m => m.MovieTags).ThenInclude(mt => mt.Tag)
                .ToListAsync();

            var total = movies.Count;
            var watched = movies.Count(m => m.WatchStatus == WatchStatus.Watched);
            var wantToWatch = movies.Count(m => m.WatchStatus == WatchStatus.WantToWatch);
            var favorites = movies.Count(m => m.IsFavorite);

            var categories = movies.Where(m => m.Category != null)
                .GroupBy(m => m.Category!.Name)
                .OrderByDescending(g => g.Count()).Take(10)
                .Select(g => $"{g.Key}({g.Count()}部)");

            var topDirectors = movies.Where(m => !string.IsNullOrEmpty(m.Director))
                .SelectMany(m => m.Director!.Split('/', ',').Select(d => d.Trim()))
                .GroupBy(d => d).OrderByDescending(g => g.Count()).Take(10)
                .Select(g => $"{g.Key}({g.Count()}部)");

            var tags = movies.SelectMany(m => m.MovieTags.Select(mt => mt.Tag?.Name))
                .Where(n => n != null).GroupBy(n => n).OrderByDescending(g => g.Count()).Take(10)
                .Select(g => $"{g.Key}({g.Count()}部)");

            var watchedMovies = movies.Where(m => m.WatchStatus == WatchStatus.Watched && m.Rating.HasValue)
                .OrderByDescending(m => m.Rating).Take(15)
                .Select(m => $"- {m.Title} ({m.Year}) ⭐{m.Rating} | {m.Director?.Split('/').FirstOrDefault() ?? ""} | {m.Category?.Name ?? ""}");

            var wantWatchList = movies.Where(m => m.WatchStatus == WatchStatus.WantToWatch)
                .Take(20).Select(m => $"- {m.Title} ({m.Year}) | {m.Category?.Name ?? ""}");

            var unwatched = movies.Where(m => m.WatchStatus == WatchStatus.NotWatched && m.Rating.HasValue)
                .OrderByDescending(m => m.Rating).Take(20)
                .Select(m => $"- {m.Title} ({m.Year}) ⭐{m.Rating} | {m.Director?.Split('/').FirstOrDefault() ?? ""} | {m.Category?.Name ?? ""}");

            _cachedSystemPrompt = $"""
你是 EasyMovie 的 AI 电影推荐助手。你了解用户的电影库，可以根据用户的偏好智能推荐电影。

## 用户的电影库概况
- 总电影数: {total} 部
- 已看: {watched} 部
- 想看: {wantToWatch} 部
- 收藏: {favorites} 部

### 类型分布
{string.Join("\n", categories)}

### 最爱导演
{string.Join("\n", topDirectors)}

### 常用标签
{string.Join("\n", tags)}

### 已看且评分高的电影 (Top 15)
{string.Join("\n", watchedMovies)}

### 用户标记"想看"的电影
{string.Join("\n", wantWatchList)}

### 库中高分未看 (Top 20)
{string.Join("\n", unwatched)}

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

    private void AddMessageBubble(string role, string content)
    {
        var isUser = role == "user";
        var cardBg = TryFindResource("MaterialDesignCardBackground") as Brush ?? new SolidColorBrush(Color.FromRgb(45, 45, 45));
        var bodyFg = TryFindResource("MaterialDesignBody") as Brush ?? Brushes.White;
        var darkBg = TryFindResource("MaterialDesignDarkBackground") as Brush ?? new SolidColorBrush(Color.FromRgb(55, 71, 79));

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
        var cardBg = TryFindResource("MaterialDesignCardBackground") as Brush ?? new SolidColorBrush(Color.FromRgb(45, 45, 45));
        var bodyFg = TryFindResource("MaterialDesignBody") as Brush ?? Brushes.White;

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