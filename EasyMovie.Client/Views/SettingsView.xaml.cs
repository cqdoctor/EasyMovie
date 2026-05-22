using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EasyMovie.Client.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        TmdbKeyBox.Text = AppSettings.TmdbApiKey ?? "";
        ProxyBox.Text = AppSettings.HttpProxy ?? "";
        DoubanCookieBox.Text = AppSettings.DoubanCookie ?? "";
        UpdateButtonStyles();
    }

    private void SystemTheme_Click(object sender, RoutedEventArgs e) { App.SetTheme(AppThemeMode.System); UpdateButtonStyles(); }
    private void DarkTheme_Click(object sender, RoutedEventArgs e) { App.SetTheme(AppThemeMode.Dark); UpdateButtonStyles(); }
    private void LightTheme_Click(object sender, RoutedEventArgs e) { App.SetTheme(AppThemeMode.Light); UpdateButtonStyles(); }

    private void UpdateButtonStyles()
    {
        var mode = AppSettings.Theme;
        SystemThemeBtn.Opacity = mode == AppThemeMode.System ? 1.0 : 0.5;
        DarkThemeBtn.Opacity = mode == AppThemeMode.Dark ? 1.0 : 0.5;
        LightThemeBtn.Opacity = mode == AppThemeMode.Light ? 1.0 : 0.5;
    }

    private void SaveNetwork_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.TmdbApiKey = TmdbKeyBox.Text?.Trim();
        AppSettings.HttpProxy = ProxyBox.Text?.Trim();
        AppSettings.DoubanCookie = DoubanCookieBox.Text?.Trim();
        MessageBox.Show("网络设置已保存", "设置");
    }
}
