using System;
using System.IO;
using System.Text.Json;

namespace StarPie.Plugin.PrismShot.Upstream;

/// <summary>
/// 把宿主的 OCR 配置读进来。
/// </summary>
/// <remarks>
/// <para>
/// 照搬过来的 <see cref="OcrManager"/> 里写着
/// <c>ConfigManager.CurrentConfig?.OcrConfig</c>，而 <c>ConfigManager</c> 是宿主的内部类型，
/// 插件既不能引用也不该引用（插件禁止引用 StarPie.dll 是宿主的硬约束）。
/// 唯一正确的替身是<b>去读那份配置真正落盘的地方</b>：<c>%LOCALAPPDATA%\StarPie\config.json</c>。
/// </para>
/// <para>
/// 这样替换不是"退而求其次"，而是恰好保证了一件事：用户在 StarPie 设置里选的引擎、
/// 填的 Key、勾的后处理规则，插件用的是<b>同一份</b>。若插件自己另存一套，
/// 用户改了设置却发现插件没跟着变，那就是两个真相 —— 比功能缺失更难受。
/// </para>
/// <para>
/// 读不到、读坏了、字段对不上，一律退回默认值，绝不抛：
/// 识别失败可以提示，插件因为读配置崩掉会连累宿主的动作面板。
/// </para>
/// </remarks>
internal static class OcrConfigLoader
{
    private static readonly object Gate = new();

    private static OcrSettings _cached = new();
    private static string _cachedKey = "";
    private static string _sourcePath = "";

    /// <summary>当前生效的 OCR 配置。永远非 null。</summary>
    public static OcrSettings Current
    {
        get
        {
            string path = FindConfigPath();
            if (path.Length == 0)
            {
                return new OcrSettings();
            }

            string stamp = StampOf(path);
            lock (Gate)
            {
                if (!string.Equals(stamp, _cachedKey, StringComparison.Ordinal))
                {
                    _cached = Load(path) ?? new OcrSettings();
                    _cachedKey = stamp;
                    _sourcePath = path;
                }

                return _cached;
            }
        }
    }

    /// <summary>配置实际来自哪个文件。给用户看诊断信息用，不含任何密钥。</summary>
    public static string SourcePath => _sourcePath;

    /// <summary>
    /// 宿主配置目录。与上游 <c>ConfigManager.GetAppDataFolder()</c> 同规则：
    /// 新目录 <c>StarPie</c> 优先，只有它不存在时才回落到老目录 <c>WinPieGestures</c>。
    /// </summary>
    private static string RootFolder()
    {
        string localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
        if (localAppData.Length == 0)
        {
            localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        string modern = Path.Combine(localAppData, "StarPie");
        if (Directory.Exists(modern))
        {
            return modern;
        }

        string legacy = Path.Combine(localAppData, "WinPieGestures");
        return Directory.Exists(legacy) ? legacy : modern;
    }

    private static string FindConfigPath()
    {
        try
        {
            string path = Path.Combine(RootFolder(), "config.json");
            return File.Exists(path) ? path : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// 缓存键。用「最后写入时间 + 长度」而不是每次都解析一遍 71 KB 的 JSON ——
    /// 一次识别里这个属性会被读好几次，但更重要的是：用户在设置里改完引擎立刻生效，
    /// 不需要重启宿主。
    /// </summary>
    private static string StampOf(string path)
    {
        try
        {
            FileInfo info = new(path);
            return $"{path}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
        }
        catch
        {
            return "";
        }
    }

    private static OcrSettings? Load(string path)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using JsonDocument document = JsonDocument.Parse(
                stream,
                new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                }
            );

            // 大小写不敏感是刻意的：宿主自己的序列化选项与版本有关，
            // 而插件要能在若干个宿主版本上都读得出来。
            JsonSerializerOptions options = new() { PropertyNameCaseInsensitive = true };

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "OcrConfig", StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value.Deserialize<OcrSettings>(options);
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
