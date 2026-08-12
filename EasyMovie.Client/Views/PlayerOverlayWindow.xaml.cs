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
        this.KeyDown += (s, e) => _host.HandleKey(e.Key);
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
             || IsDescendantOf(src, SubtitlePanel) || IsDescendantOf(src, AudioPanel)))
        {
            return;
        }

        // 点击画面空白处：收起已打开的字幕/音轨面板
        SubtitlePanel.Visibility = Visibility.Collapsed;
        AudioPanel.Visibility = Visibility.Collapsed;

        // 边缘拖动 seek：落点在左右边缘带内则进入 seek 手势（不触发暂停），否则按普通点击处理
        var pos = e.GetPosition(Root);
        var w = Root.ActualWidth;
        if (w > 0)
        {
            var band = Math.Max(70, w * 0.08);
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
            _edgeMoved = Math.Abs(dx) > 4;
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
