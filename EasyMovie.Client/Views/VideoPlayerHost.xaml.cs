using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.IO;
using System.Linq;
using EasyMovie.Core.Models;
using EasyMovie.Data;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

using Serilog;

namespace EasyMovie.Client.Views;

public partial class VideoPlayerHost : UserControl
{
    private static LibVLC? _libVLC;
    private MediaPlayer? _mediaPlayer;
    private Movie? _movie;
    private bool _isSeeking;
    private bool _isPlaying;
    private bool _isFullscreen;
    private Thickness _normalMargin = new(-24);
    private WindowState _previousWindowState = WindowState.Normal;
    private double _previousLeft, _previousTop, _previousWidth, _previousHeight;
    private CancellationTokenSource? _hideCts;
    private readonly DispatcherTimer _cursorTimer;
    private PlayerOverlayWindow? _overlay;

    // 直接读系统光标坐标（GetCursorPos），绕过 WPF 的 Mouse.GetPosition 在 HwndHost 上方不更新的坑：
    // 鼠标位于视频子窗口上方时，WPF 收不到鼠标消息，GetPosition 会“冻结”，导致轮询检测不到移动、
    // 控制栏不再弹出。GetCursorPos 始终反映真实光标位置。
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
        public int Width => right - left;
        public int Height => bottom - top;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;

    private const int GWL_STYLE = -16;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_SYSMENU = 0x00080000;
    private const int WS_MAXIMIZEBOX = 0x00010000;
    private const int WS_MINIMIZEBOX = 0x00020000;

    private POINT _lastOsCursor;
    private int _previousStyle;
    private RECT _previousRect;
    private System.Windows.Media.Brush? _previousBackground;

    public event EventHandler? Closed;

    public VideoPlayerHost()
    {
        InitializeComponent();

        _cursorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _cursorTimer.Tick += CursorTimer_Tick;

        if (_libVLC == null)
        {
            LibVLCSharp.Shared.Core.Initialize();
            // 关闭硬件解码（DXVA/D3D）：部分 GPU 上硬件解码帧会合成失败，画面出现白色/花块，
            // 故用纯软件解码（--avcodec-hw=none）。视频输出不再强指定（去掉 --vout），
            // 用 VLC 默认输出模块：direct3d11/d3d9/opengl 在信箱黑边处都渲染出白块，
            // 默认输出让 VLC 自选最合适的模块，黑边处理往往更正常。
            _libVLC = new LibVLC("--avcodec-hw=none", "--no-video-title-show");
        }
    }

    #region 覆盖窗口（独立透明顶级窗口，承载控件 + 画面点击）

    private void EnsureOverlay()
    {
        if (_overlay != null) return;
        var owner = Window.GetWindow(this);
        _overlay = new PlayerOverlayWindow(this)
        {
            Owner = owner,
            WindowState = WindowState.Normal
        };
        _overlay.Show();
        SyncOverlay();
    }

    /// <summary>把透明覆盖窗口精确贴合到视频显示区。处理 DPI 缩放并防御布局未就绪导致的 0 尺寸。</summary>
    private void SyncOverlay()
    {
        if (_overlay == null) return;
        if (ActualWidth <= 1 || ActualHeight <= 1)
        {
            Dispatcher.BeginInvoke(SyncOverlay, DispatcherPriority.Render);
            return;
        }

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget == null) return;

        // PointToScreen 返回物理像素；Window.Left/Top/Width/Height 是 DIP，需转换。
        var topLeft = PointToScreen(new Point(0, 0));
        var toDip = source.CompositionTarget.TransformFromDevice;
        _overlay.Left = topLeft.X * toDip.M11;
        _overlay.Top = topLeft.Y * toDip.M22;
        _overlay.Width = ActualWidth;
        _overlay.Height = ActualHeight;
    }

    #endregion

    private void CursorTimer_Tick(object? sender, EventArgs e)
    {
        if (!GetCursorPos(out POINT p)) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var topLeft = PointToScreen(new Point(0, 0));
        var x = (p.X / dpi.DpiScaleX) - topLeft.X;
        var y = (p.Y / dpi.DpiScaleY) - topLeft.Y;

        var over = x >= 0 && y >= 0 && x <= ActualWidth && y <= ActualHeight;
        var moved = p.X != _lastOsCursor.X || p.Y != _lastOsCursor.Y;
        _lastOsCursor = p;

        if (over && moved)
        {
            ShowControls();
        }
    }

    // 宽高比模式：fill=铺满(等比裁剪，不变形、无信箱，默认)、fit=原始比例(不变形，有信箱)、169=16:9、43=4:3
    private string _aspectMode = "fill";

    // 倍速档位（与 CycleRate 配合循环）
    private static readonly double[] _rates = { 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0 };
    private int _rateIndex = 2; // 默认 1.0x
    private long _subtitleDelay; // 字幕延迟，单位微秒
    private long _audioDelay;    // 音画同步延迟，单位微秒

    // 跨电影记忆的播放偏好（同一会话内沿用）
    private static readonly string[] _videoExts = { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".ts", ".m2ts", ".mpg", ".mpeg", ".webm", ".vob" };
    private static int _lastVolume = 80;
    private static int _lastRateIndex = 2;
    private static string _lastAspectMode = "fill";

    /// <summary>读取视频原始尺寸（播放后才有效）。</summary>
    private bool TryGetVideoSize(out int w, out int h)
    {
        w = h = 0;
        if (_mediaPlayer == null) return false;
        uint px = 0, py = 0;
        try
        {
            if (_mediaPlayer.Size(0, ref px, ref py) && px > 0 && py > 0)
            {
                w = (int)px; h = (int)py;
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>循环切换宽高比模式，返回当前模式的中文名（供覆盖窗提示）。</summary>
    public string CycleAspectMode()
    {
        _aspectMode = _aspectMode switch
        {
            "fill" => "fit",
            "fit" => "169",
            "169" => "43",
            _ => "fill"
        };
        _lastAspectMode = _aspectMode;
        ApplyAspectMode();
        return AspectModeLabel(_aspectMode);
    }

    public static string AspectModeLabel(string mode) => mode switch
    {
        "fill" => "铺满",
        "169" => "16:9",
        "43" => "4:3",
        _ => "原始比例"
    };

    /// <summary>把 VideoView 调整为目标宽高比并居中（targetAspect=null 时填满整个播放区）。
    /// 返回 VideoView 的实际宽高。信箱区域落在 VideoView 之外的 VideoPlayerHost 黑底上（显示黑色），
    /// 从而绕开 VideoView 内部 static 窗口信箱露白的问题。</summary>
    private (double w, double h) LayoutVideoView(double? targetAspect)
    {
        if (targetAspect == null)
        {
            // 填满播放区
            VideoView.Width = double.NaN;
            VideoView.Height = double.NaN;
            VideoView.HorizontalAlignment = HorizontalAlignment.Stretch;
            VideoView.VerticalAlignment = VerticalAlignment.Stretch;
            return (ActualWidth, ActualHeight);
        }
        double aspect = targetAspect.Value;
        double hostAspect = ActualWidth / ActualHeight;
        double w, h;
        if (hostAspect > aspect) { h = ActualHeight; w = h * aspect; }
        else { w = ActualWidth; h = w / aspect; }
        VideoView.Width = w;
        VideoView.Height = h;
        VideoView.HorizontalAlignment = HorizontalAlignment.Center;
        VideoView.VerticalAlignment = VerticalAlignment.Center;
        return (w, h);
    }

    private void ApplyAspectMode()
    {
        if (_mediaPlayer == null || ActualWidth <= 1 || ActualHeight <= 1) return;

        if (_aspectMode == "fit")
        {
            // 原始比例：VideoView 缩到视频比例并居中，VLC BestFit 填满它 → 完整画面、不变形，
            // 信箱在 VideoView 外的黑底上（黑色）。
            double? videoAspect = TryGetVideoSize(out int vw, out int vh) ? vw / (double)vh : null;
            LayoutVideoView(videoAspect);
            _mediaPlayer.Scale = 0;
            _mediaPlayer.AspectRatio = null;
            return;
        }

        // fill / 16:9 / 4:3：把 VideoView 布局到目标比例，再等比裁剪视频填满它 → 无信箱。
        double? target = _aspectMode switch
        {
            "169" => 16.0 / 9,
            "43" => 4.0 / 3,
            _ => (double?)null // fill：填满播放区
        };
        var (viewW, viewH) = LayoutVideoView(target);
        if (TryGetVideoSize(out int vw2, out int vh2) && vw2 > 0 && vh2 > 0)
        {
            _mediaPlayer.AspectRatio = null;
            _mediaPlayer.Scale = (float)Math.Max(viewW / vw2, viewH / vh2);
        }
    }

    private void ShowControls()
    {
        _overlay?.ShowControls();

        _hideCts?.Cancel();
        _hideCts = new CancellationTokenSource();
        var token = _hideCts.Token;
        Task.Delay(3000, token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
                Dispatcher.BeginInvoke(() => HideControls());
        }, token);
    }

    private void HideControls()
    {
        if (!_isPlaying) return;
        _overlay?.HideControls();
    }

    public void LoadMovie(Movie movie)
    {
        _movie = movie;
        EnsureOverlay();
        _overlay?.SetTitle(movie.Title);
        StartPlayback();
    }

    private void Host_Loaded(object sender, RoutedEventArgs e)
    {
        // 保存正常模式下的 Margin（ContentBorder.Padding=24 时 -24 抵消）。
        _normalMargin = Margin;

            // 跟随宿主尺寸/位置变化，保持覆盖窗口贴合；尺寸变化时重新布局 VideoView（所有比例模式都依赖播放区尺寸）
            this.SizeChanged += (s, ev) => { SyncOverlay(); ApplyAspectMode(); };
        if (Window.GetWindow(this) is Window w)
        {
            w.LocationChanged += (s, ev) => SyncOverlay();
            // 全屏/窗口缩放会改变主窗口尺寸，布局稳定后需重新贴合覆盖窗口
            // （否则全屏瞬间读到的还是旧尺寸，覆盖窗口偏上 → 返回栏溢出屏幕）
            w.SizeChanged += (s, ev) => SyncOverlay();
        }

        if (_movie != null && _mediaPlayer == null)
            StartPlayback();
    }

    private void StartPlayback()
    {
        if (_movie == null || _libVLC == null) return;

        Focus();
        EnsureOverlay();
        CleanupInner();

        try
        {
            _mediaPlayer = new MediaPlayer(_libVLC);
            VideoView.MediaPlayer = _mediaPlayer;
            // 应用跨电影记忆的偏好
            _aspectMode = _lastAspectMode;
            _rateIndex = _lastRateIndex;
            try { _mediaPlayer.Volume = _lastVolume; } catch { }
            _overlay?.SetVolumeDisplay(_lastVolume);
            var media = new Media(_libVLC, new Uri(_movie.FilePath!));
            ApplyExternalSubtitle(media);

            // 续播：用 :start-time 跳到记录处附近最近关键帧（稳定、不会像精确 seek 那样产生花屏）。
            // 不再在 Playing 事件里做精确 _mediaPlayer.Time 补 seek——那会重新触发解码花屏。
            if (_movie.PlaybackPosition > 0)
                media.AddOption($":start-time={(_movie.PlaybackPosition / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture)}");

            _mediaPlayer.Playing += (s, args) =>
            {
                // Playing 事件在 VLC 回调线程触发，仅更新状态/图标（BeginInvoke 回 UI 线程），
                // 绝不在该线程同步调用 Pause/Play，否则 VLC 内部死锁。
                _isPlaying = true;
                Dispatcher.BeginInvoke(() =>
                {
                    ApplyAspectMode();
                    ApplyRate();
                    _overlay?.SetPlaying(true);
                    // 视频原始尺寸在播放瞬间可能未就绪，延迟校正一次，确保布局/裁剪按真实比例生效
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                    timer.Tick += (s2, e2) => { ApplyAspectMode(); timer.Stop(); };
                    timer.Start();
                });
            };
            _mediaPlayer.Paused += (s, args) =>
            {
                _isPlaying = false;
                Dispatcher.BeginInvoke(() => _overlay?.SetPlaying(false));
            };
            _mediaPlayer.Stopped += (s, args) =>
            {
                _isPlaying = false;
                Dispatcher.BeginInvoke(() => _overlay?.SetPlaying(false));
            };
            _mediaPlayer.EndReached += (s, args) =>
            {
                _isPlaying = false;
                Dispatcher.BeginInvoke(() =>
                {
                    _overlay?.SetPlaying(false);
                    SavePosition(0);
                    PlayNext();
                });
            };
            _mediaPlayer.TimeChanged += (s, args) =>
            {
                if (!_isSeeking)
                    Dispatcher.BeginInvoke(UpdateSeekBar);
            };

            _cursorTimer.Start();

            if (_movie.PlaybackPosition > 0)
            {
                // 有续播进度：只准备媒体、不自动播放，弹出续播面板等用户决策。
                _mediaPlayer.Media = media;
                var ts = TimeSpan.FromMilliseconds(_movie.PlaybackPosition);
                _overlay?.ShowResume(ts);
            }
            else
            {
                _mediaPlayer.Play(media);
                ShowControls();
            }
        }
        catch (Exception ex)
        {
            AppMessageBox.ShowError($"播放失败: {ex.Message}");
            Close();
        }
    }

    #region 宿主对外接口（供覆盖窗口调用）

    public void RequestTogglePlay() => TogglePlayPause();
    public void RequestFullscreen() => ToggleFullscreen();
    public void RequestBack() => SavePositionAndClose();
    public void RequestStop() => SavePositionAndClose();

    public void SetVolume(int v)
    {
        if (_mediaPlayer == null) return;
        _mediaPlayer.Volume = v;
        _lastVolume = v;
        _overlay?.SetVolumeDisplay(v);
    }

    public void AdjustVolume(int delta)
    {
        if (_mediaPlayer == null) return;
        var v = Math.Clamp(_mediaPlayer.Volume + delta, 0, 200);
        SetVolume(v);
    }

    public void BeginSeek() => _isSeeking = true;
    public void EndSeek() => _isSeeking = false;

    public void SeekTo(long ms)
    {
        if (_mediaPlayer == null) return;
        _mediaPlayer.Time = ms;
    }

    public long GetLength() => _mediaPlayer?.Length ?? 0;
    public long GetCurrentTime() => _mediaPlayer?.Time ?? 0;

    public void ResumeContinue()
    {
        _overlay?.HideResume();
        _mediaPlayer?.Play();
        ShowControls();
    }

    public void ResumeFromStart()
    {
        if (_movie == null || _mediaPlayer == null || _libVLC == null) return;
        _overlay?.HideResume();
        _movie.PlaybackPosition = 0;
        SavePositionToDb(0);
        // 从头播：用不含 :start-time 的新媒体，避免仍跳到旧进度
        var fresh = new Media(_libVLC, new Uri(_movie.FilePath!));
        ApplyExternalSubtitle(fresh);
        _mediaPlayer.Media = fresh;
            _mediaPlayer.Play();
            ShowControls();
        }

        // ===== P0 播放增强：对外接口（供覆盖窗口调用） =====
        public void RequestCycleRate() => CycleRate();
        public void RequestSnapshot() => TakeSnapshot();
        public void RequestSubtitlePanel() => ShowSubtitlePanel();
        public void RequestAudioPanel() => ShowAudioPanel();

        /// <summary>供覆盖窗口转发键盘事件（覆盖窗口获得焦点时也能响应快捷键）。</summary>
    public void HandleKey(Key key)
    {
        switch (key)
        {
            case Key.Space:
                RequestTogglePlay();
                break;
            case Key.Escape:
                if (_isFullscreen) ToggleFullscreen();
                else SavePositionAndClose();
                break;
            case Key.Left:
                if (_mediaPlayer != null)
                    _mediaPlayer.Time = Math.Max(0, _mediaPlayer.Time - 5000);
                break;
            case Key.Right:
                if (_mediaPlayer != null)
                    _mediaPlayer.Time = Math.Min(_mediaPlayer.Length, _mediaPlayer.Time + 5000);
                break;
            case Key.Up:
                if (_overlay != null) SetVolume((int)Math.Min(100, _overlayVolume() + 5));
                break;
            case Key.Down:
                if (_overlay != null) SetVolume((int)Math.Max(0, _overlayVolume() - 5));
                break;
            case Key.F:
                ToggleFullscreen();
                break;
            case Key.M:
                SetVolume(_overlayVolume() > 0 ? 0 : 80);
                break;
            case Key.S:
                TakeSnapshot();
                break;
            case Key.C:
                CycleRate();
                break;
            case Key.OemComma:
                AdjustSubtitleDelay(-500);
                break;
            case Key.OemPeriod:
                AdjustSubtitleDelay(500);
                break;
        }
    }

    private int _overlayVolume() => (int)(_mediaPlayer?.Volume ?? 80);

    #endregion

    private void Host_Unloaded(object sender, RoutedEventArgs e)
    {
        Cleanup();
    }

    public void Cleanup()
    {
        _cursorTimer.Stop();
        SavePosition();
        CleanupInner();
        if (_overlay != null)
        {
            _overlay.Close();
            _overlay = null;
        }
    }

    private void CleanupInner()
    {
        VideoView.MediaPlayer = null;
        _mediaPlayer?.Stop();
        _mediaPlayer?.Dispose();
        _mediaPlayer = null;
    }

    public void Close()
    {
        Cleanup();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateSeekBar()
    {
        if (_mediaPlayer == null || _isSeeking || _mediaPlayer.Length <= 0) return;
        _overlay?.SetTime(_mediaPlayer.Time, _mediaPlayer.Length);
    }

    private void TogglePlayPause()
    {
        if (_mediaPlayer == null) return;
        if (_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.Pause();
            _isPlaying = false;
            _overlay?.SetPlaying(false);
        }
        else
        {
            _mediaPlayer.Play();
            _isPlaying = true;
            _overlay?.SetPlaying(true);
        }
    }

    private void LogDebug(string msg)
    {
        try
        {
            var dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "debug.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    private void ToggleFullscreen()
    {
        var window = Window.GetWindow(this) as MainWindow;
        if (window == null) { LogDebug("ToggleFullscreen: window is null"); return; }

        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        LogDebug($"ToggleFullscreen enter, _isFullscreen={_isFullscreen}, hwnd=0x{hwnd:X}, window L={window.Left},T={window.Top},W={window.Width},H={window.Height},State={window.WindowState}");

        if (_isFullscreen)
        {
            window.TitleBarBorder.Visibility = Visibility.Visible;
            window.NavBorder.Visibility = Visibility.Visible;
            window.StatusBar.Visibility = Visibility.Visible;
            window.RootGrid.RowDefinitions[0].Height = new GridLength(32);
            window.RootGrid.RowDefinitions[2].Height = GridLength.Auto;

            var cb = window.ContentBorder;
            cb.SetValue(Grid.ColumnProperty, 1);
            cb.SetValue(Grid.ColumnSpanProperty, 1);
            cb.SetValue(Grid.RowProperty, 1);
            cb.SetValue(Grid.RowSpanProperty, 2);
            cb.CornerRadius = new CornerRadius(12, 0, 0, 0);
            cb.Padding = new Thickness(24);

            // 非全屏时恢复 -24 Margin，抵消 ContentBorder.Padding=24，使播放区与 ContentBorder 边界对齐。
            Margin = _normalMargin;

            // 还原 Win32 样式与窗口几何
            SetWindowLong(hwnd, GWL_STYLE, _previousStyle);
            SetWindowPos(hwnd, HWND_TOP,
                _previousRect.left, _previousRect.top,
                _previousRect.Width, _previousRect.Height,
                SWP_SHOWWINDOW | SWP_FRAMECHANGED);

            // 还原主窗口背景（退出全屏恢复原色）
            if (_previousBackground != null)
                window.Background = _previousBackground;

            // 还原 WPF 属性
            window.WindowStyle = WindowStyle.SingleBorderWindow;
            window.ResizeMode = ResizeMode.CanResize;
            window.WindowState = WindowState.Normal;
            window.Left = _previousLeft;
            window.Top = _previousTop;
            window.Width = _previousWidth;
            window.Height = _previousHeight;
            window.WindowState = _previousWindowState;
            _overlay?.SetFullscreenIcon(false);
        }
        else
        {
            // 保存退出全屏前的窗口几何与 Win32 样式
            _previousWindowState = window.WindowState;
            _previousLeft = window.Left;
            _previousTop = window.Top;
            _previousWidth = window.Width;
            _previousHeight = window.Height;
            _previousStyle = GetWindowLong(hwnd, GWL_STYLE);
            GetWindowRect(hwnd, out _previousRect);

            window.TitleBarBorder.Visibility = Visibility.Collapsed;
            window.NavBorder.Visibility = Visibility.Collapsed;
            window.StatusBar.Visibility = Visibility.Collapsed;
            window.RootGrid.RowDefinitions[0].Height = new GridLength(0);
            window.RootGrid.RowDefinitions[2].Height = new GridLength(0);

            var cb = window.ContentBorder;
            cb.SetValue(Grid.ColumnProperty, 0);
            cb.SetValue(Grid.ColumnSpanProperty, 2);
            cb.SetValue(Grid.RowProperty, 0);
            cb.SetValue(Grid.RowSpanProperty, 3);
            cb.CornerRadius = new CornerRadius(0);
            cb.Padding = new Thickness(0);

            // 全屏时 Padding=0，必须把 Margin 也清零，否则播放区外扩 24px，
            // 覆盖窗口被顶上屏幕外 → 返回栏上部超出屏幕、顶部露出灰条。
            Margin = new Thickness(0);

            // 全屏黑底遮蔽：视频 HWND 顶部若留空隙，会露出主窗口浅色背景 → 上面的“白块”。
            // 全屏时把主窗口背景设为纯黑，即使有空隙也显示黑色而非白色。
            _previousBackground = window.Background;
            window.Background = System.Windows.Media.Brushes.Black;

            // 把窗口样式改成 WS_POPUP（去掉标题栏/边框/系统菜单），
            // Windows 才不会把窗口限制在工作区；再用 SetWindowPos 铺满整个显示器（含任务栏）。
            var style = _previousStyle;
            var newStyle = style & ~WS_CAPTION & ~WS_THICKFRAME & ~WS_SYSMENU & ~WS_MAXIMIZEBOX & ~WS_MINIMIZEBOX | WS_POPUP;
            SetWindowLong(hwnd, GWL_STYLE, newStyle);

            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFO)) };
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref mi))
            {
                LogDebug($"Fullscreen monitor rect: L={mi.rcMonitor.left},T={mi.rcMonitor.top},W={mi.rcMonitor.Width},H={mi.rcMonitor.Height}");
                window.WindowStyle = WindowStyle.None;
                window.ResizeMode = ResizeMode.NoResize;
                window.WindowState = WindowState.Normal;
                var ok = SetWindowPos(hwnd, HWND_TOP,
                    mi.rcMonitor.left, mi.rcMonitor.top,
                    mi.rcMonitor.Width, mi.rcMonitor.Height,
                    SWP_SHOWWINDOW | SWP_FRAMECHANGED);
                LogDebug($"SetWindowPos result={ok}, after L={window.Left},T={window.Top},W={window.Width},H={window.Height},State={window.WindowState}");
            }
            else
            {
                LogDebug($"Fullscreen fallback: monitor=0x{monitor:X}, GetMonitorInfo failed");
                window.WindowStyle = WindowStyle.None;
                window.WindowState = WindowState.Maximized;
            }
            _overlay?.SetFullscreenIcon(true);
        }
        _isFullscreen = !_isFullscreen;

        // 全屏布局刷新有延迟：连续几帧在 ApplicationIdle 优先级同步覆盖窗口，
        // 确保读到的是全屏后的真实尺寸/位置（返回栏贴顶、不溢出屏幕）。
        for (int i = 0; i < 3; i++)
            Dispatcher.BeginInvoke(new Action(SyncOverlay), DispatcherPriority.ApplicationIdle);
    }

    private void Host_KeyDown(object sender, KeyEventArgs e)
    {
        HandleKey(e.Key);
        e.Handled = true;
    }

    private void SavePositionAndClose()
    {
        SavePosition();
        Close();
    }

    private void SavePosition(long? position = null)
    {
        if (_mediaPlayer == null || _movie == null) return;
        var pos = position ?? _mediaPlayer.Time;

        if (_mediaPlayer.Length > 0 && pos > _mediaPlayer.Length - 3000)
            pos = 0;

        _movie.PlaybackPosition = pos;
        SavePositionToDb(pos);
    }

    private void SavePositionToDb(long position)
    {
        if (_movie == null) return;
        try
        {
            using var ctx = DbHelper.CreateContext();
            var dbMovie = ctx.Movies.Find(_movie.Id);
            if (dbMovie != null)
            {
                dbMovie.PlaybackPosition = position;
                ctx.SaveChanges();
            }
        }
        catch (Exception ex) { Log.Error(ex, "VideoPlayerHost 操作异常"); }
    }

    #region P0 播放增强：字幕/音轨/倍速/截图

    /// <summary>把同名外挂字幕作为附加字幕轨挂到媒体上（select=true 使其自动启用）。</summary>
    private void ApplyExternalSubtitle(Media media)
    {
        if (_movie?.FilePath == null) return;
        var sub = FindExternalSubtitle(_movie.FilePath);
        if (sub == null) return;
        try { media.AddOption($":sub-file={sub}"); }
        catch { /* 字幕加载失败不阻断播放 */ }
    }

    private string? FindExternalSubtitle(string moviePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(moviePath);
            var name = Path.GetFileNameWithoutExtension(moviePath);
            if (dir == null || name == null) return null;
            var exts = new[] { ".srt", ".ass", ".ssa", ".sub", ".vtt", ".smi", ".scc" };
            foreach (var ext in exts)
            {
                var exact = Path.Combine(dir, name + ext);
                if (File.Exists(exact)) return exact;
            }
            // 名称.zh.srt 等变体
            foreach (var ext in exts)
            {
                var hit = Directory.EnumerateFiles(dir, name + "*" + ext).FirstOrDefault();
                if (hit != null) return hit;
            }
        }
        catch { }
        return null;
    }

    public void CycleRate()
    {
        _rateIndex = (_rateIndex + 1) % _rates.Length;
        ApplyRate();
    }

    private void ApplyRate()
    {
        if (_mediaPlayer == null) return;
        var r = _rates[_rateIndex];
        _mediaPlayer.SetRate((float)r);
        _lastRateIndex = _rateIndex;
        _overlay?.SetRateDisplay(r);
    }

    public void TakeSnapshot()
    {
        if (_mediaPlayer == null || _movie == null) return;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EasyMovie", "Snapshots");
            Directory.CreateDirectory(dir);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var safe = string.Concat((_movie.Title ?? "snapshot").Split(Path.GetInvalidFileNameChars()));
            var path = Path.Combine(dir, $"{safe}_{stamp}.png");
            _mediaPlayer.TakeSnapshot(0, path, 0, 0);
            _overlay?.ShowToast($"截图已保存：{path}");
        }
        catch (Exception ex)
        {
            _overlay?.ShowToast($"截图失败：{ex.Message}");
        }
    }

    public void ShowSubtitlePanel()
    {
        if (_mediaPlayer == null) return;
        var desc = _mediaPlayer.SpuDescription;
        var tracks = desc.Select(t => (t.Id, t.Name ?? $"轨 {t.Id}")).ToArray();
        _overlay?.ShowSubtitleTracks(tracks, _mediaPlayer.Spu, _subtitleDelay);
    }

    public void ShowAudioPanel()
    {
        if (_mediaPlayer == null) return;
        var desc = _mediaPlayer.AudioTrackDescription;
        var tracks = desc.Select(t => (t.Id, t.Name ?? $"轨 {t.Id}")).ToArray();
        _overlay?.ShowAudioTracks(tracks, _mediaPlayer.AudioTrack);
    }

    public void SetSubtitleTrack(int id) => _mediaPlayer?.SetSpu(id);

    public void AdjustSubtitleDelay(int deltaMs)
    {
        if (_mediaPlayer == null) return;
        _subtitleDelay += deltaMs * 1000L; // SetSpuDelay 单位为微秒
        _mediaPlayer.SetSpuDelay(_subtitleDelay);
        var desc = _mediaPlayer.SpuDescription;
        var tracks = desc.Select(t => (t.Id, t.Name ?? $"轨 {t.Id}")).ToArray();
        _overlay?.ShowSubtitleTracks(tracks, _mediaPlayer.Spu, _subtitleDelay);
    }

    public void AdjustAudioDelay(int deltaMs)
    {
        if (_mediaPlayer == null) return;
        _audioDelay += deltaMs * 1000L; // SetAudioDelay 单位为微秒
        try { _mediaPlayer.SetAudioDelay(_audioDelay); } catch { }
        _overlay?.SetAudioDelayDisplay(_audioDelay);
    }

    public void SetAudioTrack(int id) => _mediaPlayer?.SetAudioTrack(id);

    #endregion

    #region P1 体验增强：手势/记忆/连播

    /// <summary>当前影片播放结束后，自动续播同目录按文件名排序的下一部视频。</summary>
    private void PlayNext()
    {
        if (_movie?.FilePath == null) return;
        try
        {
            var dir = Path.GetDirectoryName(_movie.FilePath);
            if (dir == null) return;
            var list = Directory.EnumerateFiles(dir)
                .Where(f => _videoExts.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var idx = list.FindIndex(f => f.Equals(_movie.FilePath, StringComparison.OrdinalIgnoreCase));
            if (idx < 0 || idx + 1 >= list.Count) return;
            var next = list[idx + 1];
            Movie? nextMovie = null;
            try { using var ctx = DbHelper.CreateContext(); nextMovie = ctx.Movies.FirstOrDefault(m => m.FilePath == next); }
            catch { }
            _movie = nextMovie ?? new Movie { FilePath = next, Title = Path.GetFileNameWithoutExtension(next) };
            _overlay?.SetTitle(_movie.Title ?? Path.GetFileNameWithoutExtension(next));
            StartPlayback();
        }
        catch { }
    }

    #endregion
}
