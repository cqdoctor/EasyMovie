using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using LibMPVSharp;
using LibMPVSharp.WPF;

namespace MpvPoc;

/// <summary>
/// mpv POC：验证 vo_gpu 渲染无白块、硬件解码(hwdec=auto)流畅、字幕/倍速/截图是否正常。
/// 控制全部走 mpv 命令/属性（LibMPVSharp 高层封装）。
/// </summary>
public partial class MainWindow : Window
{
    private MPVMediaPlayer? _player;
    private readonly DispatcherTimer _posTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _seeking;
    private double _subDelay;

    public MainWindow()
    {
        InitializeComponent();
        // hwdec=auto：让 mpv 自动选用 d3d11va/nvdec 硬件解码（流畅且不掉软解）
        _player = new MPVMediaPlayer(p => p.SetProperty(MPVMediaPlayer.VideoOpts.Hwdec, "auto"));
        VideoView.MediaPlayer = _player;
        _posTimer.Tick += PosTimer_Tick;
        Closed += (s, e) => _player?.Dispose();
    }

    private void Open_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "视频|*.mkv;*.mp4;*.avi;*.mov;*.ts;*.m2ts;*.flv;*.wmv;*.webm"
        };
        if (dlg.ShowDialog() == true)
        {
            _player!.ExecuteCommand("loadfile", dlg.FileName);
            _posTimer.Start();
            StatusLabel.Text = "已加载: " + dlg.FileName;
        }
    }

    private void PlayPause_Click(object s, RoutedEventArgs e)
    {
        if (_player == null) return;
        try
        {
            bool paused = _player.GetPropertyBoolean(MPVMediaPlayer.PlaybackControlOpts.Pause);
            _player.SetProperty(MPVMediaPlayer.PlaybackControlOpts.Pause, !paused);
        }
        catch { }
    }

    private void Stop_Click(object s, RoutedEventArgs e)
    {
        try { _player?.ExecuteCommand("stop"); } catch { }
    }

    private void Rate_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (_player == null || RateCombo.SelectedItem is not ComboBoxItem item) return;
        if (double.TryParse(item.Tag?.ToString(), out var r))
            try { _player.SetProperty(MPVMediaPlayer.PlaybackControlOpts.Speed, r); } catch { }
    }

    private void Volume_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_player == null) return;
        try { _player.SetProperty(MPVMediaPlayer.AudioOpts.Volume, (double)VolumeSlider.Value); } catch { }
    }

    private void Seek_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_player == null || _seeking) return;
        try { _player.SetProperty("time-pos", SeekSlider.Value); } catch { }
    }

    private void PosTimer_Tick(object? sender, EventArgs e)
    {
        if (_player == null) return;
        try
        {
            double dur = _player.GetPropertyDouble(MPVMediaPlayer.Properties.Duration);
            double pos = _player.GetPropertyDouble(MPVMediaPlayer.Properties.TimePos);
            _seeking = true;
            if (dur > 0) SeekSlider.Maximum = dur;
            SeekSlider.Value = pos;
            TimeLabel.Text = $"{TimeSpan.FromSeconds(pos):hh\\:mm\\:ss} / {TimeSpan.FromSeconds(dur):hh\\:mm\\:ss}";
            _seeking = false;
        }
        catch { }
    }

    private void Snapshot_Click(object s, RoutedEventArgs e)
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "MpvPoc");
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, $"shot_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            _player!.ExecuteCommand("screenshot", path);
            StatusLabel.Text = "截图已保存: " + path;
        }
        catch (Exception ex) { StatusLabel.Text = "截图失败: " + ex.Message; }
    }

    private void SubDelay_Click(object s, RoutedEventArgs e)
    {
        if (_player == null) return;
        _subDelay += (s == SubDelayPlus) ? 0.5 : -0.5;
        try { _player.SetProperty("sub-delay", _subDelay); } catch { }
        StatusLabel.Text = $"字幕延迟: {_subDelay:0.0}s";
    }
}
