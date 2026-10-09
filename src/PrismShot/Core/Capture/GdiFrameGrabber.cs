using System;
using System.Runtime.InteropServices;
using PrismShot.Core.Interop;

namespace PrismShot.Core.Capture;

/// <summary>
/// GDI 抓帧，兜底通道。
/// </summary>
/// <remarks>
/// 这条路出来的一定是被 DWM 降过级的 8bit SDR —— 走在这里就别指望 HDR 正确了。
/// 但远程桌面会话、锁屏、部分虚拟显卡、驱动抽风这些场景下 WGC 是会失败的，
/// 那时宁可给用户一张「平平无奇但能用」的图，也不要弹个报错让他自己对着屏幕干瞪眼。
/// 这个分支的存在感应该越低越好，但它必须存在。
/// </remarks>
internal static class GdiFrameGrabber
{
    private const uint SourceCopy = 0x00CC0020;
    private const uint CaptureBlit = 0x40000000;
    private const uint RasterOperation = SourceCopy | CaptureBlit;
    private const int DibRgbColors = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPixelsPerMeter;
        public int YPixelsPerMeter;
        public int ColorsUsed;
        public int ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public IntPtr Colors;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr handle);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr destination,
        int destinationX,
        int destinationY,
        int width,
        int height,
        IntPtr source,
        int sourceX,
        int sourceY,
        uint rasterOperation
    );

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        IntPtr dc,
        IntPtr bitmap,
        uint startScan,
        uint scanCount,
        IntPtr bits,
        ref BitmapInfo info,
        int usage
    );

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    /// <summary>抓一块虚拟桌面区域的 32bpp 位图。</summary>
    public static PrismShot.Core.Imaging.Raster? TryCapture(Rect32 region)
    {
        int width = region.Width;
        int height = region.Height;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero)
        {
            return null;
        }

        IntPtr memory = CreateCompatibleDC(screen);
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            if (memory == IntPtr.Zero)
            {
                return null;
            }

            bitmap = CreateCompatibleBitmap(screen, width, height);
            if (bitmap == IntPtr.Zero)
            {
                return null;
            }

            IntPtr old = SelectObject(memory, bitmap);
            try
            {
                if (
                    !BitBlt(
                        memory,
                        0,
                        0,
                        width,
                        height,
                        screen,
                        region.Left,
                        region.Top,
                        RasterOperation
                    )
                )
                {
                    Log.Warn("BitBlt 失败。");
                    return null;
                }
            }
            finally
            {
                SelectObject(memory, old);
            }

            Imaging.Raster raster = Imaging.Raster.CreateBgra32(width, height);
            BitmapInfo info = new()
            {
                Header = new BitmapInfoHeader
                {
                    Size = Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = width,
                    // 负高度 = 自上而下的 DIB，正好是 Raster 要的排布，省一次翻转。
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0,
                },
                Colors = IntPtr.Zero,
            };

            GCHandle pinned = GCHandle.Alloc(raster.Pixels, GCHandleType.Pinned);
            try
            {
                int copied = GetDIBits(
                    memory,
                    bitmap,
                    0,
                    (uint)height,
                    pinned.AddrOfPinnedObject(),
                    ref info,
                    DibRgbColors
                );
                if (copied != height)
                {
                    Log.Warn($"GetDIBits 只取回 {copied}/{height} 行。");
                    return null;
                }
            }
            finally
            {
                pinned.Free();
            }

            raster.ForceOpaque();
            return raster;
        }
        catch (Exception ex)
        {
            Log.Error("GDI 抓帧异常。", ex);
            return null;
        }
        finally
        {
            if (bitmap != IntPtr.Zero)
            {
                DeleteObject(bitmap);
            }

            if (memory != IntPtr.Zero)
            {
                DeleteDC(memory);
            }

            ReleaseDC(IntPtr.Zero, screen);
        }
    }
}
