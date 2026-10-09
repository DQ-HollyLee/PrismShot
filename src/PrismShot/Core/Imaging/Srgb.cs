using System;
using System.Runtime.CompilerServices;

namespace PrismShot.Core.Imaging;

/// <summary>
/// sRGB 传输函数与它的查表实现。
/// </summary>
/// <remarks>
/// 为什么用查表而不是每个像素都 <c>MathF.Pow</c>：4K 一帧有 830 万个像素，
/// 每个像素三次 <c>Pow</c> 就是 2500 万次调用，光这一步就够让人感觉到按快门有延迟。
/// 而输出本来就是 8bit，输入量化到 1/65535 造成的误差不到 0.05 个色阶，肉眼不可能分辨，
/// 这笔交换很划算。
/// </remarks>
internal static class Srgb
{
    /// <summary>线性值 [0,1] 到 sRGB 码值的 Slots 数。</summary>
    private const int TableEntries = 65536;

    private static readonly byte[] EncodeTable = BuildEncodeTable();

    /// <summary>
    /// 线性光 [0,1] → sRGB 码值 [0,255]。超出上限的（HDR 高光，说明这块显示器也渲染不出来）
    /// 一律钉在 255。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte EncodeToByte(float linear)
    {
        if (!(linear > 0f))
        {
            // NaN 与负数都走这里。偶发脏数据时宁可黑一个像素，也不要让下标变成负数炸掉整帧。
            return 0;
        }

        int index = linear >= 1f ? TableEntries - 1 : (int)(linear * TableEntries);
        return EncodeTable[index];
    }

    private static byte[] BuildEncodeTable()
    {
        byte[] table = new byte[TableEntries];
        for (int index = 0; index < TableEntries; index++)
        {
            // 取格心的线性值，避免整体偏半个色阶。
            float linear = (index + 0.5f) / TableEntries;
            table[index] = EncodeSlow(linear);
        }

        return table;
    }

    /// <summary>sRGB 正向传输函数（IEC 61966-2-1）。</summary>
    private static byte EncodeSlow(float linear)
    {
        float encoded = linear <= 0.0031308f
            ? linear * 12.92f
            : (1.055f * MathF.Pow(linear, 1f / 2.4f)) - 0.055f;

        int value = (int)((encoded * 255f) + 0.5f);
        return (byte)Math.Clamp(value, 0, 255);
    }
}
