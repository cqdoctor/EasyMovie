using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using LibVLCSharp.Shared;

// VLC 播放链路自测（无需人工看窗口即可拿到硬证据）
//   用法: VlcHeadlessTest <视频文件> [vmem|vout] [hw|sw|auto]
//     vmem = 用 VLC 内存帧回调抓真实解码像素（验证解码是否正确、是否出白块/花屏）
//     vout = 用 --vout=direct3d11 真实输出模块 + TakeSnapshot（验证输出模块能否加载出画面）
// 产物: out/ 下的 PNG 帧 + vlc 日志，可直接目视核对画面是否正常。
internal static class Program
{
    private static IntPtr _buffer = IntPtr.Zero;
    private static int _bufLen;
    private static uint _w, _h;
    private static long _frames;
    private static readonly object _snapLock = new();
    private static byte[]? _snapshot;
    // VLC 可能在 seek 时重新协商画面尺寸（如 1920x800 -> 1920x802）。旧缓冲绝不能释放：
    // VLC 解码线程可能仍在往里写，释放会直接 AccessViolation 崩进程。留着等进程退出即可。
    private static readonly List<IntPtr> _allBuffers = new();
    // 回调委托必须用字段持有，否则会被 GC 回收，VLC 回调时崩溃
    private static MediaPlayer.LibVLCVideoFormatCb? _fmtCb;
    private static MediaPlayer.LibVLCVideoCleanupCb? _cleanCb;
    private static MediaPlayer.LibVLCVideoLockCb? _lockCb;
    private static MediaPlayer.LibVLCVideoUnlockCb? _unlockCb;
    private static MediaPlayer.LibVLCVideoDisplayCb? _dispCb;

    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("用法: VlcHeadlessTest <视频文件> [vmem|vout] [hw|sw|auto]");
            return 2;
        }

        var file = args[0];
        var mode = args.Length > 1 ? args[1].ToLowerInvariant() : "vmem";
        var hw = args.Length > 2 ? args[2].ToLowerInvariant() : "hw";

        if (!File.Exists(file))
        {
            Console.WriteLine($"[fatal] 文件不存在: {file}");
            return 2;
        }

        var outDir = Path.Combine(AppContext.BaseDirectory, "out");
        Directory.CreateDirectory(outDir);
        var logPath = Path.Combine(outDir, $"vlc-{mode}-{hw}.log");

        Console.WriteLine($"[init] 文件={file}");
        Console.WriteLine($"[init] 大小={new FileInfo(file).Length / 1024.0 / 1024.0:F1} MB");
        Console.WriteLine($"[init] 模式={mode} 解码={hw}");

        Core.Initialize();

        // 与 DecoderSettings.ToLibVlcOptions() 保持一致的参数
        var opts = new List<string> { "--no-video-title-show", "--file-caching=6000" };
        if (mode == "vout")
            opts.Add("--vout=direct3d11");     // 真实输出模块（客户端 VideoView 走这条）
        switch (hw)
        {
            case "hw": opts.Add("--avcodec-hw=d3d11va"); break;
            case "sw":
                opts.Add("--avcodec-hw=none");
                opts.Add($"--avcodec-threads={Math.Max(1, Environment.ProcessorCount - 1)}");
                break;
            default: break;                    // auto: 不强制，交给 VLC 自选
        }
        opts.Add("--verbose=2");
        Console.WriteLine($"[init] LibVLC 参数: {string.Join(" ", opts)}");

        using var log = new StreamWriter(logPath, false) { AutoFlush = true };
        using var libVLC = new LibVLC(opts.ToArray());

        var errors = new List<string>();
        void OnLog(object? _, LogEventArgs e)
        {
            var line = $"[{e.Level}] {e.Module}: {e.Message}";
            lock (log) log.WriteLine(line);
            if (e.Level == LogLevel.Error) errors.Add(line);
        }
        libVLC.Log += OnLog;

        using var media = new Media(libVLC, file, FromType.FromPath);
        using var mp = new MediaPlayer(libVLC);

        if (mode == "vmem")
        {
            _fmtCb = OnFormat; _cleanCb = OnCleanup;
            _lockCb = OnLock; _unlockCb = OnUnlock; _dispCb = OnDisplay;
            mp.SetVideoFormatCallbacks(_fmtCb, _cleanCb);
            mp.SetVideoCallbacks(_lockCb, _unlockCb, _dispCb);
        }

        var playing = new ManualResetEventSlim(false);
        mp.Playing += (_, _) => playing.Set();
        mp.EncounteredError += (_, _) => Console.WriteLine("[event] EncounteredError");

        if (!mp.Play(media))
        {
            Console.WriteLine("[fatal] Play() 返回 false");
            return 1;
        }

        if (!playing.Wait(TimeSpan.FromSeconds(15)))
            Console.WriteLine("[warn] 15s 内未进入 Playing 状态");
        else
            Console.WriteLine($"[play] 已开始播放, 时长={mp.Length / 1000}s");

        Thread.Sleep(2500);

        // 跳到 25% 处（真实观影场景 + 验证 seek 后是否出损坏帧/马赛克）
        Console.WriteLine("[seek] 跳转到 25% 位置");
        mp.Position = 0.25f;
        Thread.Sleep(3500);

        var shots = new List<string>();
        for (int i = 1; i <= 3; i++)
        {
            var shot = Path.Combine(outDir, $"{mode}-{hw}-shot{i}.png");
            if (mode == "vmem")
            {
                if (SaveFromMemory(shot)) shots.Add(shot);
            }
            else
            {
                // TakeSnapshot 由 vout 模块截取，能真实反映输出画面
                if (mp.TakeSnapshot(0, shot, 0, 0))
                {
                    for (int k = 0; k < 40 && !File.Exists(shot); k++) Thread.Sleep(100);
                    if (File.Exists(shot)) shots.Add(shot);
                }
                else Console.WriteLine($"[warn] TakeSnapshot 失败 (#{i})");
            }
            Thread.Sleep(1500);
        }

        var timeAfter = mp.Time;
        var frames = Interlocked.Read(ref _frames);
        Console.WriteLine($"[play] 播放位置={timeAfter / 1000}s, 状态={mp.State}");
        if (mode == "vmem") Console.WriteLine($"[play] 收到解码帧数={frames} ({_w}x{_h})");

        mp.Stop();
        Thread.Sleep(300);
        // 必须先关闭日志写入，才能读取日志做摘要
        log.Flush();
        log.Close();

        Console.WriteLine($"[log] VLC 日志: {logPath}");
        if (errors.Count > 0)
        {
            Console.WriteLine($"[error] VLC 报错 {errors.Count} 条, 前 8 条:");
            foreach (var e in errors.Take(8)) Console.WriteLine("   " + e);
        }
        else Console.WriteLine("[error] VLC 无 error 级日志");

        // 检查参数是否被 VLC 接受（日志写入已关闭，可安全读取）
        var logText = File.ReadAllText(logPath);
        foreach (var kw in new[] { "unknown option", "option .* does not exist", "no suitable vout" })
            if (logText.Contains("unknown option", StringComparison.OrdinalIgnoreCase))
            { Console.WriteLine("[warn] 日志含 'unknown option'，参数可能不被接受"); break; }

        // 输出模块 / 硬件解码实际生效情况
        foreach (var key in new[] { "direct3d11", "d3d11va", "dxva2", "avcodec", "hardware" })
        {
            var hit = logText.Split('\n').FirstOrDefault(l =>
                l.Contains(key, StringComparison.OrdinalIgnoreCase) &&
                (l.Contains("using", StringComparison.OrdinalIgnoreCase) ||
                 l.Contains("Using", StringComparison.Ordinal) ||
                 l.Contains("decoder", StringComparison.OrdinalIgnoreCase)));
            if (hit != null) Console.WriteLine($"[vlc] {hit.Trim()}");
        }

        // 逐张分析截图像素
        Console.WriteLine($"[shots] 成功产出 {shots.Count} 张画面");
        bool anyGood = false;
        foreach (var s in shots)
        {
            var r = Analyze(s);
            Console.WriteLine($"   {Path.GetFileName(s)} {r.W}x{r.H} " +
                              $"白像素={r.WhiteRatio:P1} 黑像素={r.BlackRatio:P1} " +
                              $"平均亮度={r.MeanLuma:F0} 亮度标准差={r.StdLuma:F1} 颜色数={r.Colors}");
            if (r.Verdict == Verdict.Ok) { anyGood = true; Console.WriteLine("      => 正常画面"); }
            else if (r.Verdict == Verdict.White) Console.WriteLine("      => 几乎全白（白屏/白块）");
            else if (r.Verdict == Verdict.Black) Console.WriteLine("      => 几乎全黑（无画面）");
            else Console.WriteLine("      => 画面内容过于单一，可疑");
        }

        Console.WriteLine();
        if (mode == "vmem" && frames == 0)
        {
            Console.WriteLine("[CONCLUSION] 失败：解码链没有产出任何帧。");
            return 1;
        }
        if (shots.Count == 0)
        {
            Console.WriteLine("[CONCLUSION] 失败：拿不到任何画面（输出模块未出图）。");
            return 1;
        }
        if (anyGood)
        {
            Console.WriteLine($"[CONCLUSION] 通过：{mode}/{hw} 能正常解码并输出有效画面（非白屏/非黑屏），seek 后画面正常。");
            return 0;
        }
        Console.WriteLine($"[CONCLUSION] 失败：{mode}/{hw} 出图了但画面异常（白屏/黑屏/内容单一）。");
        return 1;
    }

    #region VLC 内存帧回调

    private static uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height,
                                 ref uint pitches, ref uint lines)
    {
        _w = width; _h = height;
        _bufLen = (int)(width * height * 4);
        // 只新增、不释放旧缓冲（见字段注释）
        var buf = Marshal.AllocHGlobal(_bufLen);
        _allBuffers.Add(buf);
        lock (_snapLock) _snapshot = null;  // 尺寸变了，旧快照作废
        _buffer = buf;
        if (chroma != IntPtr.Zero) Marshal.WriteInt32(chroma, 0x32335652); // "RV32" = BGRA32
        pitches = width * 4;
        lines = height;
        Console.WriteLine($"[format] VLC 协商输出 {width}x{height} RV32");
        return 1;
    }

    private static void OnCleanup(ref IntPtr opaque) { }

    private static IntPtr OnLock(IntPtr opaque, IntPtr planes) => _buffer;

    private static void OnUnlock(IntPtr opaque, IntPtr picture, IntPtr planes) { }

    private static void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        Interlocked.Increment(ref _frames);
        // 定期留存一帧给主线程分析/保存
        if (Interlocked.Read(ref _frames) % 10 == 0 && _buffer != IntPtr.Zero)
        {
            lock (_snapLock)
            {
                var len = _bufLen;
                var src = _buffer;
                if (len <= 0 || src == IntPtr.Zero) return;
                if (_snapshot == null || _snapshot.Length != len) _snapshot = new byte[len];
                Marshal.Copy(src, _snapshot, 0, len);
            }
        }
    }

    private static bool SaveFromMemory(string path)
    {
        byte[]? data;
        uint w, h;
        lock (_snapLock)
        {
            data = _snapshot?.ToArray();
            w = _w; h = _h;
        }
        if (data == null || w == 0 || data.Length != (int)(w * h * 4))
        { Console.WriteLine("[warn] 尚无可保存的解码帧"); return false; }
        using var bmp = new Bitmap((int)w, (int)h, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, (int)w, (int)h);
        var bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < h; y++)
                Marshal.Copy(data, y * (int)w * 4, bd.Scan0 + y * bd.Stride, (int)w * 4);
        }
        finally { bmp.UnlockBits(bd); }
        bmp.Save(path, ImageFormat.Png);
        return true;
    }

    #endregion

    private enum Verdict { Ok, White, Black, Flat }

    private record struct Result(int W, int H, double WhiteRatio, double BlackRatio,
                                 double MeanLuma, double StdLuma, int Colors, Verdict Verdict);

    private static Result Analyze(string path)
    {
        using var bmp = new Bitmap(path);
        int w = bmp.Width, h = bmp.Height;
        int step = Math.Max(1, Math.Min(w, h) / 180);
        long white = 0, black = 0, n = 0;
        double sum = 0, sumSq = 0;
        var colors = new HashSet<int>();
        for (int y = 0; y < h; y += step)
            for (int x = 0; x < w; x += step)
            {
                var c = bmp.GetPixel(x, y);
                n++;
                if (c.R >= 248 && c.G >= 248 && c.B >= 248) white++;
                if (c.R <= 6 && c.G <= 6 && c.B <= 6) black++;
                double luma = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
                sum += luma; sumSq += luma * luma;
                if (colors.Count < 20000) colors.Add(c.ToArgb() & 0xF8F8F8);
            }
        double mean = sum / n;
        double std = Math.Sqrt(Math.Max(0, sumSq / n - mean * mean));
        double wr = (double)white / n, br = (double)black / n;
        var v = wr > 0.90 ? Verdict.White
              : br > 0.97 ? Verdict.Black
              : (colors.Count < 40 || std < 3) ? Verdict.Flat
              : Verdict.Ok;
        return new Result(w, h, wr, br, mean, std, colors.Count, v);
    }
}
