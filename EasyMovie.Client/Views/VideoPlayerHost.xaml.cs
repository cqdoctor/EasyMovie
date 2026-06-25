using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using EasyMovie.Core.Models;
using EasyMovie.Data;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace EasyMovie.Client.Views;

public partial class VideoPlayerHost : UserControl
{
    private static LibVLC? _libVLC;
    private MediaPlayer? _mediaPlayer;
    private Movie? _movie;
    private readonly DispatcherTimer _timer;
    private bool _isSeeking;
    private bool _isPlaying;
    private CancellationTokenSource? _hideCts;
    private bool _isFullscreen;

    public event EventHandler? Closed;

    public VideoPlayerHost()
    {
        InitializeComponent();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += Timer_Tick;

        if (_libVLC == null)
        {
            LibVLCSharp.Shared.Core.Initialize();
            _libVLC = new LibVLC();
        }
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
        // 控件首次加载时若已有电影则开始播放（通常由 LoadMovie 触发）
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

            VideoView.MediaPlayer = _mediaPlayer;
            _mediaPlayer.Play(media);

            _timer.Start();

            if (_movie.PlaybackPosition > 0)
            {
                var ts = TimeSpan.FromMilliseconds(_movie.PlaybackPosition);
                ResumeTimeText.Text = ts.ToString(@"hh\:mm\:ss");
                ResumePanel.Visibility = Visibility.Visible;
            }
            else
            {
                StartAutoHide();
            }
        }
        catch (Exception ex)
        {
            AppMessageBox.ShowError($"播放失败: {ex.Message}");
            Close();
        }
    }

    private void Host_Unloaded(object sender, RoutedEventArgs e)
    {
        Cleanup();
    }

    public void Cleanup()
    {
        SavePosition();
        _timer.Stop();
        _mediaPlayer?.Stop();
        _mediaPlayer?.Dispose();
        _mediaPlayer = null;
        VideoView.MediaPlayer = null;
    }

    public void Close()
    {
        Cleanup();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (_mediaPlayer == null || !_isPlaying) return;
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
        StartAutoHide();
    }

    private void ResumeNo_Click(object sender, RoutedEventArgs e)
    {
        ResumePanel.Visibility = Visibility.Collapsed;
        if (_movie != null)
        {
            _movie.PlaybackPosition = 0;
            SavePositionToDb(0);
        }
        StartAutoHide();
    }

    private void ResumePanel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ResumeYes_Click(sender, e);
    }

    private void Host_MouseMove(object sender, MouseEventArgs e)
    {
        ShowControls();
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

    private void ShowControls()
    {
        if (ResumePanel.Visibility == Visibility.Visible) return;

        ControlBar.Visibility = Visibility.Visible;
        TitleBar.Visibility = Visibility.Visible;

        _hideCts?.Cancel();
        _hideCts = new CancellationTokenSource();
        var token = _hideCts.Token;
        Task.Delay(3000, token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                Dispatcher.BeginInvoke(() => HideControls());
            }
        }, token);
    }

    private void HideControls()
    {
        if (!_isPlaying) return;
        ControlBar.Visibility = Visibility.Collapsed;
        TitleBar.Visibility = Visibility.Collapsed;
    }

    private void StartAutoHide()
    {
        Task.Delay(3000).ContinueWith(t =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_isPlaying)
                    HideControls();
            });
        });
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

        ShowControls();
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
