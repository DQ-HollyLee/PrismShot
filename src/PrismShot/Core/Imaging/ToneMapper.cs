using System;
using System.Buffers.Binary;
using PrismShot.Core.Capture;

namespace PrismShot.Core.Imaging;

/// <summary>
/// 把 HDR 的浮点帧转成能拿去编码和显示的 8bit sRGB。
/// </summary>
/// <remarks>
/// <para>
/// 这里做的是「还原用户在屏幕上看见的样子」，不是「把所有高光压进 8bit」。两者区别很大：
/// 前者的做法是把 scRGB 线性值按这块屏的 SDR 白电平缩放后再走一遍 sRGB 传输函数，
/// 超出 1.0 的部分说明这块屏也渲染不出来，直接钉在白；
/// 后者会为了保住高光把整体压暗，截出来的图「看着有细节但和你眼睛看到的完全不一样」——
/// 那属于 HDR 转 SDR 的艺术再创作，不是截图工具该干的事。
/// </para>
/// <para>
/// 缩放因子的来历：scRGB 里 1.0 恒等于 80 尼特。而 HDR 模式下 DWM 会把 SDR 内容整体抬到
/// <c>SdrWhiteLevelInNits</c>，所以画面里一个本来是纯白的 SDR 像素，在浮点帧里的值是
/// <c>白电平 / 80</c>。要把它变回 SDR 的 1.0，就得乘 <c>80 / 白电平</c>。
/// </para>
/// </remarks>
internal static class ToneMapper
{
    /// <summary>scRGB 每像素 8 字节（R16G16B16A16Float）。</summary>
    private const int FloatBytesPerPixel = 8;

    /// <summary>把它当作真 HDR 内容的亮度门槛（相对 SDR 白电平的倍数）。</summary>
    public const float HdrContentThreshold = 1.15f;

    public static Raster ToSdr(ReadbackTexture texture, float sdrWhiteNits)
    {
        Raster result = Raster.CreateBgra32(texture.Width, texture.Height);

        if (!texture.IsFloat16)
        {
            CopyStraight(texture, result);
            return result;
        }

        float scale = ScreenshotDefaults.ReferenceSdrWhiteNits / Math.Max(sdrWhiteNits, 1f);

        ReadOnlySpan<byte> source = texture.Pixels;
        Span<byte> destination = result.Pixels;

        for (int row = 0; row < texture.Height; row++)
        {
            int sourceOffset = row * texture.RowPitch;
            int destinationOffset = row * result.Stride;

            for (int column = 0; column < texture.Width; column++)
            {
                int pixelStart = sourceOffset + (column * FloatBytesPerPixel);

                float red = ToSingle(
                    BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(pixelStart, 2))
                );
                float green = ToSingle(
                    BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(pixelStart + 2, 2))
                );
                float blue = ToSingle(
                    BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(pixelStart + 4, 2))
                );

                int target = destinationOffset + (column * 4);
                // 目标布局 BGRA，所以要反着写。
                destination[target] = Srgb.EncodeToByte(blue * scale);
                destination[target + 1] = Srgb.EncodeToByte(green * scale);
                destination[target + 2] = Srgb.EncodeToByte(red * scale);
                destination[target + 3] = 255;
            }
        }

        return result;
    }

    /// <summary>
    /// 判定这一帧里到底有没有真 HDR 内容。
    /// </summary>
    /// <remarks>
    /// HDR 屏开着不代表画面是 HDR 的 —— 一张放 Office 的桌面，Windows 也会全程用浮点路径合成。
    /// 只有在最大内容亮度明显超过 SDR 白电平时，才有必要走「保留高光」那一套麻烦的输出。
    /// 扫描步长刻意取粗：这只是一个开关判定，没必要为了 1% 的精度多花十倍的 CPU。
    /// </remarks>
    public static bool HasRealHdrContent(ReadbackTexture texture, float sdrWhiteNits)
    {
        if (!texture.IsFloat16)
        {
            return false;
        }

        float threshold = (sdrWhiteNits / ScreenshotDefaults.ReferenceSdrWhiteNits)
            * HdrContentThreshold;

        ReadOnlySpan<byte> source = texture.Pixels;
        int rowStep = Math.Max(1, texture.Height / 96);
        int columnStep = Math.Max(1, texture.Width / 96);

        for (int row = 0; row < texture.Height; row += rowStep)
        {
            int rowOffset = row * texture.RowPitch;
            for (int column = 0; column < texture.Width; column += columnStep)
            {
                int pixelStart = rowOffset + (column * FloatBytesPerPixel);

                // 只看亮度最高的通道就够了：想判断的是「有没有超过 SDR 白的高光」。
                float green = ToSingle(
                    BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(pixelStart + 2, 2))
                );
                float red = ToSingle(
                    BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(pixelStart, 2))
                );
                float blue = ToSingle(
                    BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(pixelStart + 4, 2))
                );

                if (MathF.Max(MathF.Max(red, green), blue) > threshold)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void CopyStraight(ReadbackTexture texture, Raster destination)
    {
        int bytesPerRow = texture.Width * 4;
        ReadOnlySpan<byte> source = texture.Pixels;
        Span<byte> target = destination.Pixels;

        for (int row = 0; row < texture.Height; row++)
        {
            source.Slice(row * texture.RowPitch, bytesPerRow)
                .CopyTo(target.Slice(row * destination.Stride, bytesPerRow));
        }

        destination.ForceOpaque();
    }

    private static float ToSingle(ushort halfBits) =>
        (float)BitConverter.UInt16BitsToHalf(halfBits);
}
