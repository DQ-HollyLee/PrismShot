using System;
using System.Runtime.InteropServices;

namespace PrismShot.Core.Platform;

/// <summary>
/// 进程 DPI 感知等级。
/// </summary>
/// <remarks>
/// <para>
/// 这个看似可以交给 app.manifest 的东西，实测靠不住：同一份清单，走 WPF 常驻路径时
/// <c>GetSystemMetrics</c> 给出 2560x1440（真实像素），走纯命令行路径却给出 2048x1152
/// （被系统按 125% 缩放虚拟化过的值）。差 1.25 倍意味着 <c>--capture</c> 会拼出一张缩小 20% 的
/// 虚拟桌面、<c>--probe</c> 会报错尺寸、按坐标裁出来的区域也会整体缩水。
/// </para>
/// <para>
/// 所以这里在 <c>Main</c> 的第一行显式声明一次，三条入口（常驻 / 自测 / 插件调用）行为一致，
/// 不再依赖清单有没有被正确应用。
/// </para>
/// </remarks>
internal static class DpiAwareness
{
    // Windows 10 1703+
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    public static void Enable()
    {
        try
        {
            if (SetProcessDpiAwarenessContext(PerMonitorAwareV2))
            {
                return;
            }
        }
        catch (EntryPointNotFoundException)
        {
            // 太老的系统没有这个导出，往下退。
        }
        catch (DllNotFoundException)
        {
            // 同上。
        }

        try
        {
            // Windows 8.1+：PROCESS_PER_MONITOR_DPI_AWARE = 2
            if (SetProcessDpiAwareness(2) == 0)
            {
                return;
            }
        }
        catch (EntryPointNotFoundException)
        {
        }
        catch (DllNotFoundException)
        {
        }

        try
        {
            _ = SetProcessDPIAware();
        }
        catch (EntryPointNotFoundException)
        {
        }
        catch (DllNotFoundException)
        {
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    [DllImport("shcore.dll", SetLastError = true)]
    private static extern int SetProcessDpiAwareness(int awareness);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDPIAware();
}
