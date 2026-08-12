using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace MpvPoc;

/// <summary>
/// 对 libmpv 的轻封装：把 mpv 嵌入到给定的 Win32 窗口句柄（wid），
/// 通过 C API 的字符串接口控制播放。渲染交给 mpv 自己的 vo=gpu（D3D11），
/// 正确清屏 → 无 VLC 那种信箱白块；hwdec=auto → 硬件解码流畅。
/// </summary>
internal sealed class MpvPlayer : IDisposable
{
    private IntPtr _ctx;
    private readonly CultureInfo _inv = CultureInfo.InvariantCulture;

    /// <summary>把 mpv 嵌入到 hostHandle 指向的窗口（WPF 里用 WindowsFormsHost 内的 Panel.Handle）。</summary>
    public void Initialize(IntPtr hostHandle)
    {
        _ctx = MpvApi.mpv_create();
        if (_ctx == IntPtr.Zero)
            throw new InvalidOperationException("mpv_create 失败：未找到或无法加载 libmpv-2.dll");

        // wid 必须在 initialize 之前作为 option 设置（嵌入目标窗口）
        long hwnd = hostHandle.ToInt64();
        MpvApi.mpv_set_option(_ctx, "wid", MpvApi.mpv_format.MPV_FORMAT_INT64, ref hwnd);
        // vo=gpu 走 D3D11 渲染，正确清屏；hwdec=auto 自动选 d3d11va/nvdec 硬件解码
        MpvApi.mpv_set_option_string(_ctx, "vo", "gpu");
        MpvApi.mpv_set_option_string(_ctx, "hwdec", "auto");
        // 播放结束不自动关闭窗口，方便查看最后一帧
        MpvApi.mpv_set_option_string(_ctx, "keep-open", "yes");

        int r = MpvApi.mpv_initialize(_ctx);
        if (r != 0)
            throw new InvalidOperationException($"mpv_initialize 失败（错误码 {r}）");
    }

    public void Load(string file) => Cmd("loadfile", file);
    public void Stop() => Cmd("stop");
    public void Pause(bool paused) => Set("pause", paused ? "yes" : "no");
    public void SetVolume(double v) => Set("volume", v.ToString(_inv));
    public void SetSpeed(double s) => Set("speed", s.ToString(_inv));
    public void Seek(double sec) => Set("time-pos", sec.ToString(_inv));
    public void SetSubDelay(double d) => Set("sub-delay", d.ToString(_inv));
    public void Screenshot(string path) => Cmd("screenshot", path);

    public double GetDouble(string name)
    {
        var s = Get(name);
        return double.TryParse(s, NumberStyles.Float, _inv, out var v) ? v : 0;
    }

    public bool GetBool(string name) => Get(name) == "yes";

    private string? Get(string name)
    {
        if (_ctx == IntPtr.Zero) return null;
        var ptr = MpvApi.mpv_get_property_string(_ctx, name);
        if (ptr == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringAnsi(ptr); }
        finally { MpvApi.mpv_free(ptr); }
    }

    private void Set(string name, string value)
    {
        if (_ctx == IntPtr.Zero) return;
        MpvApi.mpv_set_property_string(_ctx, name, value);
    }

    private void Cmd(params string[] args)
    {
        if (_ctx == IntPtr.Zero) return;
        // mpv_command 要求 args 以 NULL 结尾
        var arr = new string[args.Length + 1];
        Array.Copy(args, arr, args.Length);
        arr[args.Length] = null!;
        MpvApi.mpv_command(_ctx, arr);
    }

    public void Dispose()
    {
        if (_ctx != IntPtr.Zero)
        {
            MpvApi.mpv_terminate_destroy(_ctx);
            _ctx = IntPtr.Zero;
        }
    }
}
