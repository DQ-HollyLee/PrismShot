using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Win32;

namespace PrismShot.Core.Platform;

/// <summary>一次录屏宏触发之后的结局。</summary>
/// <remarks>
/// 这个类型存在的原因写在 <see cref="RecorderMacro.Fire"/> 上：
/// 宏失效的时候用户看到的是「点了一下，什么也没发生」，
/// 因为宏本身只做一件事 —— 往系统里注入一个合成按键，而按键有没有人接，
/// 发的人是永远不知道的。既然不知道，就必须把「我发了什么、发给谁、可能为什么没反应」
/// 如实交回界面，让用户自己能判断。
/// </remarks>
internal enum MacroLevel
{
    /// <summary>组合键已送出。</summary>
    Sent,

    /// <summary>太密集的重复点击，这一次被拦下来了。</summary>
    Throttled,

    /// <summary>送出去了，但这一台机器上对应的录像功能大概率是关的。</summary>
    Suspicious,

    /// <summary>根本没送出去：没配置，或者发送失败。</summary>
    Failed,
}

internal sealed record MacroOutcome(string Title, string Hint, MacroLevel Level)
{
    public bool Sent => Level == MacroLevel.Sent || Level == MacroLevel.Suspicious;
}

/// <summary>
/// 录屏宏：把「按下某个组合键」这件事包装成一个按钮。
/// </summary>
/// <remarks>
/// <para>
/// 各家显卡驱动把全局热键定得各不相同，彼此并不统一：
/// NVIDIA ShadowPlay 是 Alt+F9，AMD 是 Ctrl+Shift+R，Xbox 游戏栏是 Win+Alt+R。
/// 与其去记这堆组合，不如在覆盖层上留一个按钮 —— 反正截图和录屏本来就是同一个上下文里的事。
/// </para>
/// <para>
/// <b>宏的天性是「不知道有没有成功」</b>：它往系统里注入一串合成按键就完事了，
/// 对方有没有注册这个热键、录屏功能是不是被关了，全都无从得知。
/// 于是失败的形式非常难受 —— 界面上毫无变化，用户只能猜是按钮坏了还是自己点错了。
/// 所以这里能做的只有两件踏实事：<b>说了会发什么</b>（<see cref="Describe"/> 挂在按钮提示上），
/// 以及<b>发了之后把话说回来</b>（<see cref="MacroOutcome"/>），
/// 外加一句「本机这项功能看着是关的」的体检结果。
/// </para>
/// </remarks>
internal static class RecorderMacro
{
    /// <summary>
    /// 两次触发之间的最小间隔。连点会把"开始录制 / 结束录制"来回切，
    /// 而连点恰恰是"点了没反应"时最自然的行为 —— 于是越点越乱。
    /// </summary>
    private const int ThrottleMilliseconds = 1200;

    private static long _lastTicks;

    /// <summary>按钮提示上写的字：这条宏到底会发什么。</summary>
    public static string Describe(AppSettings settings)
    {
        RecorderSettings recorder = settings.Recorder;

        if (!string.IsNullOrWhiteSpace(recorder.LaunchPath))
        {
            return $"启动录像程序：{recorder.LaunchPath}";
        }

        HotkeyBinding binding = RecorderPresets.Resolve(recorder);
        return binding.IsEmpty
            ? "录屏宏还没配置（settings.json 的 Recorder 节）"
            : $"发送录屏组合键 {binding.Text}（{DescribeKind(binding)}）";
    }

    public static MacroOutcome Fire(AppSettings settings)
    {
        RecorderSettings recorder = settings.Recorder;

        if (!string.IsNullOrWhiteSpace(recorder.LaunchPath))
        {
            return Launch(recorder.LaunchPath)
                ? new MacroOutcome(
                    "已启动录像程序",
                    Path.GetFileName(recorder.LaunchPath),
                    MacroLevel.Sent
                )
                : new MacroOutcome(
                    "没能把录像程序拉起来",
                    $"检查一下 Recorder.LaunchPath：{recorder.LaunchPath}",
                    MacroLevel.Failed
                );
        }

        HotkeyBinding binding = RecorderPresets.Resolve(recorder);
        if (binding.IsEmpty)
        {
            string problem = "录屏宏既没绑组合键也没设启动程序。请在配置文件的 Recorder 节里填一个。";
            Log.Warn(problem);
            return new MacroOutcome("录屏宏还没配置", problem, MacroLevel.Failed);
        }

        if (!PassThrottle(binding.Text))
        {
            return new MacroOutcome(
                $"刚发过 {binding.Text}",
                "连点会让录制来回启停，稍等一下再点",
                MacroLevel.Throttled
            );
        }

        Log.Info($"发送录屏组合键 {binding.Text}。");

        if (!InputSimulator.SendChord(binding))
        {
            return new MacroOutcome(
                $"{binding.Text} 没能送进系统",
                "合成按键被拒。以管理员常驻时偶发，重试一次通常就好",
                MacroLevel.Failed
            );
        }

        // 发出去不等于生效。这一步能查的只有「对面的录屏功能开着吗」，
        // 查不到就闭嘴 —— 猜一个错的原因比不说更糟。
        string? suspicion = InspectReachability(binding);
        if (suspicion is not null)
        {
            Log.Warn(suspicion);
            return new MacroOutcome(
                $"已发送 {binding.Text}",
                suspicion,
                MacroLevel.Suspicious
            );
        }

        return new MacroOutcome(
            $"已发送 {binding.Text}",
            DescribeKind(binding),
            MacroLevel.Sent
        );
    }

    /// <summary>
    /// 连点保护。同一条组合键在最小间隔内重复触发只允许一次 ——
    /// 录像这种「按一下开始、再按一下结束」的开关，被无意连点等于什么都没做。
    /// </summary>
    private static bool PassThrottle(string chord)
    {
        long now = Environment.TickCount64;
        long previous = Interlocked.Read(ref _lastTicks);

        if (previous != 0 && now - previous < ThrottleMilliseconds)
        {
            Log.Debug($"录屏宏 {chord} 触发过密，已跳过这一次。");
            return false;
        }

        Interlocked.Exchange(ref _lastTicks, now);
        return true;
    }

    private static bool Launch(string path)
    {
        try
        {
            _ = Process.Start(
                new ProcessStartInfo(path)
                {
                    UseShellExecute = true,
                    WorkingDirectory = AppPaths.AppDirectory,
                }
            );
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"启动录像程序失败：{path}", ex);
            return false;
        }
    }

    private static string DescribeKind(HotkeyBinding binding)
    {
        if (IsXboxChord(binding))
        {
            return "Xbox Game Bar";
        }

        if (
            binding.Modifiers.HasFlag(HotkeyModifiers.Alt)
            && !binding.Modifiers.HasFlag(HotkeyModifiers.Win)
            && binding.Text.StartsWith("Alt+F", StringComparison.Ordinal)
        )
        {
            return "NVIDIA ShadowPlay";
        }

        if (
            binding.Modifiers.HasFlag(HotkeyModifiers.Control)
            && binding.Modifiers.HasFlag(HotkeyModifiers.Shift)
        )
        {
            return "AMD Adrenalin";
        }

        return "录像软件";
    }

    private static bool IsXboxChord(HotkeyBinding binding) =>
        binding.Modifiers.HasFlag(HotkeyModifiers.Win)
        && binding.Modifiers.HasFlag(HotkeyModifiers.Alt)
        && binding.VirtualKey == 0x52; // R

    /// <summary>
    /// 发出去之前能想到的、唯一值得查的一件事：对面的录屏功能是不是被关了。
    /// 只查 Xbox Game Bar —— 它有公开的开关位，查错了会给出错误的安慰或错误的指控；
    /// 显卡驱动那两家没有稳定的开关位，不去猜。
    /// </summary>
    private static string? InspectReachability(HotkeyBinding binding)
    {
        if (!IsXboxChord(binding))
        {
            return null;
        }

        if (!GameBarRecordingDisabled())
        {
            return null;
        }

        return "本机 Xbox Game Bar 的录制是关的，这个键没人接。"
            + "要么在「设置 → 游戏 → Xbox Game Bar」里打开，要么把 settings.json 的 "
            + "Recorder.Preset 改成 nvidia（Alt+F9）或 amd（Ctrl+Shift+R）";
    }

    private static bool GameBarRecordingDisabled()
    {
        if (ReadDword(Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled") == 0)
        {
            return true;
        }

        return ReadDword(
            Registry.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\GameDVR",
            "AppCaptureEnabled"
        ) == 0;
    }

    /// <summary>读不到一律返回 -1：键不存在时应当作「没被显式关掉」，不去冤枉它。</summary>
    private static int ReadDword(RegistryKey root, string path, string name)
    {
        try
        {
            using RegistryKey? key = root.OpenSubKey(path);
            return key?.GetValue(name) is int value ? value : -1;
        }
        catch
        {
            return -1;
        }
    }
}
