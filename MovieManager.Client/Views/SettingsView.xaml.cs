using System.Windows;
using System.Windows.Controls;

namespace MovieManager.Client.Views;

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

    private void DarkTheme_Click(object sender, RoutedEventArgs e) { if (!App.IsDarkTheme) App.ToggleTheme(); UpdateButtonStyles(); }
    private void LightTheme_Click(object sender, RoutedEventArgs e) { if (App.IsDarkTheme) App.ToggleTheme(); UpdateButtonStyles(); }

    private void UpdateButtonStyles()
    {
        DarkThemeBtn.Style = App.IsDarkTheme ? FindResource("MaterialDesignFlatButton") as Style : FindResource("MaterialDesignFlatButton") as Style;
        LightThemeBtn.Style = !App.IsDarkTheme ? FindResource("MaterialDesignFlatButton") as Style : FindResource("MaterialDesignFlatButton") as Style;
    }

    private void SaveNetwork_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.TmdbApiKey = TmdbKeyBox.Text?.Trim();
        AppSettings.HttpProxy = ProxyBox.Text?.Trim();
        AppSettings.DoubanCookie = DoubanCookieBox.Text?.Trim();
        MessageBox.Show("网络设置已保存", "设置");
    }
}
