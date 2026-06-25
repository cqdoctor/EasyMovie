using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    private readonly DispatcherTimer _timer;
    private bool _isSeeking;
    private bool _isPlaying;
    private CancellationTokenSource? _hideCts;

    public VideoPlayerWindow(Movie movie)
    {
        InitializeComponent();
        _movie = movie;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += Timer_Tick;

        if (_libVLC == null)
        {
            LibVLCSharp.Shared.Core.Initialize();
            _libVLC = new LibVLC();
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        TitleLabel.Text = _movie.Title;

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

            VideoView.MediaPlayer = _mediaPlayer;
            _mediaPlayer.Play(media);

            _timer.Start();

            // 检查是否有上次播放位置
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
        if (_mediaPlayer != null && _movie.PlaybackPosition > 0)
        {
            _mediaPlayer.Time = _movie.PlaybackPosition;
        }
        StartAutoHide();
    }

    private void ResumeNo_Click(object sender, RoutedEventArgs e)
    {
        ResumePanel.Visibility = Visibility.Collapsed;
        _movie.PlaybackPosition = 0;
        SavePositionToDb(0);
        StartAutoHide();
    }

    private void ResumePanel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 点击面板空白区域 = 继续播放
        ResumeYes_Click(sender, e);
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        ShowControls();
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

    private void ShowControls()
    {
        if (ResumePanel.Visibility == Visibility.Visible) return;

        ControlBar.Visibility = Visibility.Visible;
        Cursor = Cursors.Arrow;

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
        Cursor = Cursors.None;
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

        ShowControls();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SavePosition();
        _timer.Stop();
        _mediaPlayer?.Stop();
        _mediaPlayer?.Dispose();
        _mediaPlayer = null;
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