using System;
using System.IO;
using System.Text.Json;

namespace EasyMovie.Client.Views
{
    /// <summary>
    /// 视频解码模式：持久化到 AppData，供 LibVLC 初始化时选用。
    /// 默认 Software —— 保留此前“关闭硬件解码消除白块”的修复成果；
    /// 用户可在播放器「更多」面板切换到 Hardware/Auto 以获得 4K/高码率流畅播放
    /// （若画面重新出现白块，切回 Software 即可）。
    /// </summary>
    public static class DecoderSettings
    {
        public enum Mode { Software, Hardware, Auto }

        public static Mode Current { get; private set; } = Mode.Software;

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
            catch { /* 损坏则用默认 Software */ }
        }

        public static void Set(Mode m)
        {
            Current = m;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.WriteAllText(Path, JsonSerializer.Serialize(Current.ToString()));
            }
            catch { }
        }

        /// <summary>生成 LibVLC 启动参数（含软解多线程 / 本地缓存等优化）。</summary>
        public static string[] ToLibVlcOptions()
        {
            var opts = new System.Collections.Generic.List<string> { "--no-video-title-show" };
            switch (Current)
            {
                case Mode.Hardware:
                    // 显式启用硬件解码（dxva2/d3d11va 由 VLC 自选），流畅但部分 GPU 有白块风险
                    opts.Add("--avcodec-hw=any");
                    break;
                case Mode.Auto:
                    // 不强制，交给 VLC 自选（默认即硬件解码）
                    break;
                default: // Software
                    opts.Add("--avcodec-hw=none");
                    // 软件解码多线程，充分利用 CPU 核心缓解卡顿/马赛克
                    opts.Add($"--avcodec-threads={Math.Max(1, Environment.ProcessorCount - 1)}");
                    break;
            }
            // 本地文件缓存，防止偶发卡顿/停滞（对直播/网络流不生效，但无害）
            opts.Add("--file-caching=6000");
            return opts.ToArray();
        }
    }
}
