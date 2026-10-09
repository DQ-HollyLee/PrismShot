using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using PrismShot.Core.Capture;

namespace PrismShot.Core.Interop;

/// <summary>
/// DisplayConfig 系列 API。用途只有一个：问清楚某块屏「HDR 开没开」和「SDR 白电平多少尼特」。
/// </summary>
/// <remarks>
/// 为什么不用 WinRT 的 <c>DisplayInformation.CreateForDisplayId</c>：
/// 那条路在 .NET 的 WinRT 投影里根本没有（试到 26100 都没有），它实际上是 WinAppSDK 才有的东西，
/// 用它意味着要把整个 WindowsAppSDK 运行时拖进来 —— 那正是我们想避开的那类依赖。
/// 而 DisplayConfig 是自 Win10 1709 起就有的纯 Win32 接口，远低于我们支持的最低系统版本。
///
/// 两个关键数值的口径（与 Chromium / Kodi 的实现逐字对齐）：
/// <list type="bullet">
/// <item><c>DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO.value</c> bit0 = supported，bit1 = enabled。</item>
/// <item><c>SDRWhiteLevel</c> = (尼特 / 80) * 1000，即 1000 代表 80 尼特。</item>
/// </list>
/// </remarks>
internal static class DisplayConfig
{
    public const uint QueryOnlyActivePaths = 0x00000002;

    public const uint DeviceInfoGetSourceName = 1;
    public const uint DeviceInfoGetTargetName = 2;
    public const uint DeviceInfoGetAdvancedColorInfo = 9;
    public const uint DeviceInfoGetSdrWhiteLevel = 11;

    public const int ErrorSuccess = 0;
    public const int ErrorInsufficientBuffer = 122;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceInfoHeader
    {
        public uint Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIndex;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIndex;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public Rational RefreshRate;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathInfo
    {
        public PathSourceInfo SourceInfo;
        public PathTargetInfo TargetInfo;
        public uint Flags;
    }

    /// <summary>
    /// DISPLAYCONFIG_MODE_INFO 的联合体部分。我们完全用不到里面的内容，
    /// 只需要它在数组里的步长正确，所以直接按最大成员（VIDEO_SIGNAL_INFO，48 字节）定死长度。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 48)]
    internal struct ModeInfoUnion { }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ModeInfo
    {
        public uint InfoType;
        public uint Id;
        public Luid AdapterId;
        public ModeInfoUnion Mode;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SourceDeviceName
    {
        public DeviceInfoHeader Header;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GetAdvancedColorInfo
    {
        public DeviceInfoHeader Header;
        public uint Value;
        public uint ColorEncoding;
        public uint BitsPerColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SdrWhiteLevel
    {
        public DeviceInfoHeader Header;
        public uint Level;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags,
        out uint pathCount,
        out uint modeCount
    );

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint pathCount,
        [In, Out] PathInfo[] pathInfoArray,
        ref uint modeCount,
        [In, Out] ModeInfo[] modeInfoArray,
        IntPtr currentTopologyId
    );

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetSourceDeviceInfo(ref SourceDeviceName packet);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetAdvancedColorInfoImpl(ref GetAdvancedColorInfo packet);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetSdrWhiteLevelImpl(ref SdrWhiteLevel packet);

    /// <summary>
    /// 按 GDI 设备名（形如 <c>\\.\DISPLAY1</c>，与 MONITORINFOEX.szDevice 同形）查这块屏的色彩信息。
    /// </summary>
    /// <returns>查不到时返回 null。调用方负责决定这时该当 SDR 还是「未知」。</returns>
    public static AdvancedColorProbe? Probe(string gdiDeviceName)
    {
        if (string.IsNullOrEmpty(gdiDeviceName))
        {
            return null;
        }

        foreach (PathInfo path in EnumerateActivePaths())
        {
            SourceDeviceName source = new()
            {
                Header = new DeviceInfoHeader
                {
                    Type = DeviceInfoGetSourceName,
                    Size = (uint)Marshal.SizeOf<SourceDeviceName>(),
                    AdapterId = path.SourceInfo.AdapterId,
                    Id = path.SourceInfo.Id,
                },
                ViewGdiDeviceName = string.Empty,
            };

            if (GetSourceDeviceInfo(ref source) != ErrorSuccess)
            {
                continue;
            }

            if (
                !string.Equals(
                    source.ViewGdiDeviceName,
                    gdiDeviceName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                continue;
            }

            bool supported = false;
            bool enabled = false;
            int bitsPerColor = 8;

            GetAdvancedColorInfo colorInfo = new()
            {
                Header = new DeviceInfoHeader
                {
                    Type = DeviceInfoGetAdvancedColorInfo,
                    Size = (uint)Marshal.SizeOf<GetAdvancedColorInfo>(),
                    AdapterId = path.TargetInfo.AdapterId,
                    Id = path.TargetInfo.Id,
                },
            };
            if (GetAdvancedColorInfoImpl(ref colorInfo) == ErrorSuccess)
            {
                supported = (colorInfo.Value & 0x1) != 0;
                enabled = (colorInfo.Value & 0x2) != 0;
                if (colorInfo.BitsPerColor > 0)
                {
                    bitsPerColor = (int)colorInfo.BitsPerColor;
                }
            }

            float whiteNits = ScreenshotDefaults.ReferenceSdrWhiteNits;
            SdrWhiteLevel white = new()
            {
                Header = new DeviceInfoHeader
                {
                    Type = DeviceInfoGetSdrWhiteLevel,
                    Size = (uint)Marshal.SizeOf<SdrWhiteLevel>(),
                    AdapterId = path.TargetInfo.AdapterId,
                    Id = path.TargetInfo.Id,
                },
            };
            if (GetSdrWhiteLevelImpl(ref white) == ErrorSuccess && white.Level > 0)
            {
                // 微软文档口径：1000 表示 80 尼特。
                whiteNits = white.Level / 1000f * ScreenshotDefaults.ReferenceSdrWhiteNits;
            }

            return new AdvancedColorProbe(supported, enabled, whiteNits, bitsPerColor, true);
        }

        return null;
    }

    private static List<PathInfo> EnumerateActivePaths()
    {
        List<PathInfo> result = new();
        if (
            GetDisplayConfigBufferSizes(QueryOnlyActivePaths, out uint pathCount, out uint modeCount)
            != ErrorSuccess
        )
        {
            return result;
        }

        if (pathCount == 0)
        {
            return result;
        }

        PathInfo[] paths = new PathInfo[pathCount];
        ModeInfo[] modes = new ModeInfo[modeCount];

        int code = QueryDisplayConfig(
            QueryOnlyActivePaths,
            ref pathCount,
            paths,
            ref modeCount,
            modes,
            IntPtr.Zero
        );
        if (code != ErrorSuccess)
        {
            return result;
        }

        // 成功调用后 count 会被改写成实际写入的条数。
        uint written = Math.Min(pathCount, (uint)paths.Length);
        for (uint i = 0; i < written; i++)
        {
            result.Add(paths[i]);
        }

        return result;
    }
}

/// <summary>一块屏的高级色彩信息。</summary>
/// <param name="SupportsHdr">显卡/OS 报告这块屏 HDR 可用。</param>
/// <param name="HdrEnabled">当前真的开着 HDR。</param>
/// <param name="SdrWhiteNits">SDR 白电平（尼特）。SDR 模式下系统恒返回 80。</param>
/// <param name="BitsPerColor">每通道位深。</param>
internal readonly record struct AdvancedColorProbe(
    bool SupportsHdr,
    bool HdrEnabled,
    float SdrWhiteNits,
    int BitsPerColor,
    bool IsKnown
);
