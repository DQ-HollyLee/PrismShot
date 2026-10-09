using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using PrismShot.Core.Imaging;

namespace PrismShot.Core.Platform;

/// <summary>
/// 直写 Win32 剪贴板。
/// </summary>
/// <remarks>
/// <para>
/// 不用 <c>System.Windows.Clipboard</c>：它要求 STA 线程，而我们的截图是在线程池上跑完的，
/// 为了贴个图再往回切一次 UI 线程，等于把「截图完成」和「UI 线程在忙」耦在一起 ——
/// 覆盖层刚收起来那一下 UI 线程恰恰最忙。
/// </para>
/// <para>
/// 另一个原因是 WPF 的剪贴板 API 在目标进程短暂占用剪贴板时会直接抛异常。
/// 这里退化成「重试几次、失败就跳过」，不打断截图流程 —— 图已经存盘了，
/// 剪贴板没贴上最多是用户再复制一次，不该变成一个报错弹窗。
/// </para>
/// </remarks>
internal static class ClipboardWriter
{
    private const uint CfDib = 8;
    private const uint CfHDrop = 15;
    private const uint GmemMoveable = 0x0002;
    private const uint DibRgbColors = 0;

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
    private struct DropFiles
    {
        public uint Size;
        public int X;
        public int Y;
        public int Wide;
        public int Count;
        /// <summary>后面紧跟着双 null 结尾的宽字符路径列表。</summary>
        public char FirstPath;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr memory);

    /// <summary>把位图放进剪贴板（CF_DIB，32bpp 自上而下）。</summary>
    public static bool TrySetBitmap(Raster raster)
    {
        int headerSize = Marshal.SizeOf<BitmapInfoHeader>();
        int pixelBytes = raster.Stride * raster.Height;
        int totalBytes = headerSize + pixelBytes;

        return WriteClipboard(CfDib, totalBytes, pointer =>
        {
            BitmapInfoHeader header = new()
            {
                Size = headerSize,
                Width = raster.Width,
                // 负高度 = 自上而下，与 Raster 的排布一致，省掉一次翻转。
                Height = -raster.Height,
                Planes = 1,
                BitCount = 32,
                Compression = 0,
                SizeImage = pixelBytes,
            };

            Marshal.StructureToPtr(header, pointer, fDeleteOld: false);
            Marshal.Copy(raster.Pixels, 0, pointer + headerSize, pixelBytes);

            // 有意思的细节：CF_DIB 名义上不含 alpha 通道，32bpp 时约定第 4 字节保留为 0。
            // 但我们给的是 255（不透明）。绝大多数接收方按 32bpp BI_RGB 直接忽略它，
            // 少数（例如部分 Electron 应用）会按 alpha 处理 —— 留 255 比留 0 安全得多。
        });
    }

    /// <summary>把文件路径放进剪贴板（CF_HDROP），可用于「粘贴成文件」。</summary>
    public static bool TrySetFile(string path)
    {
        List<string> paths = new() { path };
        int headerSize = Marshal.SizeOf<DropFiles>() - sizeof(char);
        int characterBytes = (paths.Count + 1) * sizeof(char) * (path.Length + 1) + sizeof(char);
        int totalBytes = headerSize + characterBytes;

        return WriteClipboard(CfHDrop, totalBytes, pointer =>
        {
            DropFiles drop = new()
            {
                Size = (uint)Marshal.SizeOf<DropFiles>(),
                Count = paths.Count,
            };
            Marshal.StructureToPtr(drop, pointer, fDeleteOld: false);

            int offset = headerSize;
            foreach (string item in paths)
            {
                char[] characters = item.ToCharArray();
                Marshal.Copy(characters, 0, pointer + offset, characters.Length);
                offset += characters.Length * sizeof(char);
                Marshal.WriteInt16(pointer + offset, 0);
                offset += sizeof(char);
            }

            // 列表以额外的空字符收尾。
            Marshal.WriteInt16(pointer + offset, 0);
        });
    }

    private static bool WriteClipboard(uint format, int bytes, Action<IntPtr> fill)
    {
        // 剪贴板是全局独占资源，别的进程可能正拿着。重试几次就放弃，不阻塞截图流程。
        for (int attempt = 0; attempt < 6; attempt++)
        {
            IntPtr memory = IntPtr.Zero;
            if (!OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(25 * (attempt + 1));
                continue;
            }

            try
            {
                if (!EmptyClipboard())
                {
                    return false;
                }

                memory = GlobalAlloc(GmemMoveable, (UIntPtr)bytes);
                if (memory == IntPtr.Zero)
                {
                    Log.Warn("GlobalAlloc 失败，剪贴板未写入。");
                    return false;
                }

                IntPtr locked = GlobalLock(memory);
                if (locked == IntPtr.Zero)
                {
                    GlobalFree(memory);
                    return false;
                }

                try
                {
                    fill(locked);
                }
                finally
                {
                    GlobalUnlock(memory);
                }

                if (SetClipboardData(format, memory) == IntPtr.Zero)
                {
                    GlobalFree(memory);
                    Log.Warn("SetClipboardData 失败。");
                    return false;
                }

                // 所有权已经交给系统，这里不能再 GlobalFree。
                memory = IntPtr.Zero;
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"写剪贴板异常：{ex.Message}");
                if (memory != IntPtr.Zero)
                {
                    GlobalFree(memory);
                }

                return false;
            }
            finally
            {
                CloseClipboard();
            }
        }

        Log.Warn("剪贴板一直被占用，这次没贴上。");
        return false;
    }
}
