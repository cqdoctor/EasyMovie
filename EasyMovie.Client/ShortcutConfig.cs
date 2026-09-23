using System.IO;
using System.Text.Json;
using System.Windows.Input;

using Serilog;

namespace EasyMovie.Client;

public class ShortcutConfig
{
    public string Action { get; set; } = "";
    public string KeyGesture { get; set; } = "";

    private static readonly string SavePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EasyMovie", "shortcuts.json");

    public static readonly (string Action, string DefaultGesture, string DescriptionKey)[] Defaults =
    [
        ("Search", "Ctrl+F", "Shortcuts_Search"),
        ("AddNew", "Ctrl+N", "Shortcuts_AddNew"),
        ("Delete", "Delete", "Shortcuts_Delete"),
        ("Detail", "Enter", "Shortcuts_Detail"),
        ("Escape", "Escape", "Shortcuts_Escape"),
        ("Refresh", "F5", "Shortcuts_Refresh"),
        ("SelectAll", "Ctrl+A", "Shortcuts_SelectAll"),
        ("CycleView", "F3", "Shortcuts_CycleView"),
        ("Nav1", "Ctrl+D1", "Shortcuts_Nav1"),
        ("Nav2", "Ctrl+D2", "Shortcuts_Nav2"),
        ("Nav3", "Ctrl+D3", "Shortcuts_Nav3"),
        ("Nav4", "Ctrl+D4", "Shortcuts_Nav4"),
        ("ShortcutsHelp", "Ctrl+OemQuestion", "Shortcuts_Help"),
    ];

    public static List<ShortcutConfig> LoadAll()
    {
        try
        {
            if (!File.Exists(SavePath)) return GetDefaults();
            var json = File.ReadAllText(SavePath);
            return JsonSerializer.Deserialize<List<ShortcutConfig>>(json) ?? GetDefaults();
        }
        catch (Exception ex) { Log.Error(ex, "加载快捷键失败，使用默认值"); return GetDefaults(); }
    }

    public static List<ShortcutConfig> GetDefaults()
    {
        return Defaults.Select(d => new ShortcutConfig { Action = d.Action, KeyGesture = d.DefaultGesture }).ToList();
    }

    public static void SaveAll(List<ShortcutConfig> configs)
    {
        try
        {
            var dir = Path.GetDirectoryName(SavePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(configs, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SavePath, json);
        }
        catch (Exception ex) { Log.Error(ex, "快捷键加载异常"); }
    }

    /// <summary>
    /// 解析手势字符串。**空白输入与退化手势（Key.None）一律返回 null**，表示"该项不绑定快捷键"。
    /// <para>注意：<c>KeyGestureConverter.ConvertFromString("")</c> 不抛异常，而是返回
    /// <c>KeyGesture{ Key = None, Modifiers = None }</c>。若直接透传，调用方
    /// <c>if (gesture != null) InputBindings.Add(new KeyBinding(...))</c> 会注册一个永远不触发的
    /// 空绑定（用户清空快捷键后仍占用一项 InputBinding）。因此这里显式归一化为 null。</para>
    /// </summary>
    public static KeyGesture? ParseGesture(string gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture)) return null;
        try
        {
            var parsed = (KeyGesture?)KeyGestureConverter.ConvertFromString(gesture);
            // 退化手势（无按键）没有绑定价值，视为"未绑定"
            return parsed == null || parsed.Key == Key.None ? null : parsed;
        }
        catch (Exception ex) { Log.Error(ex, "读取快捷键失败"); return null; }
    }

    private static readonly KeyGestureConverter KeyGestureConverter = new();
}
