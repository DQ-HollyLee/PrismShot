using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace PrismShot.Core.Imaging;

/// <summary>支持的输出格式。</summary>
internal enum ImageOutputFormat
{
    /// <summary>
    /// 默认格式。理由很直接：StarPie 的 AI OCR 接口吃得最稳的就是这一口，
    /// 而且体积小、到处都能读。8bit 有损在这里没什么损失 —— 截的是屏幕，不是照片。
    /// </summary>
    Jpg = 0,

    Png = 1,
    Bmp = 2,
    Tiff = 3,
    Gif = 4,
}

/// <summary>
/// 把 8bit 位图落成文件。
/// </summary>
/// <remarks>
/// 用的是 WPF 自带的 WIC 编码器。刻意不引 Magick.NET / ImageSharp / SkiaSharp：
/// 它们每一个都是几兆到几十兆的本机库，而 WIC 本来就在系统里，
/// 而且资源管理器的缩略图本来就是靠它解码的 —— 没必要为了编码 JPEG 再背一份。
/// 代价是拿不到 AVIF / JPEG XL 这些新编码，但目前的主流需求（OCR、贴图、存档）全够。
/// </remarks>
internal static class ImageWriter
{
    public static string ExtensionOf(ImageOutputFormat format) =>
        format switch
        {
            ImageOutputFormat.Jpg => ".jpg",
            ImageOutputFormat.Png => ".png",
            ImageOutputFormat.Bmp => ".bmp",
            ImageOutputFormat.Tiff => ".tif",
            ImageOutputFormat.Gif => ".gif",
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

    public static ImageOutputFormat Parse(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => ImageOutputFormat.Jpg,
            "png" => ImageOutputFormat.Png,
            "bmp" => ImageOutputFormat.Bmp,
            "tiff" or "tif" => ImageOutputFormat.Tiff,
            "gif" => ImageOutputFormat.Gif,
            _ => ImageOutputFormat.Jpg,
        };

    /// <summary>保存。<paramref name="path"/> 必须已经带好正确的扩展名。</summary>
    /// <returns>实际写入的文件名（可能与传入不同，重名时会自动加后缀）。</returns>
    public static string Save(Raster raster, string path, int jpegQuality = 92)
    {
        string directory = Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(directory);
        string unique = MakeUnique(path);

        BitmapEncoder encoder = CreateEncoder(path, jpegQuality);
        encoder.Frames.Add(BitmapFrame.Create(raster.ToBitmapSource()));

        using FileStream stream = File.Open(unique, FileMode.Create, FileAccess.Write);
        encoder.Save(stream);

        return unique;
    }

    /// <summary>
    /// 编码成 PNG 字节，不落盘。
    /// </summary>
    /// <remarks>
    /// 剪贴板要放带透明通道的图时用得上：PNG 是唯一一种「贴出去之后透明还在」的通用载体，
    /// 而它的字节正好能原样塞进 <c>PNG</c> 这个剪贴板格式里，不用再造一种编码。
    /// </remarks>
    public static byte[] ToPngBytes(Raster raster)
    {
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(raster.ToBitmapSource()));

        using MemoryStream stream = new();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public static BitmapEncoder CreateEncoder(string path, int jpegQuality)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 1, 100) },
            ".png" => new PngBitmapEncoder(),
            ".bmp" => new BmpBitmapEncoder(),
            ".tif" or ".tiff" => new TiffBitmapEncoder
            {
                Compression = TiffCompressOption.Default,
            },
            ".gif" => new GifBitmapEncoder(),
            _ => throw new NotSupportedException($"不支持的输出格式：{extension}"),
        };
    }

    /// <summary>重名不覆盖：追加 _2、_3 ...</summary>
    public static string MakeUnique(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        string directory = Path.GetDirectoryName(path) ?? ".";
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);

        for (int index = 2; ; index++)
        {
            string candidate = Path.Combine(directory, $"{name}_{index}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
