using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MaterialDesignThemes.Wpf;

namespace EasyMovie.Client.Views;

/// <summary>
/// 独立的透明覆盖窗口：承载播放器的所有控件（返回栏/续播面板/控制栏）并捕获画面点击。
/// 它是顶级窗口，永远位于视频 HWND 之上，因此能正常接收鼠标点击——
/// 彻底规避“空气墙”问题（VideoView 内的视频子窗口会盖住其 Content 里的 WPF 控件并吞掉点击）。
/// 背景为透明画刷：视觉上能看到下方视频，同时能命中测试捕获点击。
/// </summary>
public partial class PlayerOverlayWindow : Window
{
    private readonly VideoPlayerHost _host;
    private bool _isSeeking;

    // 边缘拖动 seek 手势状态：在画面左右边缘带内按下并拖动来快进/快退。
    private bool _edgeSeeking;
    private double _edgeStartX;
    private double _edgeWidth;
    private double _edgeDir;      // 左边缘=+1（向右拖前进），右边缘=-1（向左拖前进）
    private long _edgeStartMs;
    private long _edgeTargetMs;
    private bool _edgeMoved;

    public PlayerOverlayWindow(VideoPlayerHost host)
    {
        InitializeComponent();
        _host = host;
        VolumeSlider.Value = 80;
        // 键盘事件转发给宿主处理（覆盖窗口获得焦点时也能响应快捷键）
        this.KeyDown += Overlay_KeyDown;
    }

    #region 画面点击：暂停/继续、双击全屏

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 续播决策面板显示期间，不响应画面点击（避免未决策就误播）
        if (ResumePanel.Visibility == Visibility.Visible) return;

        // 点到控件时不触发画面播放/暂停（避免误暂停）
        var src = e.OriginalSource as DependencyObject;
        if (src != null &&
            (IsDescendantOf(src, TitleBar) || IsDescendantOf(src, ControlBar) || IsDescendantOf(src, ResumePanel)
             || IsDescendantOf(src, SubtitlePanel) || IsDescendantOf(src, AudioPanel)
             || IsDescendantOf(src, MorePanel) || IsDescendantOf(src, PicturePanel) || IsDescendantOf(src, InfoPanel)))
        {
            return;
        }

        // 点击画面空白处：收起已打开的字幕/音轨/更多/画面/信息面板
        SubtitlePanel.Visibility = Visibility.Collapsed;
        AudioPanel.Visibility = Visibility.Collapsed;
        MorePanel.Visibility = Visibility.Collapsed;
        PicturePanel.Visibility = Visibility.Collapsed;
        InfoPanel.Visibility = Visibility.Collapsed;

        // 边缘拖动 seek：仅当落点在左右最窄边缘带（固定 50px）内才进入 seek 手势，
        // 避免用户点画面左/右侧想暂停时误入。拖动需超过 12px 才算有效 seek。
        var pos = e.GetPosition(Root);
        var w = Root.ActualWidth;
        if (w > 0)
        {
            var band = 50.0;
            bool onLeft = pos.X <= band;
            bool onRight = pos.X >= w - band;
            if (onLeft || onRight)
            {
                BeginEdgeSeek(pos.X, w, onLeft);
                e.Handled = true;
                return;
            }
        }

        if (e.ClickCount == 2)
            _host.RequestFullscreen();
        else
            _host.RequestTogglePlay();
    }

    private void BeginEdgeSeek(double startX, double width, bool onLeft)
    {
        _edgeSeeking = true;
        _edgeStartX = startX;
        _edgeWidth = width;
        _edgeDir = onLeft ? 1.0 : -1.0;
        _edgeStartMs = _host.GetCurrentTime();
        _edgeTargetMs = _edgeStartMs;
        _edgeMoved = false;
        _host.BeginSeek();
        ShowControls();          // 确保进度条可见，便于预览 seek 目标
        CaptureMouse();
    }

    private void Root_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_edgeSeeking) return;
        var pos = e.GetPosition(Root);
        var dx = pos.X - _edgeStartX;
        var len = _host.GetLength();
        if (_edgeWidth > 0 && len > 0)
        {
            var target = _edgeStartMs + (dx / _edgeWidth) * len * _edgeDir;
            target = Math.Max(0, Math.Min(len, target));
            _edgeTargetMs = (long)target;
            _edgeMoved = Math.Abs(dx) > 12;
            SeekBar.Maximum = len;
            SeekBar.Value = target;
            TimeLabel.Text = $"{FormatTime((long)target)} / {FormatTime(len)}";
            SeekTooltip.Text = FormatTime((long)target);
            SeekTooltip.Visibility = Visibility.Visible;
        }
    }

    private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_edgeSeeking) return;
        _edgeSeeking = false;
        ReleaseMouseCapture();
        _host.EndSeek();
        SeekTooltip.Visibility = Visibility.Collapsed;
        if (_edgeMoved)
            _host.SeekTo(_edgeTargetMs);
    }

    private void Root_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _host.AdjustVolume(e.Delta > 0 ? 5 : -5);
        e.Handled = true;
    }

    #endregion

    #region 控件事件 → 转交宿主

    private void PlayPause_Click(object sender, RoutedEventArgs e) => _host.RequestTogglePlay();
    private void Stop_Click(object sender, RoutedEventArgs e) => _host.RequestStop();
    private void Back_Click(object sender, RoutedEventArgs e) => _host.RequestBack();
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => _host.RequestFullscreen();
    private void ResumeYes_Click(object sender, RoutedEventArgs e) => _host.ResumeContinue();
    private void ResumeNo_Click(object sender, RoutedEventArgs e) => _host.ResumeFromStart();

    private void Aspect_Click(object sender, RoutedEventArgs e)
    {
        var label = _host.CycleAspectMode();
        AspectTooltipText.Text = $"画面比例：{label}";
        AspectTooltip.Visibility = Visibility.Visible;
        // 1.5 秒后隐藏提示
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        timer.Tick += (s, ev) => { AspectTooltip.Visibility = Visibility.Collapsed; timer.Stop(); };
        timer.Start();
    }

    private void Subtitle_Click(object sender, RoutedEventArgs e)
    {
        AudioPanel.Visibility = Visibility.Collapsed;
        _host.RequestSubtitlePanel();
    }

    private void Audio_Click(object sender, RoutedEventArgs e)
    {
        SubtitlePanel.Visibility = Visibility.Collapsed;
        _host.RequestAudioPanel();
    }

    private void Rate_Click(object sender, RoutedEventArgs e) => _host.RequestCycleRate();
    private void Snapshot_Click(object sender, RoutedEventArgs e) => _host.RequestSnapshot();
    private void SubDelayMinus_Click(object sender, RoutedEventArgs e) => _host.AdjustSubtitleDelay(-500);
    private void SubDelayPlus_Click(object sender, RoutedEventArgs e) => _host.AdjustSubtitleDelay(500);
    private void AudioDelayMinus_Click(object sender, RoutedEventArgs e) => _host.AdjustAudioDelay(-500);
    private void AudioDelayPlus_Click(object sender, RoutedEventArgs e) => _host.AdjustAudioDelay(500);

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // XAML 解析 InitializeComponent 时会先触发一次 ValueChanged，此时 _host 尚未赋值，必须守卫。
        if (_host == null) return;
        _host.SetVolume((int)VolumeSlider.Value);
    }

    private void SeekBar_DragStarted(object sender, RoutedEventArgs e)
    {
        _isSeeking = true;
        _host.BeginSeek();
    }

    private void SeekBar_DragCompleted(object sender, RoutedEventArgs e)
    {
        _host.SeekTo((long)SeekBar.Value);
        _isSeeking = false;
        _host.EndSeek();
    }

    private void SeekBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_host == null) return;
        if (_isSeeking)
            TimeLabel.Text = $"{FormatTime((long)SeekBar.Value)} / {FormatTime(_host.GetLength())}";
    }

    #endregion

    #region 宿主调用的视图更新接口

    public void SetPlaying(bool playing)
        => PlayPauseIcon.Kind = playing ? PackIconKind.Pause : PackIconKind.Play;

    public void SetTitle(string title) => WindowTitleLabel.Text = title;

    public void SetFullscreenIcon(bool isFullscreen)
        => FullscreenIcon.Kind = isFullscreen ? PackIconKind.FullscreenExit : PackIconKind.Fullscreen;

    public void ShowResume(TimeSpan ts)
    {
        ResumeTimeText.Text = ts.ToString(@"hh\:mm\:ss");
        ResumePanel.Visibility = Visibility.Visible;
        SetResumeMode(true); // 不透明深色底，挡住续播期间视频窗口的灰底
    }

    public void HideResume()
    {
        ResumePanel.Visibility = Visibility.Collapsed;
        SetResumeMode(false);
    }

    /// <summary>续播期间把整片背景设为不透明深色（遮住视频灰底）；播放时恢复极透明(alpha=1)以露出视频，
    /// 但绝不能用 Brushes.Transparent(alpha=0)——那会让点击穿透到视频 HWND，单击暂停/双击全屏失效。</summary>
    private void SetResumeMode(bool on)
        => Root.Background = on
            ? new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E))
            : (Brush)new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));

    public void ShowControls()
    {
        if (ResumePanel.Visibility == Visibility.Visible) return;
        TitleBar.Visibility = Visibility.Visible;
        ControlBar.Visibility = Visibility.Visible;
    }

    public void HideControls()
    {
        TitleBar.Visibility = Visibility.Collapsed;
        ControlBar.Visibility = Visibility.Collapsed;
        SubtitlePanel.Visibility = Visibility.Collapsed;
        AudioPanel.Visibility = Visibility.Collapsed;
        MorePanel.Visibility = Visibility.Collapsed;
        PicturePanel.Visibility = Visibility.Collapsed;
        InfoPanel.Visibility = Visibility.Collapsed;
    }

    public void SetVolumeDisplay(int v)
    {
        VolumeSlider.Value = v;
        VolumeIcon.Kind = v == 0 ? PackIconKind.VolumeOff
            : v < 50 ? PackIconKind.VolumeMedium
            : PackIconKind.VolumeHigh;
    }

    public void SetTime(long pos, long len)
    {
        if (_isSeeking) return;
        SeekBar.Maximum = len > 0 ? len : 1;
        SeekBar.Value = pos;
        TimeLabel.Text = $"{FormatTime(pos)} / {FormatTime(len)}";
    }

    public void SetRateDisplay(double rate)
        => RateLabel.Text = $"{rate:0.00}x";

    public void SetAudioDelayDisplay(long delayUs)
        => AudioDelayLabel.Text = $"{delayUs / 1000.0:0.0}s";

    public void ShowSubtitleTracks((int Id, string Name)[] tracks, int currentId, long delayUs)
    {
        SubtitleTrackList.Children.Clear();
        SubtitleTrackList.Children.Add(MakeTrackRadio("关闭字幕", -1, currentId == -1,
            id => _host.SetSubtitleTrack(id)));
        foreach (var t in tracks)
            SubtitleTrackList.Children.Add(MakeTrackRadio(t.Name, t.Id, t.Id == currentId,
                id => _host.SetSubtitleTrack(id)));
        SubDelayLabel.Text = $"{delayUs / 1000.0:0.0}s";
        SubtitlePanel.Visibility = Visibility.Visible;
    }

    public void ShowAudioTracks((int Id, string Name)[] tracks, int currentId)
    {
        AudioTrackList.Children.Clear();
        foreach (var t in tracks)
            AudioTrackList.Children.Add(MakeTrackRadio(t.Name, t.Id, t.Id == currentId,
                id => _host.SetAudioTrack(id)));
        AudioPanel.Visibility = Visibility.Visible;
    }

    private RadioButton MakeTrackRadio(string name, int id, bool isChecked, Action<int> onSelect)
    {
        var rb = new RadioButton
        {
            Content = name,
            Foreground = Brushes.White,
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 2),
            IsChecked = isChecked,
            Tag = id
        };
        rb.Checked += (s, e) => onSelect(id);
        return rb;
    }

    private DispatcherTimer? _toastTimer;
    public void ShowToast(string msg)
    {
        ToastText.Text = msg;
        Toast.Visibility = Visibility.Visible;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _toastTimer.Tick += (s, e) => { Toast.Visibility = Visibility.Collapsed; _toastTimer?.Stop(); };
        _toastTimer.Start();
    }

    #endregion

    #region P2 进阶：更多/画面增强/编码信息

    private void More_Click(object sender, RoutedEventArgs e)
    {
        bool open = MorePanel.Visibility != Visibility.Visible;
        SubtitlePanel.Visibility = Visibility.Collapsed;
        AudioPanel.Visibility = Visibility.Collapsed;
        PicturePanel.Visibility = Visibility.Collapsed;
        InfoPanel.Visibility = Visibility.Collapsed;
        MorePanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open) UpdateDecoderButtons(DecoderSettings.Current);
    }

    private void PictureMenu_Click(object s, RoutedEventArgs e) { MorePanel.Visibility = Visibility.Collapsed; _host.RequestPicturePanel(); }
    private void InfoMenu_Click(object s, RoutedEventArgs e) { MorePanel.Visibility = Visibility.Collapsed; _host.RequestInfoPanel(); }
    private void StepMenu_Click(object s, RoutedEventArgs e) { MorePanel.Visibility = Visibility.Collapsed; _host.RequestStepFrame(); }
    private void AbAMenu_Click(object s, RoutedEventArgs e) { MorePanel.Visibility = Visibility.Collapsed; _host.RequestSetAbA(); }
    private void AbBMenu_Click(object s, RoutedEventArgs e) { MorePanel.Visibility = Visibility.Collapsed; _host.RequestSetAbB(); }
    private void AbClearMenu_Click(object s, RoutedEventArgs e) { MorePanel.Visibility = Visibility.Collapsed; _host.RequestClearAb(); }
    private void MiniMenu_Click(object s, RoutedEventArgs e) { MorePanel.Visibility = Visibility.Collapsed; _host.RequestToggleMini(); }
    private void ShortcutsMenu_Click(object s, RoutedEventArgs e) { MorePanel.Visibility = Visibility.Collapsed; _host.RequestShortcutsPanel(); }

    private void DecodeSoft_Click(object s, RoutedEventArgs e) => SwitchDecoder(DecoderSettings.Mode.Software);
    private void DecodeHard_Click(object s, RoutedEventArgs e) => SwitchDecoder(DecoderSettings.Mode.Hardware);
    private void DecodeAuto_Click(object s, RoutedEventArgs e) => SwitchDecoder(DecoderSettings.Mode.Auto);

    private void SwitchDecoder(DecoderSettings.Mode mode)
    {
        _host.RequestSetDecoder(mode);
        UpdateDecoderButtons(mode);
        string tip = mode == DecoderSettings.Mode.Software ? "软件解码（无白块，CPU 占用高）"
                   : mode == DecoderSettings.Mode.Hardware ? "硬件解码（流畅，部分 GPU 可能白块）"
                   : "自动解码（由 VLC 选择）";
        ShowToast($"解码模式：{tip}（重新播放后生效）");
    }

    private void UpdateDecoderButtons(DecoderSettings.Mode mode)
    {
        DecodeSoftBtn.FontWeight = DecodeHardBtn.FontWeight = DecodeAutoBtn.FontWeight = FontWeights.Normal;
        if (mode == DecoderSettings.Mode.Software) DecodeSoftBtn.FontWeight = FontWeights.Bold;
        else if (mode == DecoderSettings.Mode.Hardware) DecodeHardBtn.FontWeight = FontWeights.Bold;
        else DecodeAutoBtn.FontWeight = FontWeights.Bold;
    }

    // 重绑快捷键时的“监听下一按键”状态
    private PlayerShortcuts.PlayerAction? _listeningAction;

    private void Overlay_KeyDown(object sender, KeyEventArgs e)
    {
        if (_listeningAction != null)
        {
            // 不接受 Esc（用于退出）与作为结构性键的 F
            if (e.Key != Key.Escape && e.Key != Key.F)
            {
                _host.SetShortcut(_listeningAction.Value, e.Key);
                _listeningAction = null;
                ShowShortcuts(_host.GetShortcuts());
            }
            e.Handled = true;
            return;
        }
        _host.HandleKey(e.Key);
    }

    private void BrightnessSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_host == null) return;
        _host.SetBrightness(BrightnessSlider.Value);
        BrightnessVal.Text = BrightnessSlider.Value.ToString("0.00");
    }
    private void ContrastSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_host == null) return;
        _host.SetContrast(ContrastSlider.Value);
        ContrastVal.Text = ContrastSlider.Value.ToString("0.00");
    }
    private void SaturationSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_host == null) return;
        _host.SetSaturation(SaturationSlider.Value);
        SaturationVal.Text = SaturationSlider.Value.ToString("0.00");
    }
    private void GammaSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_host == null) return;
        _host.SetGamma(GammaSlider.Value);
        GammaVal.Text = GammaSlider.Value.ToString("0.00");
    }
    private void HueSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_host == null) return;
        _host.SetHue((int)HueSlider.Value);
        HueVal.Text = ((int)HueSlider.Value).ToString();
    }
    private void PictureReset_Click(object s, RoutedEventArgs e)
    {
        _host.ResetPictureAdjust();
        BrightnessSlider.Value = 1; ContrastSlider.Value = 1; SaturationSlider.Value = 1;
        GammaSlider.Value = 1; HueSlider.Value = 0;
    }

    public void ShowPictureAdjust((float Brightness, float Contrast, float Saturation, float Gamma, int Hue, bool Enabled) adj)
    {
        BrightnessSlider.Value = adj.Brightness;
        ContrastSlider.Value = adj.Contrast;
        SaturationSlider.Value = adj.Saturation;
        GammaSlider.Value = adj.Gamma;
        HueSlider.Value = adj.Hue;
        PicturePanel.Visibility = Visibility.Visible;
    }

    public void ShowMediaInfo(System.Collections.Generic.List<(string Label, string Value)> info)
    {
        InfoList.Children.Clear();
        foreach (var (label, value) in info)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var l = new TextBlock
            {
                Text = label,
                Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Top
            };
            var v = new TextBlock
            {
                Text = value,
                Foreground = Brushes.White,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(l, 0);
            Grid.SetColumn(v, 1);
            row.Children.Add(l);
            row.Children.Add(v);
            InfoList.Children.Add(row);
        }
        InfoPanel.Visibility = Visibility.Visible;
    }

    public void SetMiniIcon(bool isMini) { /* 迷你模式状态指示（预留） */ }

    public void ShowShortcuts(System.Collections.Generic.Dictionary<PlayerShortcuts.PlayerAction, Key> shortcuts)
    {
        ShortcutList.Children.Clear();
        foreach (var kv in shortcuts)
        {
            var action = kv.Key;
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var label = new TextBlock
            {
                Text = PlayerShortcuts.ActionLabel(action),
                Foreground = Brushes.White,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            var btn = new Button
            {
                Content = PlayerShortcuts.KeyLabel(kv.Value),
                Style = (Style)FindResource("MaterialDesignFlatButton"),
                Foreground = Brushes.White,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(8, 0, 0, 0),
                Tag = action
            };
            btn.Click += (s, e) =>
            {
                _listeningAction = action;
                btn.Content = "按下新键…";
            };
            Grid.SetColumn(label, 0);
            Grid.SetColumn(btn, 1);
            row.Children.Add(label);
            row.Children.Add(btn);
            ShortcutList.Children.Add(row);
        }
        ShortcutsPanel.Visibility = Visibility.Visible;
    }

    #endregion

    private static bool IsDescendantOf(DependencyObject? child, DependencyObject? parent)
    {
        if (child == null || parent == null) return false;
        var p = VisualTreeHelper.GetParent(child);
        while (p != null)
        {
            if (ReferenceEquals(p, parent)) return true;
            p = VisualTreeHelper.GetParent(p);
        }
        return false;
    }

    private static string FormatTime(long ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms);
        return ts.TotalHours >= 1
            ? ts.ToString(@"hh\:mm\:ss")
            : ts.ToString(@"mm\:ss");
    }
}
