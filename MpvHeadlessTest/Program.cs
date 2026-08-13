using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace MpvHeadlessTest;

/// <summary>
/// Headless 自测：不依赖 GUI/显示，验证 libmpv 的「解码 + 出帧」链路是否工作。
/// 用 vo=image 把解码帧写成 png；同时轮询 time-pos 确认播放在推进。
/// 关键：headless 无显示器时 hwdec=auto 可能初始化失败，故本自测用 hwdec=no（纯软解）排除 GPU 干扰，
/// 只验证「libmpv 能否把视频解码成有效图像帧」这一核心能力。
/// 运行：dotnet run -- "D:\path\to\sample.mp4"
/// </summary>
class Program
{
    const string Dll = "libmpv-2.dll";

    enum mpv_format
    {
        NONE = 0, STRING = 1, OSD_STRING = 2, FLAG = 3, INT64 = 4, DOUBLE = 5,
        NODE = 6, NODE_ARRAY = 7, NODE_MAP = 8, BYTE_ARRAY = 9
    }

    [DllImport(Dll)] static extern IntPtr mpv_create();
    [DllImport(Dll)] static extern int mpv_initialize(IntPtr ctx);
    [DllImport(Dll)] static extern void mpv_terminate_destroy(IntPtr ctx);
    [DllImport(Dll, CharSet = CharSet.Ansi)] static extern int mpv_set_option_string(IntPtr ctx, string name, string value);
    [DllImport(Dll, CharSet = CharSet.Ansi)] static extern int mpv_set_property_string(IntPtr ctx, string name, string value);
    [DllImport(Dll, CharSet = CharSet.Ansi)] static extern IntPtr mpv_get_property_string(IntPtr ctx, string name);
    [DllImport(Dll)] static extern void mpv_free(IntPtr data);
    [DllImport(Dll, CharSet = CharSet.Ansi)]
    static extern int mpv_command(IntPtr ctx, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string[] args);

    static int Main(string[] args)
    {
        string file = args.Length > 0 ? args[0] : "sample.mp4";
        if (!File.Exists(file)) { Console.WriteLine($"[FAIL] 找不到视频文件: {file}"); return 2; }

        string outDir = Path.Combine(Directory.GetCurrentDirectory(), "out");
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        Directory.CreateDirectory(outDir);

        var ctx = mpv_create();
        if (ctx == IntPtr.Zero) { Console.WriteLine("[FAIL] mpv_create 失败（libmpv-2.dll 未加载？）"); return 3; }

        // vo=image：把解码帧写出为图片，不依赖任何 GUI/显示表面。
        // image-out-dir 用绝对路径，避免 mpv 把图片写到 CWD 根目录（相对路径在此环境下不可靠）。
        mpv_set_option_string(ctx, "vo", "image");
        mpv_set_option_string(ctx, "image-out-dir", Path.GetFullPath(outDir));
        mpv_set_option_string(ctx, "image-format", "jpg");
        mpv_set_option_string(ctx, "frames", "8");
        // headless 无显示器，关掉硬件解码，纯软解，排除 GPU 初始化失败干扰
        mpv_set_option_string(ctx, "hwdec", "no");
        // 记录 mpv 自身日志，便于排查
        mpv_set_option_string(ctx, "log-file", Path.Combine(outDir, "mpv.log"));
        mpv_set_option_string(ctx, "v", "warning");

        int initRc = mpv_initialize(ctx);
        Console.WriteLine($"[init] mpv_initialize rc={initRc} (0=成功)");
        if (initRc != 0) { mpv_terminate_destroy(ctx); return 4; }

        Console.WriteLine($"[load] {file}");
        mpv_command(ctx, new[] { "loadfile", file, null });

        // 轮询 time-pos，确认解码在持续推进
        int advancedCount = 0;
        double lastPos = -1;
        for (int i = 0; i < 16; i++)
        {
            Thread.Sleep(300);
            var tp = mpv_get_property_string(ctx, "time-pos");
            string s = tp == IntPtr.Zero ? "?" : Marshal.PtrToStringAnsi(tp)!;
            if (tp != IntPtr.Zero) mpv_free(tp);
            if (double.TryParse(s, out var p) && p > lastPos) { advancedCount++; lastPos = p; }
            Console.WriteLine($"  t={(i * 0.3):F1}s  time-pos={s}");
        }

        // mpv 可能把图片写到 out/ 或 CWD 根目录，两个位置都检查
        var cwd = Directory.GetCurrentDirectory();
        var pngs = Directory.GetFiles(outDir, "*.jpg").Concat(Directory.GetFiles(outDir, "*.png"))
                         .Concat(Directory.GetFiles(cwd, "000000*.jpg")).Concat(Directory.GetFiles(cwd, "000000*.png"))
                         .ToArray();
        Console.WriteLine($"[result] 输出帧数量={pngs.Length}");
        long totalBytes = 0;
        foreach (var p in pngs)
        {
            var len = new FileInfo(p).Length;
            totalBytes += len;
            Console.WriteLine($"  {Path.GetFileName(p)}  {len} bytes");
        }

        mpv_terminate_destroy(ctx);

        if (pngs.Length > 0 && totalBytes > 1000)
        {
            Console.WriteLine("[CONCLUSION] 解码+出帧 OK：libmpv 能把视频解码成有效图像帧（headless 软解验证通过）。");
            Console.WriteLine("   => 若桌面 POC 打开不播放/白屏，问题在 GUI 渲染层（vo=gpu 嵌入 wid），而非解码链路。");
            return 0;
        }
        else
        {
            Console.WriteLine("[CONCLUSION] 连出帧都失败。详见 out/mpv.log：");
            if (File.Exists(Path.Combine(outDir, "mpv.log")))
                Console.WriteLine(File.ReadAllText(Path.Combine(outDir, "mpv.log")));
            return 1;
        }
    }
}
