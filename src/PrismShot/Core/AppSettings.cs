using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using IoDirectory = System.IO.Directory;

namespace PrismShot.Core;

/// <summary>
/// 用户配置。所有可调项都在这里，别处不许藏「顺手写死的常量」。
/// </summary>
/// <remarks>
/// 每次新增字段都要给一个「老配置里没有它时该是什么」的默认值，并且默认值必须是
/// 我们要的行为，而不是 <c>default</c>。配置文件是用户的，他可能三年都不打开一次，
/// 升级后行为不该因为多了一个字段而变。
/// </remarks>
internal sealed class AppSettings
{
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public OutputSettings Output { get; set; } = new();

    public HotkeySettings Hotkeys { get; set; } = new();

    public BehaviorSettings Behavior { get; set; } = new();

    public RecorderSettings Recorder { get; set; } = new();

    /// <summary>
    /// 升到 2 的那一版把默认输出格式从 JPG 换成了 PNG —— JPG 没有透明通道，
    /// 圈选（任意形状）存出来只能是外接矩形。见 <see cref="SettingsStore.Normalize"/> 里的迁移。
    /// </summary>
    public const int CurrentSchemaVersion = 2;
}

internal sealed class OutputSettings
{
    /// <summary>
    /// jpg / png / bmp / tif / gif。默认 png —— 无损，且是唯一装得下
    /// 圈选（任意形状）透明通道的格式。
    /// </summary>
    public string Format { get; set; } = "png";

    public int JpegQuality { get; set; } = 92;

    /// <summary>空 = 图片库下的 PrismShot 目录。</summary>
    public string Directory { get; set; } = string.Empty;

    public string FileNamePattern { get; set; } = "{date}_{time}";

    public bool SaveToFile { get; set; } = true;

    public bool CopyToClipboard { get; set; } = true;

    /// <summary>
    /// 剪贴板里放文件路径（CF_HDROP）而不是位图。
    /// 默认 false：放位图时在聊天框里能直接粘贴出图，放路径则要看接收方认不认。
    /// </summary>
    public bool ClipboardAsFile { get; set; } = false;
}

internal sealed class HotkeySettings
{
    /// <summary>区域截图。默认抢占原来属于「屏幕草图」的组合。</summary>
    public string Region { get; set; } = "Win+Shift+S";

    /// <summary>全屏截图。默认抢占 PrintScreen。</summary>
    public string FullScreen { get; set; } = "PrintScreen";

    /// <summary>窗口截图。空 = 不绑定。</summary>
    public string Window { get; set; } = string.Empty;

    /// <summary>重复上一次的选区。空 = 不绑定。</summary>
    public string RepeatLastRegion { get; set; } = string.Empty;

    /// <summary>覆盖层里触发录像宏的那个键。空 = 覆盖层里不提供。</summary>
    public string RecorderMacro { get; set; } = "Alt+F9";
}

internal sealed class BehaviorSettings
{
    public bool LaunchOnBoot { get; set; } = true;

    /// <summary>virtual = 所有显示器拼起来；cursor = 光标所在的那一块。</summary>
    public string FullScreenScope { get; set; } = "virtual";

    /// <summary>覆盖层里默认选中的工具。Rect / Freeform / Window / Fullscreen。</summary>
    public string DefaultSelectionMode { get; set; } = "Rect";

    public bool OverlayDimBackground { get; set; } = true;

    public bool OverlayMagnifier { get; set; } = true;

    /// <summary>截图后右下角浮出的缩略图。关掉的话截图就完全「无声」了。</summary>
    public bool ShowThumbnail { get; set; } = true;

    public int ThumbnailSeconds { get; set; } = 3;

    /// <summary>托盘图标。默认关 —— 常驻存在感越低越好，要改配置去开始菜单找设置。</summary>
    public bool ShowTrayIcon { get; set; } = false;

    /// <summary>抓单块屏的超时。虚拟机上适当调大。</summary>
    public int PerDisplayTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// 抓帧通道：auto（默认）/ wgc / gdi。
    /// </summary>
    /// <remarks>
    /// 见 <see cref="Capture.CaptureEngine"/>：WGC 是 HDR 屏唯一正确的路，
    /// 但它要建一台 D3D11 设备，实测会给常驻进程留下几百 MB 拿不回来的私有提交内存。
    /// 默认的 auto 只在真有 HDR 需求时才付这笔钱；老配置里没有这一项时也按 auto 处理。
    /// </remarks>
    public string CaptureEngine { get; set; } = "auto";

    /// <summary>
    /// 每次截图后是否同时留一份 16bit HDR 副本（PNG，BT.2020 + PQ）。
    /// 默认关：绝大多数用途是 OCR 和分享，多存一份大文件只是占地方。
    /// </summary>
    public bool SaveHdrCopy { get; set; } = false;
}

internal sealed class RecorderSettings
{
    /// <summary>xbox / nvidia / amd / custom。决定往 <see cref="Keys"/> 里塞什么。</summary>
    public string Preset { get; set; } = "xbox";

    /// <summary>自定义组合键。只有 Preset = custom 时生效。</summary>
    public string Keys { get; set; } = "Win+Alt+R";

    /// <summary>非空时改为启动这个程序，而不是发组合键。给没有全局录像热键的方案用。</summary>
    public string LaunchPath { get; set; } = string.Empty;
}

/// <summary>配置的读写。写入是原子的，读坏了不会把用户配置弄丢。</summary>
internal static class SettingsStore
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static AppSettings Load()
    {
        string path = AppPaths.ConfigPath;
        if (!File.Exists(path))
        {
            return new AppSettings();
        }

        try
        {
            string json = File.ReadAllText(path);
            AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(json);
            return settings is null ? new AppSettings() : Normalize(settings);
        }
        catch (Exception ex)
        {
            // 配置读坏了不要让软件起不来：改名留证，按默认值继续跑。
            Log.Error($"配置解析失败，已备份为 settings.bad.json 并回退默认值。", ex);
            TryBackup(path);
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            IoDirectory.CreateDirectory(AppPaths.DataDirectory);
            string path = AppPaths.ConfigPath;
            string temporary = path + ".tmp";

            // 先写临时文件再替换：中途断电也不会留下半截 JSON 把用户配置顶掉。
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, WriteOptions));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("保存配置失败。", ex);
        }
    }

    private static void TryBackup(string path)
    {
        try
        {
            File.Move(
                path,
                Path.Combine(AppPaths.DataDirectory, "settings.bad.json"),
                overwrite: true
            );
        }
        catch
        {
            // 备份失败无所谓，反正默认值已经能跑了。
        }
    }

    /// <summary>把越界或空值拉回可用范围，免得一份手改坏了的配置让软件行为诡异。</summary>
    private static AppSettings Normalize(AppSettings settings)
    {
        settings.Output.JpegQuality = Math.Clamp(settings.Output.JpegQuality, 1, 100);
        settings.Behavior.ThumbnailSeconds = Math.Clamp(
            settings.Behavior.ThumbnailSeconds,
            1,
            30
        );
        settings.Behavior.PerDisplayTimeoutSeconds = Math.Clamp(
            settings.Behavior.PerDisplayTimeoutSeconds,
            1,
            60
        );

        if (string.IsNullOrWhiteSpace(settings.Output.FileNamePattern))
        {
            settings.Output.FileNamePattern = "{date}_{time}";
        }

        if (string.IsNullOrWhiteSpace(settings.Output.Format))
        {
            settings.Output.Format = "png";
        }

        // 老配置里没有这一项时反序列化出来是 null/空 —— 必须落回 auto。
        // 落成别的值（比如当成 wgc）会让每个升级上来的用户都白白多背几百 MB。
        if (string.IsNullOrWhiteSpace(settings.Behavior.CaptureEngine))
        {
            settings.Behavior.CaptureEngine = "auto";
        }

        // schema 1 → 2：默认输出格式 JPG → PNG。
        //
        // 老配置里那句 "jpg" 是当初自动写进去的默认值，不是用户手改的偏好 ——
        // 留着它，圈选就永远存不出形状（JPG 没有 alpha 通道，遮罩会被整段跳过）。
        // 所以升级时把它换成 png；这样改过一次之后，用户再想手动调回 jpg，没人会再动它。
        if (settings.SchemaVersion < 2)
        {
            if (string.Equals(settings.Output.Format, "jpg", StringComparison.OrdinalIgnoreCase))
            {
                settings.Output.Format = "png";
            }

            settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        }

        return settings;
    }
}
