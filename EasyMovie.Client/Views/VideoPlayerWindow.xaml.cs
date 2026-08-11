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

namespace EasyMovie.Client.Views;

public partial class VideoPlayerWindow : Window
{
    private static LibVLC? _libVLC;
    private MediaPlayer? _mediaPlayer;
    private readonly Movie _movie;
    private bool _isSeeking;
    private bool _isPlaying;
    private CancellationTokenSource? _hideCts;
    private readonly DispatcherTimer _cursorTimer;
    private Point _lastCursorPos = new(double.NaN, double.NaN);

    // VLC 帧回调渲染到 WriteableBitmap（视频是 WPF Image，控件可直接悬浮其上）
    private WriteableBitmap? _bitmap;
    private uint _videoWidth;
    private uint _videoHeight;
    private bool _frameLocked;

    public VideoPlayerWindow(Movie movie)
    {
        InitializeComponent();
        _movie = movie;

        _cursorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _cursorTimer.Tick += CursorTimer_Tick;

        if (_libVLC == null)
        {
            LibVLCSharp.Shared.Core.Initialize();
            // no-avcodec-corrupted：不显示 seek 到非关键帧时的损坏帧（色块/马赛克）
            _libVLC = new LibVLC("--no-avcodec-corrupted", "--no-video-title-show");
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 无边框窗口最大化时不覆盖任务栏，避免底部控制栏被遮挡
        VideoPlayerHelper.RestrictMaximizeToWorkArea(this);
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
        ControlBar.Visibility = Visibility.Collapsed;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        TitleLabel.Text = _movie.Title;

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
                    SavePosition(0); // 播放完毕，重置位置
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

    #region VLC 帧回调渲染

    /// <summary>配置 VLC 直接输出 BGRA32 (RV32) 帧到 WriteableBitmap，替代 HwndHost 渲染</summary>
    private void SetupVideoCallbacks()
    {
        if (_mediaPlayer == null) return;
        _mediaPlayer.SetVideoFormatCallbacks(OnVideoFormatSetup, OnVideoCleanup);
        _mediaPlayer.SetVideoCallbacks(OnVideoLock, OnVideoUnlock, OnVideoDisplay);
    }

    /// <summary>VLC 线程：格式协商，创建/调整 WriteableBitmap，强制 RV32 输出</summary>
    private uint OnVideoFormatSetup(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        try
        {
            var w = width;
            var h = height;
            Dispatcher.Invoke(() =>
            {
                _videoWidth = w;
                _videoHeight = h;
                if (_bitmap == null || _bitmap.PixelWidth != (int)w || _bitmap.PixelHeight != (int)h)
                {
                    _bitmap = new WriteableBitmap((int)w, (int)h, 96, 96, PixelFormats.Bgra32, null);
                    VideoImage.Source = _bitmap;
                }
            });
            // 4CC "RV32" = BGRA32（与 WriteableBitmap Bgra32 布局一致，零拷贝）
            if (chroma != IntPtr.Zero)
                Marshal.WriteInt32(chroma, 0x32335652);
            pitches = width * 4;
            lines = height;
            return 1; // 分配 1 个 picture buffer
        }
        catch
        {
            return 0;
        }
    }

    private void OnVideoCleanup(ref IntPtr opaque)
    {
        // VLC 线程：播放结束/停止时调用
        if (_frameLocked && _bitmap != null)
        {
            try { _bitmap.Unlock(); } catch { }
            _frameLocked = false;
        }
    }

    /// <summary>VLC 线程：锁定 bitmap 并返回 BackBuffer，VLC 直接写入像素</summary>
    private IntPtr OnVideoLock(IntPtr opaque, IntPtr planes)
    {
        if (_bitmap == null) return IntPtr.Zero;
        try
        {
            _bitmap.Lock();
            _frameLocked = true;
            return _bitmap.BackBuffer;
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

    /// <summary>VLC 线程：帧就绪，切到 UI 线程刷新画面</summary>
    private void OnVideoDisplay(IntPtr opaque, IntPtr picture)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_bitmap != null && _frameLocked)
            {
                _bitmap.AddDirtyRect(new Int32Rect(0, 0, (int)_videoWidth, (int)_videoHeight));
                _bitmap.Unlock();
                _frameLocked = false;
            }
        });
    }

    #endregion

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
        if (_mediaPlayer != null && _movie.PlaybackPosition > 0)
        {
            _mediaPlayer.Time = _movie.PlaybackPosition;
        }
        ShowControls();
    }

    private void ResumeNo_Click(object sender, RoutedEventArgs e)
    {
        ResumePanel.Visibility = Visibility.Collapsed;
        _movie.PlaybackPosition = 0;
        SavePositionToDb(0);
        ShowControls();
    }

    private void ResumePanel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 点击面板空白区域 = 继续播放
        ResumeYes_Click(sender, e);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            // 双击切换全屏/窗口
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
                WindowStyle = WindowStyle.SingleBorderWindow;
            }
            else
            {
                WindowStyle = WindowStyle.None;
                WindowState = WindowState.Maximized;
            }
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
                PlayPause_Click(sender, e);
                e.Handled = true;
                break;
            case Key.Escape:
                if (WindowState == WindowState.Maximized)
                {
                    WindowState = WindowState.Normal;
                    WindowStyle = WindowStyle.SingleBorderWindow;
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
                if (WindowState == WindowState.Maximized)
                {
                    WindowState = WindowState.Normal;
                    WindowStyle = WindowStyle.SingleBorderWindow;
                }
                else
                {
                    WindowStyle = WindowStyle.None;
                    WindowState = WindowState.Maximized;
                }
                e.Handled = true;
                break;
            case Key.M:
                VolumeSlider.Value = VolumeSlider.Value > 0 ? 0 : 80;
                e.Handled = true;
                break;
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _cursorTimer.Stop();
        SavePosition();
        _mediaPlayer?.Stop();
        _mediaPlayer?.Dispose();
        _mediaPlayer = null;
        if (_frameLocked && _bitmap != null)
        {
            try { _bitmap.Unlock(); } catch { }
            _frameLocked = false;
        }
        _bitmap = null;
        VideoImage.Source = null;
    }

    private void SavePositionAndClose()
    {
        SavePosition();
        Close();
    }

    private void SavePosition(long? position = null)
    {
        if (_mediaPlayer == null) return;
        var pos = position ?? _mediaPlayer.Time;

        // 如果距离结尾不到3秒，视为已看完
        if (_mediaPlayer.Length > 0 && pos > _mediaPlayer.Length - 3000)
            pos = 0;

        _movie.PlaybackPosition = pos;
        SavePositionToDb(pos);
    }

    private void SavePositionToDb(long position)
    {
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
        catch { }
    }

    private static string FormatTime(long ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms);
        return ts.TotalHours >= 1
            ? ts.ToString(@"hh\:mm\:ss")
            : ts.ToString(@"mm\:ss");
    }
}
