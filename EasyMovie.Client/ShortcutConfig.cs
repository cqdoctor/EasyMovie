using System.IO;
using System.Text.Json;
using System.Windows.Input;

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
        catch { return GetDefaults(); }
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
        catch { }
    }

    public static KeyGesture? ParseGesture(string gesture)
    {
        try { return (KeyGesture?)KeyGestureConverter.ConvertFromString(gesture); }
        catch { return null; }
    }

    private static readonly KeyGestureConverter KeyGestureConverter = new();
}
