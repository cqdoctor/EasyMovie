using System.Windows;
using System.Windows.Controls;
using EasyMovie.Core.Models;

namespace EasyMovie.Client.Views;

public partial class WatchLogDialog : Window
{
    public int MovieId { get; }
    public DateTime WatchDate => WatchDatePicker.SelectedDate ?? DateTime.Today;
    public int? Rating => RatingCombo.SelectedIndex > 0 ? RatingCombo.SelectedIndex : null;
    public string LogLocation => LocationBox.Text.Trim();
    public string LogCompanion => CompanionBox.Text.Trim();
    public string LogNotes => NotesBox.Text.Trim();

    public WatchLogDialog(int movieId, string movieTitle) : this(movieId, movieTitle, null) { }

    public WatchLogDialog(int movieId, string movieTitle, WatchLog? existing)
    {
        InitializeComponent();
        MovieId = movieId;
        MovieTitleText.Text = movieTitle;

        if (existing != null)
        {
            Title = LanguageManager.GetString("WatchLog_EditTitle");
            WatchDatePicker.SelectedDate = existing.WatchDate;
            RatingCombo.SelectedIndex = existing.Rating ?? 0;
            LocationBox.Text = existing.Location ?? "";
            CompanionBox.Text = existing.Companion ?? "";
            NotesBox.Text = existing.Notes ?? "";
        }
        else
        {
            Title = LanguageManager.GetString("WatchLog_AddTitle");
            WatchDatePicker.SelectedDate = DateTime.Today;
            RatingCombo.SelectedIndex = 0;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (WatchDatePicker.SelectedDate == null)
        {
            AppMessageBox.ShowWarning(LanguageManager.GetString("WatchLog_DateRequired"), LanguageManager.GetString("Msg_Hint"));
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
