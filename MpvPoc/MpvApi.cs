using System;
using System.Runtime.InteropServices;

namespace MpvPoc;

/// <summary>
/// libmpv (libmpv-2.dll) 的最小 P/Invoke 绑定。
/// 仅覆盖本 POC 需要的接口：创建/初始化/销毁、设置选项与属性、执行命令、读取属性字符串。
/// 完整 API 见 libmpv 官方文档（client.h）。
/// </summary>
internal static class MpvApi
{
    public const string Dll = "libmpv-2.dll";

    public enum mpv_format : int
    {
        MPV_FORMAT_NONE = 0,
        MPV_FORMAT_STRING = 1,
        MPV_FORMAT_OSD_STRING = 2,
        MPV_FORMAT_FLAG = 3,
        MPV_FORMAT_INT64 = 4,
        MPV_FORMAT_DOUBLE = 5,
        MPV_FORMAT_NODE = 6,
        MPV_FORMAT_NODE_ARRAY = 7,
        MPV_FORMAT_NODE_MAP = 8,
        MPV_FORMAT_BYTE_ARRAY = 9
    }

    [DllImport(Dll)]
    public static extern IntPtr mpv_create();

    [DllImport(Dll)]
    public static extern int mpv_initialize(IntPtr ctx);

    [DllImport(Dll)]
    public static extern void mpv_terminate_destroy(IntPtr ctx);

    [DllImport(Dll, CharSet = CharSet.Ansi)]
    public static extern int mpv_set_option_string(IntPtr ctx, string name, string value);

    [DllImport(Dll, CharSet = CharSet.Ansi)]
    public static extern int mpv_set_option(IntPtr ctx, string name, mpv_format format, ref long data);

    [DllImport(Dll, CharSet = CharSet.Ansi)]
    public static extern int mpv_set_property_string(IntPtr ctx, string name, string value);

    [DllImport(Dll, CharSet = CharSet.Ansi)]
    public static extern IntPtr mpv_get_property_string(IntPtr ctx, string name);

    [DllImport(Dll)]
    public static extern void mpv_free(IntPtr data);

    [DllImport(Dll, CharSet = CharSet.Ansi)]
    public static extern int mpv_command(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string[] args);
}
