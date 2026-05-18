using System.Windows;
using System.Windows.Controls;
using MovieManager.Client.Views;

namespace MovieManager.Client;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        NavListBox.SelectedIndex = 0;
        NavigateTo("Movies");
    }

    private void NavListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavListBox.SelectedItem is ListBoxItem item && item.Tag is string tag)
        {
            NavigateTo(tag);
        }
    }

    private void NavigateTo(string page)
    {
        ContentArea.Content = page switch
        {
            "Movies" => new MovieListView(),
            "Categories" => new CategoryManageView(),
            "Tags" => new TagManageView(),
            "Statistics" => new StatisticsView(),
            "ImportExport" => new ImportExportView(),
            _ => new MovieListView()
        };
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        NavListBox.SelectedIndex = -1;
        ContentArea.Content = new SettingsView();
    }
}
