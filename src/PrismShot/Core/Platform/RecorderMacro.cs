using System;
using System.Diagnostics;

namespace PrismShot.Core.Platform;

/// <summary>
/// 录屏宏：把「按下某个组合键」这件事包装成一个按钮。
/// </summary>
/// <remarks>
/// 各家显卡驱动把全局热键定得各不相同，彼此并不统一：
/// NVIDIA ShadowPlay 是 Alt+F9，AMD 是 Ctrl+Shift+R，Xbox 游戏栏是 Win+Alt+R。
/// 与其去记这堆组合，不如在覆盖层上留一个按钮 —— 反正截图和录屏本来就是同一个上下文里的事。
/// </remarks>
internal static class RecorderMacro
{
    public static bool Fire(AppSettings settings)
    {
        RecorderSettings recorder = settings.Recorder;

        if (!string.IsNullOrWhiteSpace(recorder.LaunchPath))
        {
            return Launch(recorder.LaunchPath);
        }

        HotkeyBinding binding = RecorderPresets.Resolve(recorder);
        if (binding.IsEmpty)
        {
            Log.Warn("录屏宏既没绑组合键也没设启动程序。请在配置文件的 Recorder 节里填一个。");
            return false;
        }

        Log.Info($"发送录屏组合键 {binding}。");
        return InputSimulator.SendChord(binding);
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
}
