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
            VideoView.MediaPlayer = _mediaPlayer;
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

    #region VLC 视频渲染（使用官方 LibVLCSharp.Wpf VideoView 硬件渲染，无需手写帧回调）

    // VideoView 通过 VideoHwndHost(HWND) 由 libVLC 直接渲染，覆盖控件作为 VideoView.Content
    // 经 ForegroundWindow 自动悬浮于画面上方。彻底避免手写 WriteableBitmap 帧缓冲的跨线程/越界崩溃。

    #endregion

    private void Host_Unloaded(object sender, RoutedEventArgs e)
    {
        Cleanup();
    }

    public void Cleanup()
    {
        _cursorTimer.Stop();
        SavePosition();
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
