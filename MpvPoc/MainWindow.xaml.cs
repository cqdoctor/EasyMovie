using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MpvPoc;

/// <summary>
/// mpv POC：直接 P/Invoke libmpv，把渲染嵌入到 WinForms Panel 的窗口句柄（wid）。
/// 验证 vo_gpu 渲染无白块、硬件解码(hwdec=auto)流畅、字幕/倍速/截图是否正常。
/// </summary>
public partial class MainWindow : Window
{
    private MpvPlayer? _player;
    private System.Windows.Forms.Panel? _renderPanel;
    private readonly DispatcherTimer _posTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _seeking;
    private double _subDelay;

    public MainWindow()
    {
        InitializeComponent();
        // 把 WinForms Panel 作为 mpv 渲染目标
        _renderPanel = new System.Windows.Forms.Panel { Dock = System.Windows.Forms.DockStyle.Fill };
        Host.Child = _renderPanel;
        Loaded += OnLoaded;
        _posTimer.Tick += PosTimer_Tick;
        Closed += (s, e) => _player?.Dispose();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _player = new MpvPlayer();
            // Panel.Handle 此时已就绪，mpv 会渲染到该窗口
            _player.Initialize(_renderPanel!.Handle);
            StatusLabel.Text = "mpv 已就绪（hwdec=no 软解，确保不出白屏/马赛克）。打开视频开始验证。";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "mpv 初始化失败: " + ex.Message;
        }
    }

    private void Open_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "视频|*.mkv;*.mp4;*.avi;*.mov;*.ts;*.m2ts;*.flv;*.wmv;*.webm"
        };
        if (dlg.ShowDialog() == true && _player != null)
        {
            _player.Load(dlg.FileName);
            _posTimer.Start();
            StatusLabel.Text = "已加载: " + dlg.FileName;
        }
    }

    private void PlayPause_Click(object s, RoutedEventArgs e)
    {
        if (_player == null) return;
        try
        {
            bool paused = _player.GetBool("pause");
            _player.Pause(!paused);
        }
        catch { }
    }

    private void Stop_Click(object s, RoutedEventArgs e)
    {
        try { _player?.Stop(); } catch { }
    }

    private void Rate_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (_player == null || RateCombo.SelectedItem is not ComboBoxItem item) return;
        if (double.TryParse(item.Tag?.ToString(), out var r))
            try { _player.SetSpeed(r); } catch { }
    }

    private void Volume_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_player == null) return;
        try { _player.SetVolume(VolumeSlider.Value); } catch { }
    }

    private void Seek_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_player == null || _seeking) return;
        try { _player.Seek(SeekSlider.Value); } catch { }
    }

    private void PosTimer_Tick(object? sender, EventArgs e)
    {
        if (_player == null) return;
        try
        {
            double dur = _player.GetDouble("duration");
            double pos = _player.GetDouble("time-pos");
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
        if (_player == null) return;
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "MpvPoc");
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, $"shot_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            _player.Screenshot(path);
            StatusLabel.Text = "截图已保存: " + path;
        }
        catch (Exception ex) { StatusLabel.Text = "截图失败: " + ex.Message; }
    }

    private void SubDelay_Click(object s, RoutedEventArgs e)
    {
        if (_player == null) return;
        _subDelay += (s == SubDelayPlus) ? 0.5 : -0.5;
        try { _player.SetSubDelay(_subDelay); } catch { }
        StatusLabel.Text = $"字幕延迟: {_subDelay:0.0}s";
    }
}
