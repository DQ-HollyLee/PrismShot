using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using PrismShot.Core.Interop;

namespace PrismShot.Core.Capture;

/// <summary>一块物理显示器的快照信息。</summary>
internal sealed class DisplayTarget
{
    /// <summary>Win32 HMONITOR。WGC 用它创建捕获项。</summary>
    public IntPtr MonitorHandle { get; init; }

    public string DeviceName { get; init; } = string.Empty;

    /// <summary>在虚拟桌面坐标系下的物理像素矩形。</summary>
    public Rect32 Bounds { get; init; }

    public bool IsPrimary { get; init; }

    /// <summary>系统 HDR 是否在这块屏上开着。未知时按 false 处理。</summary>
    public bool IsHdrEnabled { get; init; }

    /// <summary>该屏 SDR 白电平（尼特）。Windows 那根「SDR 内容亮度」滑块的当前值。</summary>
    public float SdrWhiteLevelNits { get; init; } = ScreenshotDefaults.ReferenceSdrWhiteNits;

    /// <summary>这块屏的信息是否可靠。不可靠时上层要选更保守的像素格式。</summary>
    public bool IsColorInfoKnown { get; init; } = true;

    /// <summary>DPI 缩放比（dpi / 96）。</summary>
    public double Scale { get; init; } = 1.0;

    public int PixelWidth => Bounds.Width;

    public int PixelHeight => Bounds.Height;

    /// <summary>
    /// 是否应当用浮点 scRGB 去抓这块屏。
    /// </summary>
    /// <remarks>
    /// 逻辑分两层：先看系统给不给得起（Win10 的 WGC 压根出不了 float），
    /// 再看这块屏。查不到信息时，Win11 上宁可按 float 抓 —— 多花一倍回读带宽没关系，
    /// 把 HDR 内容当成 SDR 截出来才是不能犯的错，那正是原生截图工具的毛病。
    /// </remarks>
    public bool ShouldCaptureAsFloat =>
        MonitorEnumerator.SupportsFloatCapture && (IsHdrEnabled || !IsColorInfoKnown);

    public override string ToString() =>
        $"{DeviceName} {Bounds} hdr={IsHdrEnabled} sdrWhite={SdrWhiteLevelNits:0} scale={Scale:0.##} float={ShouldCaptureAsFloat}";
}

internal static class ScreenshotDefaults
{
    /// <summary>scRGB 里 1.0 对应的标准 SDR 峰值亮度。</summary>
    public const float ReferenceSdrWhiteNits = 80f;
}

/// <summary>
/// 枚举显示器，并把「这块屏现在是不是 HDR、SDR 白电平多少」查清楚。
/// </summary>
/// <remarks>
/// HDR 判定必须在抓帧之前做：WGC 的像素格式是创建帧池时写死的，
/// 拿到帧之后再想换格式已经来不及了。所以这里宁可多查一层，也不能猜。
/// </remarks>
internal static class MonitorEnumerator
{
    /// <summary>
    /// Win11（build 22000+）才支持 WGC 直出 float scRGB；Win10 上 WGC 只能给 8bit，
    /// 那时候谈 HDR 没有意义，格式一律写死 B8G8R8A8。
    /// </summary>
    public static bool SupportsFloatCapture => Environment.OSVersion.Version.Build >= 22000;

    public static List<DisplayTarget> Enumerate()
    {
        List<DisplayTarget> targets = new();
        IntPtr primary = GetPrimaryMonitor();

        bool Callback(IntPtr monitor, IntPtr hdc, IntPtr rectPtr, IntPtr data)
        {
            try
            {
                targets.Add(Build(monitor, monitor == primary));
            }
            catch (Exception ex)
            {
                Log.Error($"枚举显示器失败（MONITOR=0x{monitor.ToInt64():X}）", ex);
            }

            return true;
        }

        if (
            !Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero)
        )
        {
            Log.Warn(
                $"EnumDisplayMonitors 返回 false（最后错误 {Marshal.GetLastWin32Error()}），改用主显示器兜底。"
            );
            targets.Add(Build(primary, true));
        }

        if (targets.Count == 0)
        {
            targets.Add(Build(primary, true));
        }

        targets.Sort((a, b) =>
            a.Bounds.Left != b.Bounds.Left
                ? a.Bounds.Left.CompareTo(b.Bounds.Left)
                : a.Bounds.Top.CompareTo(b.Bounds.Top)
        );
        return targets;
    }

    public static Rect32 VirtualScreen => Win32.GetVirtualScreen();

    private static IntPtr GetPrimaryMonitor() =>
        Win32.MonitorFromPoint(new Point32 { X = 0, Y = 0 }, Win32.MonitorDefaultToNearest);

    private static DisplayTarget Build(IntPtr monitor, bool maybePrimary)
    {
        MonitorInfoEx info = new() { Size = Marshal.SizeOf<MonitorInfoEx>() };
        if (!Win32.GetMonitorInfo(monitor, ref info))
        {
            throw new InvalidOperationException("GetMonitorInfo 失败。");
        }

        double scale = 1.0;
        int dpiResult = Win32.GetDpiForMonitor(
            monitor,
            MonitorDpiType.Effective,
            out uint dpiX,
            out _
        );
        if (dpiResult == 0 && dpiX > 0)
        {
            scale = dpiX / 96.0;
        }
        else
        {
            Log.Debug($"GetDpiForMonitor 失败(0x{dpiResult:X8})，按 100% 缩放处理。");
        }

        AdvancedColorProbe probe = DisplayConfig.Probe(info.DeviceName)
            ?? new AdvancedColorProbe(false, false, ScreenshotDefaults.ReferenceSdrWhiteNits, 8, false);

        if (!probe.IsKnown)
        {
            Log.Warn(
                $"{info.DeviceName} 查不到高级色彩信息，按{(SupportsFloatCapture ? "float + 80 尼特" : "8bit")}处理。"
            );
        }

        return new DisplayTarget
        {
            MonitorHandle = monitor,
            DeviceName = info.DeviceName,
            Bounds = info.Monitor,
            IsPrimary = maybePrimary || (info.Flags & 1) != 0,
            IsHdrEnabled = probe.HdrEnabled,
            SdrWhiteLevelNits = probe.SdrWhiteNits,
            IsColorInfoKnown = probe.IsKnown,
            Scale = scale,
        };
    }
}
