using System;

namespace PrismShot.Core.Capture;

/// <summary>抓帧走哪条通道。</summary>
/// <remarks>
/// <para>
/// 这不是一个可以随便切的开关。两条通道的差别不在画质上，而在**常驻内存**上：
/// </para>
/// <list type="bullet">
/// <item><b>WGC</b>（Windows.Graphics.Capture）能拿到 DWM 内部的浮点 scRGB 帧，
/// 这是 HDR 显示器上唯一不会过曝的路。代价是它必须配一台 D3D11 设备 ——
/// 而实测这一台设备一建起来，进程的私有提交量就上去了几百 MB，
/// 而且把设备释放掉也只能还回来十几 MB：那是驱动与 D3D 运行时自身的常驻，
/// 一个进程一旦碰过就要背到退出为止。</item>
/// <item><b>GDI</b> 只拿得到 DWM 降级出来的 8bit SDR 副本，整条路不碰 D3D，
/// 进程占用和普通小工具一个量级。</item>
/// </list>
/// <para>
/// 所以默认是 <see cref="Auto"/>：只在显示器**真的开着 HDR**（或色彩信息查不出来、
/// 不敢断言它是 SDR）时才付 WGC 的代价，否则一律走 GDI。
/// 显示器没开 HDR 的时候，浮点帧里根本不含超出 SDR 范围的内容，
/// 两条通道出来的图在肉眼和像素上都一致 —— 那几百 MB 就是纯浪费。
/// </para>
/// </remarks>
internal enum CaptureEngine
{
    /// <summary>自己判断：有 HDR 显示器才用 WGC，否则 GDI。</summary>
    Auto = 0,

    /// <summary>强制 WGC。HDR 屏必备，SDR 屏上会白白多背几百 MB。</summary>
    Wgc,

    /// <summary>强制 GDI。占用最小，但 HDR 屏上会把 HDR 内容截成过曝的 SDR。</summary>
    Gdi,
}

/// <summary>配置文本 → <see cref="CaptureEngine"/>。用户手改配置时大小写和别名都要认。</summary>
internal static class CaptureEngines
{
    public static CaptureEngine Parse(string? text) =>
        text?.Trim().ToLowerInvariant() switch
        {
            "wgc" or "hdr" or "float" => CaptureEngine.Wgc,
            "gdi" or "sdr" or "gdi-blit" => CaptureEngine.Gdi,
            _ => CaptureEngine.Auto,
        };

    public static string ToText(CaptureEngine engine) =>
        engine switch
        {
            CaptureEngine.Wgc => "wgc",
            CaptureEngine.Gdi => "gdi",
            _ => "auto",
        };
}
