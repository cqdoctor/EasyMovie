using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using EasyMovie.Core.Models;

namespace EasyMovie.Client.Views;

public static class VideoPlayerHelper
{
    public static void Play(Movie movie)
    {
        if (movie.FilePath == null || !File.Exists(movie.FilePath))
        {
            AppMessageBox.ShowWarning(
                string.Format(LanguageManager.GetString("Msg_FileNotFound"), movie.FilePath ?? ""),
                LanguageManager.GetString("Msg_Hint"));
            return;
        }

        if (Application.Current.MainWindow is MainWindow main)
        {
            main.ShowMoviePlayer(movie);
        }
        else
        {
            // 兜底：主窗口不可用时仍弹窗播放，不影响其他场景
            var player = new VideoPlayerWindow(movie);
            player.Show();
        }
    }

    /// <summary>
    /// 限制无边框/WindowChrome 窗口最大化时只占窗口所在监视器的工作区（不覆盖任务栏）。
    /// 若句柄未就绪则等 SourceInitialized 后注册。调用方建议在 OnSourceInitialized 中调用。
    /// </summary>
    public static void RestrictMaximizeToWorkArea(Window window)
    {
        if (window == null) return;
        if (PresentationSource.FromVisual(window) is HwndSource source)
        {
            source.AddHook(MaximizeWndProc);
            return;
        }
        // 句柄尚未创建，等待 SourceInitialized 再注册，避免 AddHook 静默失败
        window.SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(window) is HwndSource s2)
                s2.AddHook(MaximizeWndProc);
        };
    }

    private static IntPtr MaximizeWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg == WM_GETMINMAXINFO)
        {
            WmGetMinMaxInfo(hwnd, lParam);
            // 保持 false：让消息继续走 WPF 默认机制，保留 Window 的 Min/Max 属性处理
            handled = false;
        }
        return IntPtr.Zero;
    }

    private static void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

        // 取窗口所在监视器（不是主监视器），避免多屏/DPI 下尺寸错误
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return;

        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        var rcWork = info.rcWork;       // 工作区（不含任务栏）
        var rcMonitor = info.rcMonitor; // 整个监视器

        // ptMaxPosition 应为工作区相对监视器原点的偏移
        mmi.ptMaxPosition = new POINT
        {
            X = Math.Abs(rcWork.Left - rcMonitor.Left),
            Y = Math.Abs(rcWork.Top - rcMonitor.Top),
        };
        mmi.ptMaxSize = new POINT
        {
            X = Math.Abs(rcWork.Right - rcWork.Left),
            Y = Math.Abs(rcWork.Bottom - rcWork.Top),
        };
        mmi.ptMaxTrackSize = new POINT { X = mmi.ptMaxSize.X, Y = mmi.ptMaxSize.Y };

        Marshal.StructureToPtr(mmi, lParam, true);
    }

    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}
