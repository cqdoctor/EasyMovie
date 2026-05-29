using System.Windows;
using EasyMovie.Core.Models;

namespace EasyMovie.Client.Views;

public partial class CollectionEditDialog : Window
{
    public CollectionEditDialog()
    {
        InitializeComponent();
        Title = LanguageManager.GetString("Collection_AddTitle");
    }

    public CollectionEditDialog(MovieCollection existing)
    {
        InitializeComponent();
        NameBox.Text = existing.Name;
        DescBox.Text = existing.Description ?? "";
        Title = LanguageManager.GetString("Collection_EditTitle");
    }

    public string CollectionName => NameBox.Text.Trim();
    public string CollectionDescription => DescBox.Text.Trim();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            MessageBox.Show(LanguageManager.GetString("Collection_NameRequired"), LanguageManager.GetString("Msg_Hint"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
