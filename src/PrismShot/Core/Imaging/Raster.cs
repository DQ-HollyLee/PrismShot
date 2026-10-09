using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PrismShot.Core.Imaging;

/// <summary>
/// 一张 CPU 内存里的 8bit 位图（BGRA32，自上而下）。
/// </summary>
/// <remarks>
/// 刻意做成最朴素的字节数组，而不是直接拿 <c>BitmapSource</c> 当中间格式：
/// 冻结帧要在抓帧、裁剪、显示之间来回倒腾，中间表示越接近原始字节越好。
/// 只有最终要交给 UI 或编码器时，才付那一次 <see cref="ToBitmapSource"/> 的内存拷贝。
/// </remarks>
internal sealed class Raster
{
    private Raster(int width, int height, int stride, byte[] pixels)
    {
        Width = width;
        Height = height;
        Stride = stride;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>每行字节数，已按 4 字节对齐。</summary>
    public int Stride { get; }

    /// <summary>BGRA32 像素数据，行序自上而下。</summary>
    public byte[] Pixels { get; }

    public long ByteCount => (long)Stride * Height;

    public static Raster CreateBgra32(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        // 行对齐到 4 字节：WPF 的 stride 要求，也让整行能按 32 位块拷贝。
        int stride = ((width * 4) + 3) & ~3;
        return new Raster(width, height, stride, new byte[(long)stride * height]);
    }

    /// <summary>裁一块下来。越界部分自动钳到自身范围内。</summary>
    public Raster Crop(int left, int top, int width, int height)
    {
        left = Math.Clamp(left, 0, Width);
        top = Math.Clamp(top, 0, Height);
        width = Math.Clamp(width, 1, Width - left);
        height = Math.Clamp(height, 1, Height - top);

        Raster result = CreateBgra32(width, height);
        for (int row = 0; row < height; row++)
        {
            Buffer.BlockCopy(
                Pixels,
                ((top + row) * Stride) + (left * 4),
                result.Pixels,
                row * result.Stride,
                width * 4
            );
        }

        return result;
    }

    /// <summary>生成 WPF 位图源。仅在要显示或编码时调用。</summary>
    public BitmapSource ToBitmapSource()
    {
        BitmapSource source = BitmapSource.Create(
            Width,
            Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            Pixels,
            Stride
        );
        source.Freeze();
        return source;
    }

    /// <summary>把 alpha 全填成 255。屏幕内容本该不透明，但个别驱动会给出未初始化的 alpha。</summary>
    public void ForceOpaque()
    {
        for (int row = 0; row < Height; row++)
        {
            int offset = row * Stride + 3;
            for (int column = 0; column < Width; column++)
            {
                Pixels[offset + (column * 4)] = 255;
            }
        }
    }
}
