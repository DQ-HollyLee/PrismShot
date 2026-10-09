using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PrismShot.Core.Interop;

/// <summary>Win32 的基础结构。放在一处，别的地方只引用。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Rect32
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public int Width => Right - Left;
    public int Height => Bottom - Top;

    public override string ToString() =>
        $"({Left},{Top})-({Right},{Bottom}) {Width}x{Height}";
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct MonitorInfoEx
{
    public int Size;
    public Rect32 Monitor;
    public Rect32 WorkArea;
    public uint Flags;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string DeviceName;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Point32
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct KeyboardHookStruct
{
    public uint VirtualKeyCode;
    public uint ScanCode;
    public uint Flags;
    public uint Time;
    public IntPtr ExtraInfo;
}

internal delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rectPtr, IntPtr data);

internal delegate bool WindowEnumProc(IntPtr window, IntPtr data);

/// <summary>
/// user32 / dwmapi / shcore 里用得到的那几十个函数。
/// </summary>
/// <remarks>
/// 刻意不用 Vanara / CsWin32 这类大包：本项目总共只需要下面这些，
/// 引一个几千个 API 的程序集去换几十个声明，是把别的项目的体积问题搬到自己身上。
/// </remarks>
internal static partial class Win32
{
    // ---- 虚拟桌面 / 全局参数 ----
    public const int SystemMetricVirtualLeft = 76;
    public const int SystemMetricVirtualTop = 77;
    public const int SystemMetricVirtualWidth = 78;
    public const int SystemMetricVirtualHeight = 79;

    // ---- 窗口样式 ----
    public const uint WindowStyleExToolWindow = 0x00000080;
    public const int GetWindowLongExStyle = -20;
    public const uint DwmExtendedFrameBounds = 9;
    public const uint DwmCloaked = 14;

    // ---- 键 ----
    public const uint VirtualKeySnapshot = 0x2C; // PrintScreen
    public const uint VirtualKeyS = 0x53;
    public const uint VirtualKeyEscape = 0x1B;
    public const uint VirtualKeyLeftWindows = 0x5B;
    public const uint VirtualKeyRightWindows = 0x5C;
    public const uint VirtualKeyShift = 0x10;
    public const uint VirtualKeyMenu = 0x12; // Alt
    public const uint VirtualKeyControl = 0x11;

    // ---- 钩子 ----
    public const int WindowsHookKeyboardLowLevel = 13;
    public const int WindowsMessageKeyDown = 0x0100;
    public const int WindowsMessageKeyUp = 0x0101;
    public const int WindowsMessageSystemKeyDown = 0x0104;
    public const int WindowsMessageSystemKeyUp = 0x0105;

    // ---- SetWindowPos ----
    public static readonly IntPtr WindowHandleTopMost = new(-1);

    public const uint SetWindowPosNoActivate = 0x0010;
    public const uint SetWindowPosNoMove = 0x0002;
    public const uint SetWindowPosNoSize = 0x0001;
    public const uint SetWindowPosShowWindow = 0x0040;
    public const uint SetWindowPosHideWindow = 0x0080;

    // ----  MonitorFromPoint ----
    public const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumDisplayMonitors(
        IntPtr hdc,
        IntPtr clip,
        MonitorEnumProc callback,
        IntPtr data
    );

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(Point32 point, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out Point32 point);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWindowsHookEx(
        int hookId,
        HookProc callback,
        IntPtr module,
        uint threadId
    );

    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(
        IntPtr hook,
        int code,
        IntPtr wParam,
        IntPtr lParam
    );

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    public static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(WindowEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr window, out Rect32 rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(
        IntPtr window,
        StringBuilder text,
        int maxLength
    );

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll")]
    public static extern int SetWindowLongPtr(IntPtr window, int index, int value);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags
    );

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(Point32 point);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    public static extern uint SendInput(
        uint count,
        IntPtr inputs,
        int size
    );

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, IntPtr extraInfo);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(
        IntPtr window,
        uint attribute,
        out Rect32 value,
        int size
    );

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(
        IntPtr window,
        uint attribute,
        out int value,
        int size
    );

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(
        IntPtr monitor,
        MonitorDpiType dpiType,
        out uint dpiX,
        out uint dpiY
    );

    /// <summary>取虚拟桌面的物理像素范围。四块显示器拼起来那张「大桌面」。</summary>
    public static Rect32 GetVirtualScreen()
    {
        return new Rect32
        {
            Left = GetSystemMetrics(SystemMetricVirtualLeft),
            Top = GetSystemMetrics(SystemMetricVirtualTop),
            Right =
                GetSystemMetrics(SystemMetricVirtualLeft)
                + GetSystemMetrics(SystemMetricVirtualWidth),
            Bottom =
                GetSystemMetrics(SystemMetricVirtualTop)
                + GetSystemMetrics(SystemMetricVirtualHeight),
        };
    }
}

internal enum MonitorDpiType
{
    Effective = 0,
    Angular = 1,
    Raw = 2,
}

internal delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
