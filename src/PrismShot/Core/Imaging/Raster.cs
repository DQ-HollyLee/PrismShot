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

        int vertexCount = polygon.Count;

        // 1) 拆成边。水平边直接丢掉：它跟任何扫描线都交不出有意义的一点，
        //    留着只会让交点翻倍，把下面的奇偶判据搅乱。
        List<ScanEdge> edges = new(vertexCount);
        for (int index = 0; index < vertexCount; index++)
        {
            Point32 current = polygon[index];
            Point32 next = polygon[(index + 1) % vertexCount];
            if (current.Y == next.Y)
            {
                continue;
            }

            edges.Add(ScanEdge.Of(current, next));
        }

        if (edges.Count == 0)
        {
            // 退化成一条水平带：围不出面积，整块都算外面。
            for (int row = 0; row < Height; row++)
            {
                ClearAlphaRow(row);
            }

            return;
        }

        // 2) 边按起始行排好，逐行只把「从这一行开始」的捞进活动表。
        //
        //    原来的写法是每一行都把全部几千条边重扫一遍，还给每行 new 一个
        //    同容量的 List<double> —— 2560x1440 全屏圈选，那是上亿次判断
        //    外加一千多次大数组分配，进程直接拖死（「圈选保存后卡死」就是它）。
        //    改成活动边表之后，每行只碰真正跨过这一行的那几条边。
        edges.Sort(static (left, right) => left.YLow.CompareTo(right.YLow));

        List<ScanEdge> active = new(edges.Count);
        double[] crossings = new double[edges.Count];
        int nextEdge = 0;

        for (int row = 0; row < Height; row++)
        {
            int worldY = originY + row;

            while (nextEdge < edges.Count && edges[nextEdge].YLow <= worldY)
            {
                active.Add(edges[nextEdge]);
                nextEdge++;
            }

            // 半开区间 [YLow, YHigh)：一到 YHigh 就该退场。
            // 否则顶点正好落在扫描线上时会被数两次，整行的内外判定跟着翻过来。
            for (int index = active.Count - 1; index >= 0; index--)
            {
                if (active[index].YHigh <= worldY)
                {
                    active[index] = active[^1];
                    active.RemoveAt(active.Count - 1);
                }
            }

            int crossingCount = 0;
            for (int index = 0; index < active.Count; index++)
            {
                crossings[crossingCount++] = active[index].XAt(worldY);
            }

            if (crossingCount == 0)
            {
                ClearAlphaRow(row);
                continue;
            }

            Array.Sort(crossings, 0, crossingCount);

            // 3) 交点把这一行切成若干段，偶数段在外面。
            //    按区间整段清，而不是逐像素去问「我在不在里面」——
            //    后者是「宽 × 高 × 交点数」，前者只有「宽 × 高」。
            ClearAlphaSpan(row, double.NegativeInfinity, crossings[0], originX);
            for (int index = 1; index < crossingCount - 1; index += 2)
            {
                ClearAlphaSpan(row, crossings[index], crossings[index + 1], originX);
            }

            ClearAlphaSpan(row, crossings[crossingCount - 1], double.PositiveInfinity, originX);
        }
    }

    private void ClearAlphaRow(int row) =>
        ClearAlphaSpan(row, double.NegativeInfinity, double.PositiveInfinity, 0);

    /// <summary>把某一行里 [worldFrom, worldTo) 这一段的 alpha 清零。</summary>
    private void ClearAlphaSpan(int row, double worldFrom, double worldTo, int originX)
    {
        int start = double.IsNegativeInfinity(worldFrom)
            ? 0
            : (int)Math.Ceiling(worldFrom - originX);
        int end = double.IsPositiveInfinity(worldTo)
            ? Width
            : (int)Math.Ceiling(worldTo - originX);

        if (start < 0)
        {
            start = 0;
        }

        if (end > Width)
        {
            end = Width;
        }

        if (start >= end)
        {
            return;
        }

        int offset = (row * Stride) + (start * 4) + 3;
        for (int column = start; column < end; column++)
        {
            Pixels[offset] = 0;
            offset += 4;
        }
    }

    /// <summary>
    /// 扫描线用的一条边。存「y 较小那一端」的坐标加斜率，逐行只要一次乘法。
    /// </summary>
    private readonly record struct ScanEdge(int YLow, int YHigh, double XLow, double Slope)
    {
        public static ScanEdge Of(Point32 a, Point32 b)
        {
            bool aIsLower = a.Y < b.Y;
            return new ScanEdge(
                aIsLower ? a.Y : b.Y,
                aIsLower ? b.Y : a.Y,
                aIsLower ? a.X : b.X,
                (double)(b.X - a.X) / (b.Y - a.Y)
            );
        }

        public double XAt(int worldY) => XLow + ((worldY - YLow) * Slope);
    }
}
