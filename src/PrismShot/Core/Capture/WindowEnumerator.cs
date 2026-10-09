using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using PrismShot.Core.Interop;

namespace PrismShot.Core.Capture;

/// <summary>一个可选作截图对象的顶层窗口。</summary>
internal sealed class WindowCandidate
{
    public IntPtr Handle { get; init; }

    public string Title { get; init; } = string.Empty;

    /// <summary>已扣掉 DWM 阴影的实际边框（虚拟像素）。</summary>
    public Rect32 Bounds { get; init; }

    public bool IsMaximized { get; init; }
}

/// <summary>
/// 找「值得被框选的窗口」。
/// </summary>
/// <remarks>
/// <c>EnumWindows</c> 的原始结果里有几百个句柄：隐藏的、工具提示、输入法候选窗、
/// 各种零尺寸的辅助窗口，还有一个我们自己——直接列给用户是灾难。
/// 这里按 Win32 的惯例层层筛：可见、非最小化、有标题（或至少不是纯工具窗）、
/// 桌面 Coordinates 与虚拟桌面有交集、并且没有被 DWM「隐身」（UWP 最小化后会 cloaked）。
/// </remarks>
internal static class WindowEnumerator
{
    public static List<WindowCandidate> Enumerate(Rect32? visibleArea = null)
    {
        List<WindowCandidate> result = new();
        IntPtr shell = Win32.GetShellWindow();
        IntPtr desktop = Win32.GetDesktopWindow();
        int selfPid = Environment.ProcessId;

        WindowEnumProc callback = (window, _) =>
        {
            if (ShouldSkip(window, shell, desktop, selfPid))
            {
                return true;
            }

            Rect32 bounds = GetRealBounds(window);
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return true;
            }

            if (visibleArea.HasValue && !Intersects(bounds, visibleArea.Value))
            {
                return true;
            }

            // rectified：贴边最大化时 GetWindowRect 会多给出四周几像素的残留边框，
            // 用它做截图框会在边缘留一道背景色。DWM 的 ExtendedFrameBounds 才是真正画出来的那一圈。
            result.Add(
                new WindowCandidate
                {
                    Handle = window,
                    Title = GetTitle(window),
                    Bounds = bounds,
                    IsMaximized = Win32.IsIconic(window) == false && CoversWholeMonitor(bounds),
                }
            );
            return true;
        };

        _ = Win32.EnumWindows(callback, IntPtr.Zero);

        // Z 序从前到后，用户想要的通常是栈顶那个。
        result.Reverse();
        return result;
    }

    private static bool ShouldSkip(IntPtr window, IntPtr shell, IntPtr desktop, int selfPid)
    {
        if (window == shell || window == desktop)
        {
            return true;
        }

        if (!Win32.IsWindowVisible(window))
        {
            return true;
        }

        _ = Win32.GetWindowThreadProcessId(window, out uint pid);
        if (pid == selfPid)
        {
            return true;
        }

        // 隐藏式（AppWindow / UWP 后台）有个坑：IsWindowVisible 对它照样返回 true，
// 只有 DWM 的 cloaked 位说得清它到底有没有画在屏幕上。
        if (IsCloaked(window))
        {
            return true;
        }

        if (IsToolWindow(window))
        {
            return true;
        }

        return false;
    }

    private static bool IsCloaked(IntPtr window)
    {
        int hr = Win32.DwmGetWindowAttribute(
            window,
            Win32.DwmCloaked,
            out int cloaked,
            Marshal.SizeOf<int>()
        );

        // Handle 已失效 / 不是顶层窗口时会返回非 0，那就不算 cloaked，交给后面的尺寸筛除掉。
        return hr == 0 && cloaked != 0;
    }

    private static bool IsToolWindow(IntPtr window)
    {
        IntPtr extended = Win32.GetWindowLongPtr(window, Win32.GetWindowLongExStyle);
        return ((uint)extended.ToInt64() & Win32.WindowStyleExToolWindow) != 0;
    }

    /// <summary>取真实边框：优先 DWM 的扩展边框，拿不到退回 GetWindowRect。</summary>
    public static Rect32 GetRealBounds(IntPtr window)
    {
        int hr = Win32.DwmGetWindowAttribute(
            window,
            Win32.DwmExtendedFrameBounds,
            out Rect32 frame,
            Marshal.SizeOf<Rect32>()
        );

        if (hr == 0 && frame.Width > 0 && frame.Height > 0)
        {
            return frame;
        }

        _ = Win32.GetWindowRect(window, out Rect32 raw);
        return raw;
    }

    private static bool CoversWholeMonitor(Rect32 bounds)
    {
        foreach (DisplayTarget target in MonitorEnumerator.Enumerate())
        {
            if (
                bounds.Left <= target.Bounds.Left
                && bounds.Top <= target.Bounds.Top
                && bounds.Right >= target.Bounds.Right
                && bounds.Bottom >= target.Bounds.Bottom
            )
            {
                return true;
            }
        }

        return false;
    }

    private static bool Intersects(Rect32 a, Rect32 b) =>
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    private static string GetTitle(IntPtr window)
    {
        int length = Win32.GetWindowTextLength(window);
        if (length <= 0)
        {
            return string.Empty;
        }

        StringBuilder builder = new(length + 1);
        return Win32.GetWindowText(window, builder, builder.Capacity) > 0
            ? builder.ToString()
            : string.Empty;
    }
}
