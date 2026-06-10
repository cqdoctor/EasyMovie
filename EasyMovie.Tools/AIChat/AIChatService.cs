using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EasyMovie.Core;

namespace EasyMovie.Tools.AIChat;

/// <summary>
/// AI 聊天服务 — 支持 OpenAI / Ollama / 兼容 API，进行智能电影推荐对话
/// </summary>
public class AIChatService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>发送流式聊天请求（非流式模式用于内部实现）</summary>
    private async Task<string> ChatRawAsync(string systemPrompt, string userMessage, List<ChatMessage> history)
    {
        var endpoint = AppSettings.AiApiEndpoint?.TrimEnd('/') ?? "https://api.openai.com/v1";
        var model = string.IsNullOrWhiteSpace(AppSettings.AiModel) ? "gpt-4o-mini" : AppSettings.AiModel;
        var apiKey = AppSettings.AiApiKey ?? "";

        var messages = new List<object>
        {
            new { role = "system", content = systemPrompt }
        };

        var recentHistory = history.TakeLast(20);
        foreach (var msg in recentHistory)
            messages.Add(new { role = msg.Role, content = msg.Content });

        messages.Add(new { role = "user", content = userMessage });

        var requestBody = new
        {
            model,
            messages,
            stream = false,
            temperature = 0.7,
            max_tokens = 2048
        };

        var json = JsonSerializer.Serialize(requestBody, JsonOpts);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var http = CreateHttpClient();
        http.Timeout = TimeSpan.FromMinutes(3);

        if (!string.IsNullOrWhiteSpace(apiKey))
            http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

        var response = await http.PostAsync($"{endpoint}/chat/completions", content);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(responseJson);
        var choices = doc.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() == 0) return "";

        var message = choices[0].GetProperty("message");
        return message.TryGetProperty("content", out var text) ? text.GetString() ?? "" : "";
    }

    /// <summary>发送消息——先做非流式请求，再按字符逐个输出模拟流式效果</summary>
    public async IAsyncEnumerable<string> ChatStreamAsync(string systemPrompt, string userMessage, List<ChatMessage> history)
    {
        var result = await GetResponseSafeAsync(systemPrompt, userMessage, history);

        if (string.IsNullOrEmpty(result.Content)) yield break;

        // 模拟流式输出
        const int chunkSize = 3;
        for (int i = 0; i < result.Content.Length; i += chunkSize)
        {
            yield return result.Content.Substring(i, Math.Min(chunkSize, result.Content.Length - i));
            await Task.Delay(15);
        }
    }

    private async Task<(string Content, bool IsError)> GetResponseSafeAsync(string systemPrompt, string userMessage, List<ChatMessage> history)
    {
        try
        {
            var content = await ChatRawAsync(systemPrompt, userMessage, history);
            return (content, false);
        }
        catch (Exception ex)
        {
            return ($"❌ 请求失败: {ex.Message}\n\n请检查网络连接或 API 设置。", true);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };

        var proxy = AppSettings.HttpProxy;
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            handler.Proxy = new System.Net.WebProxy(proxy);
            handler.UseProxy = true;
        }

        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.Add("User-Agent", "EasyMovie/1.0");
        return http;
    }
}

/// <summary>聊天消息</summary>
public class ChatMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
}