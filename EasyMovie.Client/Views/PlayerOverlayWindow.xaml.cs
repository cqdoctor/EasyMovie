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

    // 拖动进度条实时 seek 的节流时间戳，以及松手后防止进度条回弹的定时器
    private int _lastDragSeekTick;
    private readonly DispatcherTimer _seekEndTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };

    public PlayerOverlayWindow(VideoPlayerHost host)
    {
        InitializeComponent();
        _host = host;
        VolumeSlider.Value = 80;
        // 键盘事件转发给宿主处理（覆盖窗口获得焦点时也能响应快捷键）
        this.KeyDown += Overlay_KeyDown;
        // 拖动/边缘 seek 松手后，保持 seeking 状态约 300ms 再结束，
        // 避免 VLC 后台 seek 未完成时进度条被 UpdateSeekBar 回弹到旧位置。
        _seekEndTimer.Tick += (s, e) =>
        {
            _seekEndTimer.Stop();
            _isSeeking = false;
            _host.EndSeek();
        };

        // 流畅度优化：关闭全部按钮的 MDIX 涟漪动画（分层窗口软件渲染下涟漪开销大），
        // 并对静态面板启用位图缓存，避免每次重合成从零重绘。
        OptimizeRendering(Root);
    }

    /// <summary>
    /// 递归遍历视觉树：关闭所有 Button 的涟漪动画，并对静态面板（跳过含拖动滑块的
    /// ControlBar / PicturePanel）启用 BitmapCache，降低分层窗口的软件渲染开销。
    /// </summary>
    private static void OptimizeRendering(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button btn)
                RippleAssist.SetIsDisabled(btn, true);
            if (child is Border border && border.Name != "ControlBar" && border.Name != "PicturePanel")
                border.CacheMode = new BitmapCache();
            OptimizeRendering(child);
        }
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
             || IsDescendantOf(src, RatePanel) || IsDescendantOf(src, MorePanel)
             || IsDescendantOf(src, PicturePanel) || IsDescendantOf(src, InfoPanel)))
        {
            return;
        }

        // 点击画面空白处：收起已打开的字幕/音轨/倍速/更多/画面/信息面板
        SubtitlePanel.Visibility = Visibility.Collapsed;
        AudioPanel.Visibility = Visibility.Collapsed;
        RatePanel.Visibility = Visibility.Collapsed;
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
            // 拖动期间只更新轻量文本（SeekTooltip/TimeLabel），避免每帧重写 SeekBar.Value
            // 触发 MDIX 滑块在软件分层窗口里的整窗重绘；最终位置在松手时一次性写入。
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
        SeekTooltip.Visibility = Visibility.Collapsed;
        SeekBar.Value = _edgeTargetMs;
        if (_edgeMoved)
        {
            _host.SeekTo(_edgeTargetMs, fast: false);
            // 松手保持 seeking 防回弹
            _seekEndTimer.Stop();
            _seekEndTimer.Start();
        }
        else
        {
            _host.EndSeek();
        }
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

    /// <summary>更新控制栏比例按钮上的常驻状态标签。</summary>
    public void SetAspectDisplay(string shortLabel) => AspectLabel.Text = shortLabel;

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
        // 再次点击同一按钮时收起面板（开/关切换）
        bool open = SubtitlePanel.Visibility != Visibility.Visible;
        AudioPanel.Visibility = Visibility.Collapsed;
        RatePanel.Visibility = Visibility.Collapsed;
        if (open)
        {
            AlignPanelToButton(SubtitlePanel, SubtitleBtn);
            _host.RequestSubtitlePanel();
        }
        else
            SubtitlePanel.Visibility = Visibility.Collapsed;
    }

    private void Audio_Click(object sender, RoutedEventArgs e)
    {
        // 再次点击同一按钮时收起面板（开/关切换）
        bool open = AudioPanel.Visibility != Visibility.Visible;
        SubtitlePanel.Visibility = Visibility.Collapsed;
        RatePanel.Visibility = Visibility.Collapsed;
        if (open)
        {
            AlignPanelToButton(AudioPanel, AudioBtn);
            _host.RequestAudioPanel();
        }
        else
            AudioPanel.Visibility = Visibility.Collapsed;
    }

    private void Rate_Click(object sender, RoutedEventArgs e)
    {
        bool open = RatePanel.Visibility != Visibility.Visible;
        // 打开倍速面板时收起其它面板（反之亦然）
        SubtitlePanel.Visibility = Visibility.Collapsed;
        AudioPanel.Visibility = Visibility.Collapsed;
        MorePanel.Visibility = Visibility.Collapsed;
        PicturePanel.Visibility = Visibility.Collapsed;
        InfoPanel.Visibility = Visibility.Collapsed;
        if (open) { AlignPanelToButton(RatePanel, RateBtn); _host.RequestRatePanel(); }
        else RatePanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>把弹出面板的左边缘对齐到触发按钮的左边缘（面板 VerticalAlignment=Bottom，底边距固定 64）。</summary>
    private void AlignPanelToButton(Border panel, FrameworkElement button)
    {
        var pos = button.TranslatePoint(new Point(0, 0), Root);
        panel.Margin = new Thickness(pos.X, 0, 0, 64);
    }
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
        _seekEndTimer.Stop();
    }

    private void SeekBar_DragCompleted(object sender, RoutedEventArgs e)
    {
        var target = (long)SeekBar.Value;
        _host.SeekTo(target, fast: false);
        // 松手后保持 seeking 一小段时间，避免 VLC 后台 seek 未完成时进度条回弹
        _seekEndTimer.Stop();
        _seekEndTimer.Start();
        ThumbPreview.Visibility = Visibility.Collapsed;   // 拖动结束隐藏缩略图预览
    }

    private void SeekBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_host == null) return;
        if (_isSeeking)
        {
            var target = (long)SeekBar.Value;
            TimeLabel.Text = $"{FormatTime(target)} / {FormatTime(_host.GetLength())}";
            // 拖动期间实时(fast)seek，让画面跟随滑块，提升跟手度；节流 80ms 避免过快连续 seek
            var now = Environment.TickCount;
            if (now - _lastDragSeekTick >= 80)
            {
                _lastDragSeekTick = now;
                _host.SeekTo(target, fast: true);
                UpdateThumbPreview(target);   // 随拖动显示对应位置的画面缩略图
            }
        }
    }

    // 进度条缩略图预览：按当前位置取最近预生成的缩略图，并让气泡水平跟随滑块
    private void UpdateThumbPreview(long time)
    {
        var path = _host.GetThumbnailForTime(time);
        if (path == null) { ThumbPreview.Visibility = Visibility.Collapsed; return; }
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.UriSource = new Uri(path);
            img.EndInit();
            ThumbImage.Source = img;
            ThumbTime.Text = FormatTime(time);
            var len = _host.GetLength();
            var x = len > 0 ? (time / (double)len) * SeekBar.ActualWidth : SeekBar.ActualWidth / 2;
            ThumbPreview.Margin = new Thickness(Math.Max(0, x - ThumbPreview.Width / 2), -112, 0, 0);
            ThumbPreview.Visibility = Visibility.Visible;
        }
        catch { ThumbPreview.Visibility = Visibility.Collapsed; }
    }

    private void AbCycle_Click(object sender, RoutedEventArgs e)
    {
        _host.CycleAbPoint();
        UpdateAbButton();
    }

    private void UpdateAbButton()
    {
        var (a, b) = _host.GetAbState();
        AbBtnText.Text = a < 0 ? "AB" : (b < 0 ? "A•" : "AB•");
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
        // 控制栏隐藏时不更新 UI（折叠元素本就不参与渲染，避免分层窗口无谓重绘）
        if (ControlBar.Visibility != Visibility.Visible) return;
        // 节流到 ~10Hz：VLC 的 TimeChanged 触发频率远高于刷新所需，限制重绘次数以提升流畅度
        var now = DateTime.UtcNow;
        if ((now - _lastTimeUpdate).TotalMilliseconds < 100) return;
        _lastTimeUpdate = now;
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

    public void ShowRateOptions(double[] rates, int currentIndex, Action<int> onSelect)
    {
        RateOptionList.Children.Clear();
        for (int i = 0; i < rates.Length; i++)
        {
            int idx = i;
            // 显式用 TextBlock 承载文字并设置 Foreground，避免 MaterialDesignFlatButton 模板
            // 不继承 Button.Foreground，导致深色底上文字变黑、看不见。
            var tb = new TextBlock
            {
                Text = $"{rates[i]:0.##}x",
                Foreground = i == currentIndex
                    ? new SolidColorBrush(Color.FromRgb(0x7C, 0x4D, 0xFF))
                    : Brushes.White,
                FontSize = 13,
                FontWeight = i == currentIndex ? FontWeights.Bold : FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            var b = new Button
            {
                Content = tb,
                Style = (Style)Application.Current.FindResource("MaterialDesignFlatButton"),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 1, 0, 1),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            RippleAssist.SetIsDisabled(b, true);
            b.Click += (s, e) => { onSelect(idx); RatePanel.Visibility = Visibility.Collapsed; };
            RateOptionList.Children.Add(b);
        }
        RatePanel.Visibility = Visibility.Visible;
    }

    private RadioButton MakeTrackRadio(string name, int id, bool isChecked, Action<int> onSelect)
    {
        // 显式 TextBlock + White，避免 RadioButton 模板不继承 Foreground，导致深色底上文字变黑看不见。
        var tb = new TextBlock
        {
            Text = name,
            Foreground = Brushes.White,
            FontSize = 12
        };
        var rb = new RadioButton
        {
            Content = tb,
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 2),
            IsChecked = isChecked,
            Tag = id
        };
        RippleAssist.SetIsDisabled(rb, true);
        rb.Checked += (s, e) => onSelect(id);
        return rb;
    }

    private DispatcherTimer? _toastTimer;
    private DateTime _lastTimeUpdate = DateTime.MinValue;
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
        RatePanel.Visibility = Visibility.Collapsed;
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
                Content = new TextBlock { Text = PlayerShortcuts.KeyLabel(kv.Value), Foreground = Brushes.White, FontSize = 12 },
                Style = (Style)FindResource("MaterialDesignFlatButton"),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(8, 0, 0, 0),
                Tag = action
            };
            RippleAssist.SetIsDisabled(btn, true);
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
        // 自身也算（点击面板的空白内边距时，OriginalSource 就是该面板 Border 自身，
        // 不向上遍历，必须显式包含，否则空白点击会穿透到画面播放/暂停逻辑）。
        if (ReferenceEquals(child, parent)) return true;
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
