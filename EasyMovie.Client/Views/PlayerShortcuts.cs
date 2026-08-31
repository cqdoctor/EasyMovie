using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using Serilog;

namespace EasyMovie.Client.Views;

/// <summary>
/// 播放器快捷键：内置默认绑定，可持久化到 AppData/EasyMovie/shortcuts.json 并由用户重绑。
/// 结构性快捷键（Esc 退出/全屏、F 全屏、←→ 快退快进）固定不变，不在此表内。
/// </summary>
public static class PlayerShortcuts
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EasyMovie", "shortcuts.json");

    public enum PlayerAction
    {
        TogglePlay, Snapshot, CycleRate, SubtitleDelayMinus, SubtitleDelayPlus,
        VolumeUp, VolumeDown, Mute, StepFrame, Info, Picture, AbA, AbB, AbClear, AspectCycle
    }

    private static readonly Dictionary<PlayerAction, Key> Defaults = new()
    {
        [PlayerAction.TogglePlay] = Key.Space,
        [PlayerAction.Snapshot] = Key.S,
        [PlayerAction.CycleRate] = Key.C,
        [PlayerAction.SubtitleDelayMinus] = Key.OemComma,
        [PlayerAction.SubtitleDelayPlus] = Key.OemPeriod,
        [PlayerAction.VolumeUp] = Key.Up,
        [PlayerAction.VolumeDown] = Key.Down,
        [PlayerAction.Mute] = Key.M,
        [PlayerAction.StepFrame] = Key.E,
        [PlayerAction.Info] = Key.I,
        [PlayerAction.Picture] = Key.P,
        [PlayerAction.AbA] = Key.B,
        [PlayerAction.AbB] = Key.N,
        [PlayerAction.AbClear] = Key.R,
        [PlayerAction.AspectCycle] = Key.A,
    };

    public static Dictionary<PlayerAction, Key> Current { get; private set; } = new(Defaults);

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<Dictionary<PlayerAction, Key>>(json);
                if (loaded != null)
                {
                    Current = new Dictionary<PlayerAction, Key>(Defaults);
                    foreach (var kv in loaded) Current[kv.Key] = kv.Value;
                }
            }
        }
        catch (Exception ex)
        {
            // 读不出来就全量回退默认键位，用户会以为"我改的快捷键丢了"，必须留痕
            Log.Warning(ex, "读取快捷键配置失败，回退默认键位: {Path}", FilePath);
        }
    }

    public static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            // 存不下 = 改完的键位下次启动就丢，属用户可见的数据丢失
            Log.Warning(ex, "保存快捷键配置失败: {Path}", FilePath);
        }
    }

    public static Dictionary<Key, PlayerAction> BuildKeyMap()
    {
        var m = new Dictionary<Key, PlayerAction>();
        foreach (var kv in Current) m[kv.Value] = kv.Key;
        return m;
    }

    public static string ActionLabel(PlayerAction a) => a switch
    {
        PlayerAction.TogglePlay => "播放/暂停",
        PlayerAction.Snapshot => "截图",
        PlayerAction.CycleRate => "倍速",
        PlayerAction.SubtitleDelayMinus => "字幕延迟 -",
        PlayerAction.SubtitleDelayPlus => "字幕延迟 +",
        PlayerAction.VolumeUp => "音量 +",
        PlayerAction.VolumeDown => "音量 -",
        PlayerAction.Mute => "静音",
        PlayerAction.StepFrame => "逐帧",
        PlayerAction.Info => "编码信息",
        PlayerAction.Picture => "画面增强",
        PlayerAction.AbA => "设 A 点",
        PlayerAction.AbB => "设 B 点",
        PlayerAction.AbClear => "清除 AB",
        PlayerAction.AspectCycle => "画面比例",
        _ => a.ToString()
    };

    public static string KeyLabel(Key k) => k switch
    {
        Key.Space => "空格",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.Up => "↑",
        Key.Down => "↓",
        Key.Left => "←",
        Key.Right => "→",
        Key.Escape => "Esc",
        _ => k.ToString()
    };
}
