using System.Windows;
using EasyMovie.Client.Views;

namespace EasyMovie.Client;

public static class AppMessageBox
{
    private static string Str(string key) => LanguageManager.GetString(key);

    public static void ShowInfo(string message, string? title = null)
    {
        Show(message, title ?? Str("Msg_Hint"), false, "ℹ️", Str("Msg_Ok") ?? "OK");
    }

    public static void ShowWarning(string message, string? title = null)
    {
        Show(message, title ?? Str("Msg_Hint"), false, "⚠️", Str("Msg_Ok") ?? "OK");
    }

    public static void ShowError(string message, string? title = null)
    {
        Show(message, title ?? Str("Msg_Hint"), false, "❌", Str("Msg_Ok") ?? "OK");
    }

    public static bool Confirm(string message, string? title = null)
    {
        return Show(message, title ?? Str("Msg_Confirm"), true, "⚠️", Str("Msg_Yes") ?? "确定");
    }

    private static bool Show(string message, string title, bool isConfirm, string icon, string okText)
    {
        var owner = Application.Current.Windows.Cast<Window>().FirstOrDefault(w => w.IsActive);
        var dlg = new AppMessageDialog(message, title, isConfirm, icon, okText)
        {
            Owner = owner
        };
        return dlg.ShowDialog() == true;
    }
}
