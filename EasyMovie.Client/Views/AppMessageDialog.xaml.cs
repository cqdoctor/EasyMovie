using System.Windows;

namespace EasyMovie.Client.Views;

public partial class AppMessageDialog : Window
{
    public bool IsConfirmMode { get; }

    public AppMessageDialog(string message, string title, bool isConfirm, string icon, string okText)
    {
        InitializeComponent();

        Title = title;
        MessageText.Text = message;
        IconText.Text = icon;
        OkButton.Content = okText ?? "OK";

        IsConfirmMode = isConfirm;
        if (isConfirm)
        {
            CancelButton.Visibility = Visibility.Visible;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
