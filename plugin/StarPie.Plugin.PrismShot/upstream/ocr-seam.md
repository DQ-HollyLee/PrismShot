# 上游提案：给 StarPie SDK 补一个「识别已有图片」的 OCR 接缝

这份文档是写给 StarPie 的上游提案（也可以看作 PrismShot 插件侧的实现约定）。
它解释**为什么需要**、**要长什么样**、以及**宿主那侧怎么落地**。

## 现状

SDK 1.10 里与 OCR 相关的唯一入口是：

```csharp
public interface IHostScreenCaptureService
{
    Task<CaptureAndRecognizeResult?> CaptureAndRecognize(CancellationToken ct = default);
}
```

它的行为是：**弹出宿主自己的选区窗口**（`ScreenSnipWindow`），用户框一块屏，然后识别。

PrismShot 插件的需求是反过来的：

> **选区由 PrismShot 完成，识别交给 StarPie。**

理由是分工明确的。PrismShot 拥有 WGC 浮点抓帧与按屏白电平做色调映射的能力，
在 HDR 屏上能给出一张曝光正确的图；而 OCR 引擎是 StarPie 的强项
（本地 WinRT / AI 视觉 / 自定义 HTTP / 云端四选一，外加一项很见功力的文案后处理）。
让任何一方去做对方的事，结果都是一份更差的成品。

用 `CaptureAndRecognize()` 无法实现这个分工 —— 用户会被要求框两次选区，
第一次框的结果直接被丢掉。这不是"凑合能用"，这是明显的产品缺陷。

## 提案：加一个「拿路径、返回文字」的入口

接口形态刻意做得极小。多返回一个 `engine`、`latency`、`lines` 之类指标的那一版我们想过又删了：
那些东西属于**结果展示**，而宿主自己的结果窗口已经把它们做完了；
插件真正需要的只有"这段文字"。

```csharp
// StarPie.Plugin.Abstractions/Services.cs

/// <summary>
/// 让用户/插件提供一张图片，交由宿主的 OCR 引擎识别。
/// 与 <see cref="IHostScreenCaptureService.CaptureAndRecognize"/> 的区别是
/// 它不再弹出自己的选区窗口 —— 图片由调用方提供。
/// </summary>
public interface IHostOcrService
{
    /// <param name="imagePath">本地图片文件路径（PNG / JPG / BMP 均可）。</param>
    /// <returns>识别出的文本；引擎失败返回 null，而不是抛。</returns>
    Task<string?> RecognizeFileAsync(string imagePath, CancellationToken cancellationToken = default);
}

/// <summary>扩展上下文：只有宿主实现了 OCR 接缝时才给出。</summary>
public interface IOcrPluginContext : IPluginContext
{
    IHostOcrService Ocr { get; }
}
```

能力门禁沿用既有的 `PluginCapability.ScreenCapture`（`1 << 9`）即可，
它的注释写的正是"读取屏幕内容（截屏后交给 OCR 识别）"—— 语义已经覆盖，
不新增位值，避免改动后续成员的位序。

这一步属于**能力新增**而非破坏性变更：`ApiVersionMinor` 提到 1.11，
清单里 `apiVersion` 仍然满足 `major == 1 && minor <= 11` 的判定，老插件不受影响。

## 宿主侧落地

宿主已有的一切都在 `WinPieGestures/OcrManager.cs` 里，缺的只是把它切成两半：

现在的 `ProcessSnippetAsync(Bitmap)` 一次做了三件事 ——
① 按 provider 分派识别、② 弹结果窗口、③ 写剪贴板。
插件要的只有 ①。所以第一步是抽出一个纯函数：

```csharp
// OcrManager.cs —— 从 ProcessSnippetAsync 里抽出「只识别」的部分
public static async Task<string> RecognizeBitmapCoreAsync(Bitmap bmp)
{
    OcrSettings config = ConfigManager.CurrentConfig?.OcrConfig ?? new OcrSettings();
    string provider = config.Provider?.Trim() ?? "Local";

    return provider switch
    {
        "Ai"     => await RecognizeWithAiVisionAsync(bmp, config),
        "Custom" => await RecognizeWithCustomHttpAsync(bmp, config),
        "Cloud"  => await RecognizeWithCloudAsync(bmp, config),
        _        => await RecognizeWithLocalWinRtAsync(bmp, config),
    };
}
```

原来的 `ProcessSnippetAsync` 改调这个函数，行为不变。
然后宿主实现接缝：

```csharp
// WinPieGestures/Plugin/OcrService.cs
internal sealed class HostOcrService : IHostOcrService
{
    // 由插件侧的 save-to-file 契约决定：一次一张图，路径已存在。
    public async Task<string?> RecognizeFileAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(imagePath)) return null;

        cancellationToken.ThrowIfCancellationRequested();

        // RecognizeBitmapCoreAsync 不像 ProcessSnippetAsync 那样在 finally 里释放 Bitmap ——
        // 后者是「流程函数」，顺手把图处理掉了；前者是纯识别，不该替调用方决定资源寿命。
        // 所以这里的所有权留在本方法，照常用 using。
        using Bitmap bitmap = new(imagePath);
        try
        {
            string raw = await OcrManager.RecognizeBitmapCoreAsync(bitmap).ConfigureAwait(false);

            OcrSettings config = ConfigManager.CurrentConfig?.OcrConfig ?? new OcrSettings();
            return OcrManager.PostProcessText(raw, config.RemoveSpacesBetweenCjk);
        }
        catch (Exception ex)
        {
            AppLogger.LogError("[plugin] 插件 OCR 请求失败", ex);
            return null;
        }
    }
}
```

最后是装配：让 `PluginContext` 实现 `IOcrPluginContext` 并把实例塞进 `Ocr`。

```csharp
internal sealed class OcrAwarePluginContext : PluginContext, IOcrPluginContext
{
    public IHostOcrService Ocr { get; } = new HostOcrService();
}
```

关键点：**`IPluginContext` 本身不变**（`IOcrPluginContext` 是额外派生），
这样老二进制里的 `IPluginContext` 转换不会失败，也不需要动 `ApiVersionMajor`。

## 插件侧约定（已实现）

PrismShot 插件已经按上面的形态写好了探测逻辑（`OcrBridge.cs`）：

- 反射查 `context` 是否实现了 `StarPie.Plugin.IOcrPluginContext`，再取 `Ocr` 属性
- 拿不到（老宿主）时不抛异常、不假装成功，而是保存选区图并返回一句清楚的提示
- 因为是反射而非编译期引用，**同一个插件 DLL 在打补丁前后都能用**，
  补丁落地那一刻功能自动生效，不需要等插件重新发布

## 为什么不用其它办法

| 备选方案 | 为什么不选 |
| --- | --- |
| 插件自己实现一个 OCR | 重复一份更差的结果，还可能把宿主的结果窗口 / 后处理 / provider 配置全部绕过去 |
| 插件写剪贴板后让宿主读剪贴板 | 宿主没有暴露"识别剪贴板内容"的入口，等于还是要一个接缝，而且多一次隐私争议 |
| 用现有的 `CaptureAndRecognize` | 要求用户框两次选区，其中一次的结果被丢弃 —— 明显的缺陷，不是取舍 |
| 在插件里 P/Invoke 宿主窗口类名去触发它的 OCR | 依赖宿主的私有实现细节，宿主一改就断，且完全不可控 |

## 附：采纳后的验证清单

上游若采纳，建议按这几项验一遍：

1. 未实现接缝的宿主上，插件仍能安装、其它四个动作不受影响
2. 打了补丁的宿主上，`captureOcr` 一次选区直接出文字
3. 图片路径不存在 / 是乱码文件时返回 null，不崩不抛
4. 取消（CancellationToken）能中断识别，不留悬挂句柄
5. 宿主 OCR 的 provider 切换（本地 / AI / 自定义 / 云端）对这个接缝同样生效
