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
    private const uint CfDibV5 = 17;
    private const uint CfHDrop = 15;
    private const uint GmemMoveable = 0x0002;

    /// <summary>BI_BITFIELDS：V5 头里那四个通道掩码有效。</summary>
    private const int BiBitFields = 3;

    /// <summary>LCS_sRGB。写了它，V5 头里的端点与伽马就被忽略，省得填一堆默认值。</summary>
    private const int LcsSrgb = 0x73524742;

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

    /// <summary>
    /// BITMAPV5HEADER。比 V1 多出来的正是 alpha 掩码 ——
    /// 剪贴板上的透明通道全靠它，CF_DIB 那份头里根本没有这个字段。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapV5Header
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
        public int ClrUsed;
        public int ClrImportant;
        public int RedMask;
        public int GreenMask;
        public int BlueMask;
        public int AlphaMask;
        public int CsType;

        // 下面这一串是 CIEXYZTRIPLE + 伽马 + 意图 + ICC 轮廓，共 9 + 3 + 4 个 int。
        // 声明成 16 个 int 只为凑齐 124 字节；CsType 写了 LCS_sRGB 之后它们全部被忽略。
        public int Endpoint0;
        public int Endpoint1;
        public int Endpoint2;
        public int Endpoint3;
        public int Endpoint4;
        public int Endpoint5;
        public int Endpoint6;
        public int Endpoint7;
        public int Endpoint8;
        public int GammaRed;
        public int GammaGreen;
        public int GammaBlue;
        public int Intent;
        public int ProfileData;
        public int ProfileSize;
        public int Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DropFiles
    {
        public uint Size;
        public int X;
        public int Y;
        public int Wide;
        public uint Count;
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string name);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr memory);

    /// <summary>一次性要贴上去的若干种格式。顺序即投放顺序，见 <see cref="TrySetBitmap"/>。</summary>
    private readonly record struct Payload(uint Format, int Bytes, Action<IntPtr> Fill);

    /// <summary>
    /// 把位图放进剪贴板。
    /// </summary>
    /// <param name="raster">要贴的图。像素是 BGRA，自上而下。</param>
    /// <param name="hasAlpha">这张图是否真的带透明区域（圈选）。</param>
    /// <remarks>
    /// <para>
    /// 不带透明时只投一份 <c>CF_DIB</c>，与老行为完全一致，零额外开销 ——
    /// 绝大多数截图走的就是这条路。
    /// </para>
    /// <para>
    /// 带透明时必须多投几种，因为 <c>CF_DIB</c> 这个 1990 年代的格式**根本没有 alpha 概念**：
    /// 32bpp 时第 4 字节约定保留为 0，接收方一律忽略。圈选存出来的 PNG 是对的、
    /// 贴出来却还是一整个外接矩形，根子就在这里 —— 遮罩没做错，是剪贴板格式装不下它。
    /// 于是按「接收方越现代、越靠前」的顺序投三份：
    /// <list type="number">
    /// <item><c>PNG</c>（注册格式）：字节级无损，透明通道原样保留。Office、Chrome、
    /// VS Code 之类优先吃它。</item>
    /// <item><c>CF_DIBV5</c>：V5 头里带 <c>bV5AlphaMask</c>，是标准剪贴板格式里唯一
    /// 正式支持 alpha 的一种。Word / PowerPoint 从它也能取出透明。</item>
    /// <item><c>CF_DIB</c>：给只认老格式的程序兜底。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 兜底那份刻意**合成到白底**而不是原样贴：老格式拿不到形状已成定局，
    /// 但如果原样贴，用户明明圈走了一块不想给人看的内容，粘贴出去却变成了完整矩形 ——
    /// 那是泄漏，不是降级。合成到白底至少形状外面是干净的。
    /// </para>
    /// </remarks>
    public static bool TrySetBitmap(Raster raster, bool hasAlpha)
    {
        if (!hasAlpha)
        {
            return SetSingle(new Payload(CfDib, DibBytes(raster), target => FillDib(target, raster, raster.Pixels)));
        }

        List<Payload> payloads = new(3);

        uint pngFormat = RegisterClipboardFormat("PNG");
        if (pngFormat != 0)
        {
            byte[]? png = TryEncodePng(raster);
            if (png is { Length: > 0 })
            {
                payloads.Add(new Payload(pngFormat, png.Length, target => Marshal.Copy(png, 0, target, png.Length)));
            }
        }

        payloads.Add(
            new Payload(
                CfDibV5,
                V5Bytes(raster),
                target => FillDibV5(target, raster, raster.Pixels)
            )
        );

        byte[] flattened = FlattenOverWhite(raster);
        payloads.Add(new Payload(CfDib, DibBytes(raster), target => FillDib(target, raster, flattened)));

        return SetMany(payloads);
    }

    /// <summary>把文件路径放进剪贴板（CF_HDROP），可用于「粘贴成文件」。</summary>
    public static bool TrySetFile(string path)
    {
        List<string> paths = new() { path };
        int headerSize = Marshal.SizeOf<DropFiles>() - sizeof(char);
        int characterBytes = (paths.Count + 1) * sizeof(char) * (path.Length + 1) + sizeof(char);
        int totalBytes = headerSize + characterBytes;

        return SetSingle(
            new Payload(
                CfHDrop,
                totalBytes,
                pointer =>
                {
                    DropFiles drop = new()
                    {
                        Size = (uint)Marshal.SizeOf<DropFiles>(),
                        Count = 1,
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
                }
            )
        );
    }

    // ---------------- 头 + 像素 ----------------

    private static int DibBytes(Raster raster) =>
        Marshal.SizeOf<BitmapInfoHeader>() + (raster.Stride * raster.Height);

    private static int V5Bytes(Raster raster) =>
        Marshal.SizeOf<BitmapV5Header>() + (raster.Stride * raster.Height);

    /// <summary>CF_DIB。32bpp BI_RGB —— 注意这份头里没有 alpha 掩码，透明信息在这里必然丢失。</summary>
    private static void FillDib(IntPtr target, Raster raster, byte[] pixels)
    {
        BitmapInfoHeader header = new()
        {
            Size = Marshal.SizeOf<BitmapInfoHeader>(),
            Width = raster.Width,
            // 负高度 = 自上而下，与 Raster 的排布一致，省掉一次翻转。
            Height = -raster.Height,
            Planes = 1,
            BitCount = 32,
            Compression = 0,
            SizeImage = raster.Stride * raster.Height,
        };

        Marshal.StructureToPtr(header, target, fDeleteOld: false);
        Marshal.Copy(pixels, 0, target + header.Size, raster.Stride * raster.Height);
    }

    /// <summary>
    /// CF_DIBV5。四个掩码按内存里的 BGRA 排布写：小端下 32 位值是 0xAARRGGBB。
    /// </summary>
    private static void FillDibV5(IntPtr target, Raster raster, byte[] pixels)
    {
        BitmapV5Header header = new()
        {
            Size = Marshal.SizeOf<BitmapV5Header>(),
            Width = raster.Width,
            Height = -raster.Height,
            Planes = 1,
            BitCount = 32,
            Compression = BiBitFields,
            SizeImage = raster.Stride * raster.Height,
            RedMask = 0x00FF0000,
            GreenMask = 0x0000FF00,
            BlueMask = 0x000000FF,
            // 0xFF000000 超出 int 正区间，必须显式转换。掩码只是位样式，符号无关紧要。
            AlphaMask = unchecked((int)0xFF000000),
            CsType = LcsSrgb,
        };

        Marshal.StructureToPtr(header, target, fDeleteOld: false);
        Marshal.Copy(pixels, 0, target + header.Size, raster.Stride * raster.Height);
    }

    /// <summary>把 alpha 合成到白底，得到一份完全不透明的副本，供老格式兜底用。</summary>
    private static byte[] FlattenOverWhite(Raster raster)
    {
        byte[] source = raster.Pixels;
        byte[] result = new byte[source.Length];

        for (int offset = 0; offset + 3 < source.Length; offset += 4)
        {
            int alpha = source[offset + 3];
            if (alpha >= 255)
            {
                result[offset] = source[offset];
                result[offset + 1] = source[offset + 1];
                result[offset + 2] = source[offset + 2];
            }
            else if (alpha <= 0)
            {
                result[offset] = 255;
                result[offset + 1] = 255;
                result[offset + 2] = 255;
            }
            else
            {
                int inverse = 255 - alpha;
                result[offset] = (byte)(((source[offset] * alpha) + (255 * inverse)) / 255);
                result[offset + 1] = (byte)(((source[offset + 1] * alpha) + (255 * inverse)) / 255);
                result[offset + 2] = (byte)(((source[offset + 2] * alpha) + (255 * inverse)) / 255);
            }

            result[offset + 3] = 255;
        }

        return result;
    }

    private static byte[]? TryEncodePng(Raster raster)
    {
        try
        {
            return ImageWriter.ToPngBytes(raster);
        }
        catch (Exception ex)
        {
            // PNG 只是三份里最优先的那一份，编不出来还有 V5 顶着，不该连累整次截图。
            Log.Warn($"剪贴板 PNG 编码失败，退化为仅 DIBV5：{ex.Message}");
            return null;
        }
    }

    // ---------------- 剪贴板会话 ----------------

    private static bool SetSingle(Payload payload)
    {
        return WithClipboard(() =>
        {
            IntPtr memory = AllocAndFill(payload);
            if (memory == IntPtr.Zero)
            {
                return false;
            }

            if (SetClipboardData(payload.Format, memory) == IntPtr.Zero)
            {
                GlobalFree(memory);
                Log.Warn("SetClipboardData 失败。");
                return false;
            }

            // 所有权已经交给系统，这里不能再 GlobalFree。
            return true;
        });
    }

    /// <summary>
    /// 一次打开、投放多种格式。
    /// </summary>
    /// <remarks>
    /// 必须是一次会话里投完：每开一次剪贴板都要 <c>EmptyClipboard</c>，
    /// 分三次投的话前两次会被自己清掉，最后只剩一种 —— 正是要避免的结果。
    /// </remarks>
    private static bool SetMany(List<Payload> payloads)
    {
        return WithClipboard(() =>
        {
            bool any = false;
            foreach (Payload payload in payloads)
            {
                IntPtr memory = AllocAndFill(payload);
                if (memory == IntPtr.Zero)
                {
                    continue;
                }

                if (SetClipboardData(payload.Format, memory) == IntPtr.Zero)
                {
                    GlobalFree(memory);
                    Log.Warn($"SetClipboardData 失败（格式 {payload.Format}）。");
                    continue;
                }

                // 所有权已经交给系统。
                any = true;
            }

            return any;
        });
    }

    private static IntPtr AllocAndFill(Payload payload)
    {
        IntPtr memory = GlobalAlloc(GmemMoveable, (UIntPtr)payload.Bytes);
        if (memory == IntPtr.Zero)
        {
            Log.Warn("GlobalAlloc 失败，剪贴板未写入。");
            return IntPtr.Zero;
        }

        IntPtr locked = GlobalLock(memory);
        if (locked == IntPtr.Zero)
        {
            GlobalFree(memory);
            return IntPtr.Zero;
        }

        try
        {
            payload.Fill(locked);
        }
        finally
        {
            GlobalUnlock(memory);
        }

        return memory;
    }

    /// <summary>
    /// 剪贴板是全局独占资源，别的进程可能正拿着。重试几次就放弃，不阻塞截图流程。
    /// </summary>
    private static bool WithClipboard(Func<bool> body)
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
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

                return body();
            }
            catch (Exception ex)
            {
                Log.Warn($"写剪贴板异常：{ex.Message}");
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
