// 结果窗口的配色。这一份**不是** StarPie 的 AppThemeManager —— 那个类拿不到（宿主内部），
// 而更关键的是：在 StarPie 里跑的时候根本不需要它。

using System;
using System.Windows;
using System.Windows.Media;
using Media = System.Windows.Media;
using Microsoft.Win32;

namespace StarPie.Plugin.PrismShot.Upstream;

/// <summary>
/// 给 <see cref="OcrResultWindow"/> 兜底配色。
/// </summary>
/// <remarks>
/// <para>
/// 上游在构造函数里写的是 <c>AppThemeManager.ApplyTheme(this, AppThemeManager.CurrentEffectiveTheme)</c>，
/// 它把当前主题的整套画笔写进窗口自己的 Resources。插件这边刻意<b>不照搬这一步</b>，
/// 因为照搬的结果只会更差：
/// </para>
/// <para>
/// 窗口里的颜色全是 <c>DynamicResource</c>，而 DynamicResource 是沿着元素树一路向上找的 ——
/// 宿主 AppThemeManager 早就把当前主题写进了 <c>Application.Current.Resources</c>，
/// 窗口一创建就自动解析到那上面。于是窗口跟着宿主的主题走：用户切成暗色、换成另一套配色，
/// 窗口跟着变，一次都不用重启。插件如果自己写死一套，反而会把宿主的当前主题盖掉。
/// </para>
/// <para>
/// 所以这里只在<b>没有宿主 Application</b> 的时候动手（独立验证桩、被别的壳加载）：
/// 那时 DynamicResource 找不到任何画笔，窗口会是一片透明 —— 看不见，等于没弹。
/// 此时按系统是亮色还是暗色注入一组默认色，值抄自 AppThemeManager 的 Light / Dark 两套。
/// </para>
/// </remarks>
internal static class OcrTheme
{
    /// <summary>窗口用到的画笔键。缺一个就是一处透明。</summary>
    private static readonly string[] Keys =
    {
        "CardBackgroundBrush",
        "CardBorderBrush",
        "TextPrimaryBrush",
        "TextSecondaryBrush",
        "TextMutedBrush",
        "SubtleCardBrush",
        "NavTabActiveBgBrush",
        "AccentPrimaryBrush",
    };

    // 与 AppThemeManager.SetThemeBrushes 的 Light 分支一致（宿主的默认亮色）
    private static readonly string[] Light =
    {
        "#FFFFFF", // CardBackground
        "#E2E8F0", // CardBorder
        "#0F172A", // TextPrimary
        "#475569", // TextSecondary
        "#64748B", // TextMuted
        "#F8FAFC", // SubtleCard
        "#EFF6FF", // NavTabActiveBg
        "#2563EB", // AccentPrimary
    };

    // 与 Dark 分支一致
    private static readonly string[] Dark =
    {
        "#131C2E",
        "#1E293B",
        "#F8FAFC",
        "#CBD5E1",
        "#94A3B8",
        "#0F172A",
        "#1E293B",
        "#3B82F6",
    };

    public static void Apply(System.Windows.Window window)
    {
        System.Windows.Application? app = System.Windows.Application.Current;

        // 宿主在跑：它的主题已经在 Application 级，别去盖。
        if (app is not null && app.Resources.Contains("CardBackgroundBrush"))
        {
            return;
        }

        string[] palette = IsWindowsInDarkTheme() ? Dark : Light;
        for (int i = 0; i < Keys.Length; i++)
        {
            Media.SolidColorBrush brush = new(
                (Media.Color)Media.ColorConverter.ConvertFromString(palette[i])
            );
            brush.Freeze();
            window.Resources[Keys[i]] = brush;
        }
    }

    /// <summary>系统当前是不是深色模式。照搬 AppThemeManager.IsWindowsInDarkTheme 的判定。</summary>
    private static bool IsWindowsInDarkTheme()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"
            );

            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }
}
