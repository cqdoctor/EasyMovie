using System;
using System.Windows;

namespace EasyMovie.Client;

/// <summary>
/// 多语言管理器，负责加载和切换语言资源字典
/// </summary>
public static class LanguageManager
{
    private const string LanguageDictPrefix = "Strings/Strings.";

    /// <summary>当前语言</summary>
    public static string CurrentLanguage => AppSettings.Language;

    /// <summary>初始化语言，在 App.Startup 中调用</summary>
    public static void Initialize()
    {
        ApplyLanguage(AppSettings.Language);
    }

    /// <summary>切换语言</summary>
    public static void SetLanguage(string lang)
    {
        if (lang == AppSettings.Language) return;
        AppSettings.Language = lang;
        ApplyLanguage(lang);
    }

    /// <summary>应用语言资源字典</summary>
    private static void ApplyLanguage(string lang)
    {
        var app = Application.Current;
        if (app == null) return;

        // 移除旧的语言字典
        for (int i = app.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            var dict = app.Resources.MergedDictionaries[i];
            if (dict.Source != null && dict.Source.OriginalString.Contains("Strings/Strings."))
            {
                app.Resources.MergedDictionaries.RemoveAt(i);
            }
        }

        // 加载新的语言字典
        var newDict = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/{LanguageDictPrefix}{lang}.xaml", UriKind.Absolute)
        };
        app.Resources.MergedDictionaries.Add(newDict);
    }

    /// <summary>获取当前语言的字符串资源</summary>
    public static string GetString(string key)
    {
        if (Application.Current.TryFindResource(key) is string value)
            return value;
        return key;
    }
}
