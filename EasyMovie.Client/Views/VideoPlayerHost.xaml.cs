using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
    private CancellationTokenSource? _hideCts;
    private readonly DispatcherTimer _cursorTimer;
    private Point _lastCursorPos = new(double.NaN, double.NaN);

    // VLC 帧回调渲染到 WriteableBitmap（视频是 WPF Image，控件可直接悬浮其上）
    private WriteableBitmap? _bitmap;
    // 用 GCHandle 把 bitmap 句柄传入 VLC 回调的 opaque，保证 Format/Lock/Display 始终操作同一个 bitmap，
    // 避免分辨率重协商时字段被重赋值导致 VLC 写入错误的 buffer（原生越界 → 进程闪退）。
    private GCHandle _bitmapHandle;

    public event EventHandler? Closed;

    public VideoPlayerHost()
    {
        InitializeComponent();

        // 视频区域是 WPF Image，但鼠标在 Image 上移动仍走 WPF 事件；
        // 用轮询统一检测（对嵌入/全屏一致），唤出控制栏
        _cursorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _cursorTimer.Tick += CursorTimer_Tick;

        if (_libVLC == null)
        {
            LibVLCSharp.Shared.Core.Initialize();
            // no-avcodec-corrupted：不显示 seek 到非关键帧时的损坏帧（色块/马赛克）
            _libVLC = new LibVLC("--no-avcodec-corrupted", "--no-video-title-show");
        }
    }

    private void CursorTimer_Tick(object? sender, EventArgs e)
    {
        var pos = Mouse.GetPosition(this);
        if (pos.X >= 0 && pos.Y >= 0 && pos.X <= ActualWidth && pos.Y <= ActualHeight)
        {
            if (pos != _lastCursorPos)
            {
                _lastCursorPos = pos;
                ShowControls();
            }
        }
    }

    private void ShowControls()
    {
        TitleBar.Visibility = Visibility.Visible;
        ControlBar.Visibility = Visibility.Visible;

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
        TitleBar.Visibility = Visibility.Collapsed;
        ControlBar.Visibility = Visibility.Collapsed;
    }

    public void LoadMovie(Movie movie)
    {
        _movie = movie;
        WindowTitleLabel.Text = movie.Title;
        TitleLabel.Text = movie.Title;
        StartPlayback();
    }

    private void Host_Loaded(object sender, RoutedEventArgs e)
    {
        if (_movie != null && _mediaPlayer == null)
            StartPlayback();
    }

    private void StartPlayback()
    {
        if (_movie == null || _libVLC == null) return;

        Focus();
        Cleanup(); // 先清理可能存在的旧播放器

        try
        {
            _mediaPlayer = new MediaPlayer(_libVLC);
            SetupVideoCallbacks();
            var media = new Media(_libVLC, new Uri(_movie.FilePath!));
            _mediaPlayer.Playing += (s, args) =>
            {
                _isPlaying = true;
                Dispatcher.BeginInvoke(() => PlayPauseIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.Pause);
            };
            _mediaPlayer.Paused += (s, args) =>
            {
                _isPlaying = false;
                Dispatcher.BeginInvoke(() => PlayPauseIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.Play);
            };
            _mediaPlayer.Stopped += (s, args) =>
            {
                _isPlaying = false;
                Dispatcher.BeginInvoke(() => PlayPauseIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.Play);
            };
            _mediaPlayer.EndReached += (s, args) =>
            {
                _isPlaying = false;
                Dispatcher.BeginInvoke(() =>
                {
                    PlayPauseIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.Play;
                    SavePosition(0);
                });
            };
            _mediaPlayer.TimeChanged += (s, args) =>
            {
                if (!_isSeeking)
                {
                    Dispatcher.BeginInvoke(() => UpdateSeekBar());
                }
            };

            _mediaPlayer.Play(media);

            _cursorTimer.Start();
            ShowControls();

            if (_movie.PlaybackPosition > 0)
            {
                var ts = TimeSpan.FromMilliseconds(_movie.PlaybackPosition);
                ResumeTimeText.Text = ts.ToString(@"hh\:mm\:ss");
                ResumePanel.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            AppMessageBox.ShowError($"播放失败: {ex.Message}");
            Close();
        }
    }

    #region VLC 帧回调渲染（稳健版：bitmap 全程只在 UI 线程创建/锁定/刷新，杜绝跨线程所有权崩溃）

    /// <summary>配置 VLC 直接输出 BGRA32 (RV32) 帧到 WriteableBitmap，替代 HwndHost 渲染</summary>
    private void SetupVideoCallbacks()
    {
        if (_mediaPlayer == null) return;
        _mediaPlayer.SetVideoFormatCallbacks(OnVideoFormatSetup, OnVideoCleanup);
        _mediaPlayer.SetVideoCallbacks(OnVideoLock, OnVideoUnlock, OnVideoDisplay);
    }

    /// <summary>VLC 线程：格式协商。WriteableBitmap 必须在 UI 线程创建（DispatcherObject 归属 UI 线程），
    /// 否则后续 VideoImage.Source 赋值或渲染会触发跨线程异常 / 原生 AV 导致进程闪退。</summary>
    private uint OnVideoFormatSetup(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        try
        {
            // 4CC "RV32" = BGRA32（与 WriteableBitmap Bgra32 布局一致，零拷贝）
            if (chroma != IntPtr.Zero)
                Marshal.WriteInt32(chroma, 0x32335652);
            pitches = width * 4;
            lines = height;

            var w = (int)width;
            var h = (int)height;
            WriteableBitmap? bmp = null;
            // 在 UI 线程创建 bitmap 并挂到 Image.Source（两者必须同一线程，保证所有权一致）
            Dispatcher.Invoke(() =>
            {
                bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                VideoImage.Source = bmp;
            });

            if (bmp == null) return 0;

            // GCHandle 随 opaque 传入 Lock/Display，保证始终操作同一个 bitmap 实例
            if (_bitmapHandle.IsAllocated) _bitmapHandle.Free();
            _bitmapHandle = GCHandle.Alloc(bmp);
            opaque = GCHandle.ToIntPtr(_bitmapHandle);
            _bitmap = bmp;
            return 1; // 分配 1 个 picture buffer
        }
        catch
        {
            return 0;
        }
    }

    private void OnVideoCleanup(ref IntPtr opaque)
    {
        // VLC 线程：播放结束/停止/格式重协商时调用。仅释放 GCHandle；
        // bitmap 的解锁由 Display 每帧负责，这里不跨线程访问避免 InvalidOperationException。
        if (opaque != IntPtr.Zero)
        {
            try
            {
                GCHandle.FromIntPtr(opaque).Free();
            }
            catch (Exception ex) { Log.Error(ex, "VideoPlayerHost 视频清理异常"); }
            opaque = IntPtr.Zero;
        }
        _bitmapHandle = default;
        _bitmap = null;
    }

    /// <summary>VLC 线程：锁定 bitmap 并返回 BackBuffer。bitmap 属于 UI 线程，
    /// 必须切回 UI 线程执行 Lock（Dispatcher.Invoke 同步等待），否则跨线程抛异常。</summary>
    private IntPtr OnVideoLock(IntPtr opaque, IntPtr planes)
    {
        try
        {
            if (opaque == IntPtr.Zero) return IntPtr.Zero;
            var bmp = (WriteableBitmap)GCHandle.FromIntPtr(opaque).Target;
            if (bmp == null) return IntPtr.Zero;
            IntPtr backBuffer = IntPtr.Zero;
            Dispatcher.Invoke(() =>
            {
                bmp.Lock();
                backBuffer = bmp.BackBuffer;
            });
            return backBuffer;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private void OnVideoUnlock(IntPtr opaque, IntPtr picture, IntPtr planes)
    {
        // 在 Display 回调统一解锁（需要 UI 线程 AddDirtyRect）
    }

    /// <summary>VLC 线程：帧就绪。切回 UI 线程刷新画面（AddDirtyRect/Unlock 同属 UI 线程）</summary>
    private void OnVideoDisplay(IntPtr opaque, IntPtr picture)
    {
        try
        {
            if (opaque == IntPtr.Zero) return;
            var bmp = (WriteableBitmap)GCHandle.FromIntPtr(opaque).Target;
            if (bmp == null) return;
            Dispatcher.Invoke(() =>
            {
                bmp.AddDirtyRect(new Int32Rect(0, 0, bmp.PixelWidth, bmp.PixelHeight));
                bmp.Unlock();
            });
        }
        catch (Exception ex) { Log.Error(ex, "VideoPlayerHost 视频显示异常"); }
    }

    #endregion

    private void Host_Unloaded(object sender, RoutedEventArgs e)
    {
        Cleanup();
    }

    public void Cleanup()
    {
        _cursorTimer.Stop();
        SavePosition();
        _mediaPlayer?.Stop();
        _mediaPlayer?.Dispose();
        _mediaPlayer = null;
        if (_bitmapHandle.IsAllocated)
        {
            try { if (_bitmap != null) _bitmap.Unlock(); } catch (Exception ex) { Log.Error(ex, "VideoPlayerHost 操作异常"); }
            _bitmapHandle.Free();
        }
        _bitmapHandle = default;
        _bitmap = null;
        VideoImage.Source = null;
    }

    public void Close()
    {
        Cleanup();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateSeekBar()
    {
        if (_mediaPlayer == null || _isSeeking || _mediaPlayer.Length <= 0) return;

        var pos = _mediaPlayer.Time;
        var len = _mediaPlayer.Length;
        SeekBar.Maximum = len;
        SeekBar.Value = pos;
        TimeLabel.Text = $"{FormatTime(pos)} / {FormatTime(len)}";
    }

    private void SeekBar_DragStarted(object sender, RoutedEventArgs e)
    {
        _isSeeking = true;
    }

    private void SeekBar_DragCompleted(object sender, RoutedEventArgs e)
    {
        if (_mediaPlayer == null) return;
        _mediaPlayer.Time = (long)SeekBar.Value;
        _isSeeking = false;
    }

    private void SeekBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isSeeking && _mediaPlayer != null)
        {
            var seekPos = (long)SeekBar.Value;
            TimeLabel.Text = $"{FormatTime(seekPos)} / {FormatTime(_mediaPlayer.Length)}";
        }
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaPlayer == null) return;
        if (_isPlaying)
            _mediaPlayer.Pause();
        else
            _mediaPlayer.Play();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        SavePositionAndClose();
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_mediaPlayer == null) return;
        _mediaPlayer.Volume = (int)VolumeSlider.Value;

        VolumeIcon.Kind = VolumeSlider.Value == 0 ? MaterialDesignThemes.Wpf.PackIconKind.VolumeOff
            : VolumeSlider.Value < 50 ? MaterialDesignThemes.Wpf.PackIconKind.VolumeMedium
            : MaterialDesignThemes.Wpf.PackIconKind.VolumeHigh;
    }

    private void ResumeYes_Click(object sender, RoutedEventArgs e)
    {
        ResumePanel.Visibility = Visibility.Collapsed;
        if (_mediaPlayer != null && _movie != null && _movie.PlaybackPosition > 0)
        {
            _mediaPlayer.Time = _movie.PlaybackPosition;
        }
        ShowControls();
    }

    private void ResumeNo_Click(object sender, RoutedEventArgs e)
    {
        ResumePanel.Visibility = Visibility.Collapsed;
        if (_movie != null)
        {
            _movie.PlaybackPosition = 0;
            SavePositionToDb(0);
        }
        ShowControls();
    }

    private void ResumePanel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ResumeYes_Click(sender, e);
    }

    private void Host_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleFullscreen();
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        SavePositionAndClose();
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        ToggleFullscreen();
    }

    private void ToggleFullscreen()
    {
        var window = Window.GetWindow(this);
        if (window == null) return;

        if (_isFullscreen)
        {
            window.WindowState = WindowState.Normal;
            window.WindowStyle = WindowStyle.SingleBorderWindow;
            FullscreenIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.Fullscreen;
        }
        else
        {
            window.WindowStyle = WindowStyle.None;
            window.WindowState = WindowState.Maximized;
            FullscreenIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.FullscreenExit;
        }
        _isFullscreen = !_isFullscreen;
    }

    private void Host_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
                PlayPause_Click(sender, e);
                e.Handled = true;
                break;
            case Key.Escape:
                if (_isFullscreen)
                {
                    ToggleFullscreen();
                }
                else
                {
                    SavePositionAndClose();
                }
                e.Handled = true;
                break;
            case Key.Left:
                if (_mediaPlayer != null)
                    _mediaPlayer.Time = Math.Max(0, _mediaPlayer.Time - 5000);
                e.Handled = true;
                break;
            case Key.Right:
                if (_mediaPlayer != null)
                    _mediaPlayer.Time = Math.Min(_mediaPlayer.Length, _mediaPlayer.Time + 5000);
                e.Handled = true;
                break;
            case Key.Up:
                VolumeSlider.Value = Math.Min(100, VolumeSlider.Value + 5);
                e.Handled = true;
                break;
            case Key.Down:
                VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 5);
                e.Handled = true;
                break;
            case Key.F:
                ToggleFullscreen();
                e.Handled = true;
                break;
            case Key.M:
                VolumeSlider.Value = VolumeSlider.Value > 0 ? 0 : 80;
                e.Handled = true;
                break;
        }
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

    private static string FormatTime(long ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms);
        return ts.TotalHours >= 1
            ? ts.ToString(@"hh\:mm\:ss")
            : ts.ToString(@"mm\:ss");
    }
}
