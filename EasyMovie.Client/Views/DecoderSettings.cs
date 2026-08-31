using System;
using System.IO;
using System.Text.Json;
using Serilog;

namespace EasyMovie.Client.Views
{
    /// <summary>
    /// 视频解码模式：持久化到 AppData，供 LibVLC 初始化时选用。
    ///
    /// VLC 播放出现白块 / 绿块 / 马赛克（局部方块、花屏）是老问题，根因几乎都是
    /// 默认的 DXVA2 硬件解码后端在部分 GPU/驱动上输出损坏帧。这是已知的成熟问题，
    /// 有标准解法，而非 VLC 本身不可用：
    ///   1) 把硬件解码后端从易出问题的 DXVA2 换为更稳的 D3D11VA（--avcodec-hw=d3d11va）；
    ///   2) 把视频输出模块固定为 Direct3D11（--vout=direct3d11），正确清屏、不乱白块；
    ///   3) 个别 GPU 连 D3D11VA 都坏，再退回纯软件解码（--avcodec-hw=none），代价是 4K/高码率可能卡。
    /// 默认 Hardware（d3d11va）——既保留硬件解码的流畅，又避开了 DXVA2 的白块/马赛克。
    /// </summary>
    public static class DecoderSettings
    {
        public enum Mode { Software, Hardware, Auto }

        public static Mode Current { get; private set; } = Mode.Hardware;

        private static readonly string Path = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EasyMovie", "decode.json");

        static DecoderSettings() => Load();

        public static void Load()
        {
            try
            {
                if (File.Exists(Path))
                {
                    var v = JsonSerializer.Deserialize<string>(File.ReadAllText(Path));
                    if (v != null && Enum.TryParse<Mode>(v, true, out var m))
                        Current = m;
                }
            }
            catch (Exception ex)
            {
                // 文件损坏则回退默认 Hardware(d3d11va)。注意：本静态构造首次触发时机在播放前，
                // 若早于 Serilog 初始化，此条会被静默丢弃（Serilog 默认 logger 不输出），不影响功能。
                Log.Warning(ex, "读取解码模式配置失败，回退默认 {Mode}: {Path}", Current, Path);
            }
        }

        public static void Set(Mode m)
        {
            Current = m;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.WriteAllText(Path, JsonSerializer.Serialize(Current.ToString()));
            }
            catch (Exception ex)
            {
                // 存不下 = 用户在设置里切的解码模式下次启动就丢，白块问题会"复发"
                Log.Warning(ex, "保存解码模式配置失败: {Path}", Path);
            }
        }

        /// <summary>生成 LibVLC 启动参数（稳定输出模块 + 合理硬件后端 + 本地缓存）。</summary>
        public static string[] ToLibVlcOptions()
        {
            // 固定为 Direct3D11 输出模块：现代 Windows 上最稳，正确清屏，避免“自动”选到会出白块的 vout。
            var opts = new System.Collections.Generic.List<string>
            {
                "--no-video-title-show",
                "--vout=direct3d11",
            };
            switch (Current)
            {
                case Mode.Hardware:
                    // 显式用 D3D11VA 后端：比 VLC 默认（多为 DXVA2）稳，从源头消除白块/马赛克，同时保留硬件解码流畅。
                    opts.Add("--avcodec-hw=d3d11va");
                    break;
                case Mode.Auto:
                    // 不强制后端，交给 VLC 自选（现代 Windows 多为 d3d11va），但输出模块仍固定 direct3d11。
                    break;
                default: // Software
                    opts.Add("--avcodec-hw=none");
                    // 软件解码多线程，充分利用 CPU 核心缓解卡顿/马赛克
                    opts.Add($"--avcodec-threads={Math.Max(1, Environment.ProcessorCount - 1)}");
                    break;
            }
            // 本地文件缓存，防止偶发卡顿/停滞
            opts.Add("--file-caching=6000");
            return opts.ToArray();
        }
    }
}
