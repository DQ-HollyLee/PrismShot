using System;
using System.Collections.Generic;
using PrismShot.Core.Interop;
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

    /// <summary>
    /// 把多边形外的像素 alpha 清零 —— 圈选的输出应该是透明底的。
    /// </summary>
    /// <param name="polygon">虚拟坐标下的闭合多边形顶点。</param>
    /// <param name="originX">本张图左上角在虚拟桌面里的 X。</param>
    /// <param name="originY">同理，Y。</param>
    /// <remarks>
    /// 逐行做扫描线比「逐点调用 IsInside」快得多：一行只用求几次交点，而不是宽乘高次。
    /// 代价是这里多写三十行，但圈选区域动辄上百万像素，值得。
    /// </remarks>
    public void ApplyPolygonAlpha(IReadOnlyList<Point32> polygon, int originX, int originY)
    {
        if (polygon is null || polygon.Count < 3)
        {
            return;
        }

        for (int row = 0; row < Height; row++)
        {
            int worldY = originY + row;
            List<double> crossings = new(polygon.Count);

            for (int index = 0; index < polygon.Count; index++)
            {
                Point32 current = polygon[index];
                Point32 next = polygon[(index + 1) % polygon.Count];

                bool currentBelow = current.Y <= worldY;
                bool nextBelow = next.Y <= worldY;
                if (currentBelow == nextBelow)
                {
                    continue;
                }

                // 半开区间约定：只在「向上穿过」这条边上记一次交点，
                // 免得顶点正好落在扫描线上时被数两次，导致内部判定翻转。
                if (next.Y == current.Y)
                {
                    continue;
                }

                double ratio = (double)(worldY - current.Y) / (next.Y - current.Y);
                crossings.Add(current.X + (ratio * (next.X - current.X)));
            }

            if (crossings.Count == 0)
            {
                // 整行都在多边形外。
                ClearAlphaRow(row);
                continue;
            }

            crossings.Sort();
            int rowOffset = row * Stride;
            for (int column = 0; column < Width; column++)
            {
                int pixelOffset = rowOffset + (column * 4);
                Pixels[pixelOffset + 3] = IsInsideOdd(crossings, originX + column)
                    ? Pixels[pixelOffset + 3]
                    : (byte)0;
            }
        }
    }

    private void ClearAlphaRow(int row)
    {
        int offset = row * Stride + 3;
        for (int column = 0; column < Width; column++)
        {
            Pixels[offset + (column * 4)] = 0;
        }
    }

    private static bool IsInsideOdd(List<double> crossings, double worldX)
    {
        int count = 0;
        foreach (double x in crossings)
        {
            if (x > worldX)
            {
                count++;
            }
        }

        // 奇数个交点在右侧 = 内部。这里用严格大于，免得顶点处左右都算，退化成同样的奇偶错乱。
        return (count & 1) == 1;
    }
}
