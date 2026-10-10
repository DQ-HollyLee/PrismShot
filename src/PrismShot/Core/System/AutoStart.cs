using System;
using System.IO;
using Microsoft.Win32;

namespace PrismShot.Core.Platform;

/// <summary>
/// 开机自启。写 HKCU 的 Run 键。
/// </summary>
/// <remarks>
/// 不用计划任务：计划任务能做「延迟启动」和「不用管理员」，但代价是会在任务计划程序里
/// 留一条用户看不懂、也不知道能不能删的条目。Run 键在「任务管理器 → 启动」里可见可控，
/// 用户要关掉它时找得到地方 —— 一个常驻后台的截图工具，让用户能随时看见并关掉它，比省那点启动开销重要。
/// </remarks>
internal static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PrismShot";

    /// <summary>便携模式不碰注册表：拷到 U 盘上还要往人家机器写自启，那是耍流氓。</summary>
    public static bool IsSupported => AppPaths.Current == InstallKind.Installed;

    public static bool IsEnabled()
    {
        if (!IsSupported)
        {
            return false;
        }

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"读取自启项失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>把自启状态调成 <paramref name="enabled"/>。返回是否真的落到了期望状态。</summary>
    public static bool Apply(bool enabled)
    {
        if (!IsSupported)
        {
            Log.Info("便携模式，跳过开机自启设置。");
            return false;
        }

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                key.SetValue(ValueName, CommandLine, RegistryValueKind.String);
                Log.Info($"已写入开机自启：{CommandLine}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Info("已移除开机自启。");
            }

            return IsEnabled() == enabled;
        }
        catch (Exception ex)
        {
            Log.Error("设置开机自启失败。", ex);
            return false;
        }
    }

    /// <summary>移掉自启项。计划任务接管之后要用它把注册表那条让出去，否则开机会起两个实例。</summary>
    public static void Remove()
    {
        if (!IsSupported)
        {
            return;
        }

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(ValueName) is string)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Info("已移除注册表自启项（改由计划任务接管）。");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"移除注册表自启项失败：{ex.Message}");
        }
    }

    /// <summary>注册表里现在写着的自启命令行；没有这一条则为 null。</summary>
    public static string? Current()
    {
        if (!IsSupported)
        {
            return null;
        }

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>启动用的命令行。带引号，用户目录里有空格时也不会散架。</summary>
    public static string CommandLine
    {
        get
        {
            string path = AppPaths.ExecutablePath;
            if (string.IsNullOrEmpty(path))
            {
                path = Path.Combine(AppPaths.AppDirectory, "PrismShot.exe");
            }

            return path.Contains(' ') ? $"\"{path}\"" : path;
        }
    }

    /// <summary>
    /// 校验自启路径是否还指得对。用户把文件夹挪了位置之后，注册表里会留一条死路径，
    /// 表现是「重启之后快捷键就失效了」，而这种问题隔天就没人记得是怎么回事。
    /// </summary>
    public static bool IsStale()
    {
        if (!IsSupported)
        {
            return false;
        }

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            if (key?.GetValue(ValueName) is not string value || value.Length == 0)
            {
                return false;
            }

            string configured = value.Trim().Trim('"');
            return !string.Equals(
                configured,
                AppPaths.ExecutablePath,
                StringComparison.OrdinalIgnoreCase
            );
        }
        catch
        {
            return false;
        }
    }
}
