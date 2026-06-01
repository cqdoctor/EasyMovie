using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EasyMovie.Client.Views;

public partial class ShortcutSettingsDialog : Window
{
    private List<ShortcutConfig> _configs;
    private string? _recordingAction;
    private Border? _recordingBorder;

    public ShortcutSettingsDialog()
    {
        InitializeComponent();
        _configs = ShortcutConfig.LoadAll();
        RefreshList();
    }

    private void RefreshList()
    {
        ShortcutsPanel.Children.Clear();

        var header = new TextBlock
        {
            Text = "⌨️ " + LanguageManager.GetString("Shortcuts_Title"),
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 16),
            Foreground = SafeFindBrush("MaterialDesignBody", Colors.White)
        };
        ShortcutsPanel.Children.Add(header);

        var hint = new TextBlock
        {
            Text = LanguageManager.GetString("Shortcuts_ClickToEdit"),
            FontSize = 12,
            Foreground = SafeFindBrush("MaterialDesignHintForeground", Color.FromRgb(117, 117, 117)),
            Margin = new Thickness(0, 0, 0, 12)
        };
        ShortcutsPanel.Children.Add(hint);

        foreach (var cfg in _configs)
        {
            var descKey = ShortcutConfig.Defaults.FirstOrDefault(d => d.Action == cfg.Action).DescriptionKey;
            var desc = LanguageManager.GetString(descKey);

            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var descText = new TextBlock
            {
                Text = desc,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = SafeFindBrush("MaterialDesignBody", Colors.White)
            };
            Grid.SetColumn(descText, 0);
            row.Children.Add(descText);

            var keyBorder = new Border
            {
                Background = SafeFindBrush("MaterialDesignCardBackground", Color.FromRgb(45, 45, 45)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 5, 10, 5),
                HorizontalAlignment = HorizontalAlignment.Right,
                Cursor = Cursors.Hand,
                Tag = cfg.Action
            };
            var keyText = new TextBlock
            {
                Text = FormatGesture(cfg.KeyGesture),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Consolas"),
                Foreground = SafeFindBrush("MaterialDesignBody", Colors.White),
                Tag = cfg.Action
            };
            keyBorder.Child = keyText;

            keyBorder.MouseLeftButtonUp += KeyBorder_Click;
            Grid.SetColumn(keyBorder, 1);
            row.Children.Add(keyBorder);

            ShortcutsPanel.Children.Add(row);
        }
    }

    private static string FormatGesture(string gesture)
    {
        return gesture.Replace("OemQuestion", "/").Replace("D1", "1").Replace("D2", "2")
            .Replace("D3", "3").Replace("D4", "4");
    }

    private void KeyBorder_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.Tag is not string action) return;

        StopRecording();

        _recordingAction = action;
        _recordingBorder = border;

        var keyText = (TextBlock)border.Child;
        keyText.Text = LanguageManager.GetString("Shortcuts_PressKey");
        keyText.Foreground = new SolidColorBrush(Color.FromRgb(121, 134, 203));
        border.Background = new SolidColorBrush(Color.FromArgb(40, 121, 134, 203));

        PreviewKeyDown += Recording_PreviewKeyDown;
        Focus();
    }

    private void Recording_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recordingAction == null) return;

        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            StopRecording();
            return;
        }

        var modifiers = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.LeftCtrl || key == Key.RightCtrl ||
            key == Key.LeftAlt || key == Key.RightAlt ||
            key == Key.LeftShift || key == Key.RightShift)
            return;

        var gestureStr = "";
        if (modifiers.HasFlag(ModifierKeys.Control)) gestureStr += "Ctrl+";
        if (modifiers.HasFlag(ModifierKeys.Alt)) gestureStr += "Alt+";
        if (modifiers.HasFlag(ModifierKeys.Shift)) gestureStr += "Shift+";
        gestureStr += key.ToString();

        var cfg = _configs.FirstOrDefault(c => c.Action == _recordingAction);
        if (cfg != null)
        {
            var conflict = _configs.FirstOrDefault(c => c.Action != _recordingAction && c.KeyGesture == gestureStr);
            if (conflict != null)
            {
                var conflictDesc = ShortcutConfig.Defaults.FirstOrDefault(d => d.Action == conflict.Action).DescriptionKey;
                AppMessageBox.ShowWarning(
                    string.Format(LanguageManager.GetString("Shortcuts_Conflict"), LanguageManager.GetString(conflictDesc)),
                    LanguageManager.GetString("Msg_Hint"));
                StopRecording();
                return;
            }

            cfg.KeyGesture = gestureStr;
        }

        StopRecording();
        RefreshList();
    }

    private void StopRecording()
    {
        if (_recordingAction != null)
        {
            PreviewKeyDown -= Recording_PreviewKeyDown;
            _recordingAction = null;
        }
        _recordingBorder = null;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        StopRecording();
        ShortcutConfig.SaveAll(_configs);

        if (Owner is MainWindow mw)
            mw.ApplyShortcuts();

        DialogResult = true;
        Close();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        StopRecording();
        _configs = ShortcutConfig.GetDefaults();
        RefreshList();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        StopRecording();
        DialogResult = false;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        StopRecording();
        base.OnClosed(e);
    }

    private static Brush SafeFindBrush(string resourceKey, Color fallback)
    {
        var brush = Application.Current.TryFindResource(resourceKey) as Brush;
        if (brush != null) return brush;
        var solid = new SolidColorBrush(fallback);
        solid.Freeze();
        return solid;
    }
}
