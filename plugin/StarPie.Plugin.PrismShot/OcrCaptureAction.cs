using System.Globalization;
using System.IO;

namespace StarPie.Plugin.PrismShot;

/// <summary>
/// 「截图 + 识别」：用 PrismShot 完成选区，把结果交给 StarPie 的 OCR。
/// </summary>
/// <remarks>
/// <para>
/// 与宿自带的 <c>截屏识字</c> 的差别只在<b>谁来做选区</b>：这一段换成了 PrismShot 的冻结帧覆盖层，
/// 于是 HDR 屏幕上的高亮内容不会在存入识别之前先被压成一团过曝的白 ——
/// 文字连笔画都糊掉了的话，再好的识别引擎也没用。
/// </para>
/// <para>
/// 识别那一刀用的仍然是 StarPie 自己的引擎与配置（本地离线 / AI 视觉 / 自定义 HTTP），
/// 插件不重复实现 OCR：用户选的引擎、语言与后处理规则必须只有一份真相。
/// </para>
/// <para>
/// 中间图落在插件私有数据目录里：识别成功即删，失败留存 ——
/// 那时候这张图是用户唯一还能拿去别处补救的东西。
/// </para>
/// </remarks>
internal sealed class OcrCaptureAction : IActionContribution
{
    private static readonly IReadOnlyList<ParameterField> Fields = new List<ParameterField>
    {
        new()
        {
            Key = "copyToClipboard",
            Label = "识别结果写入剪贴板",
            Type = ParameterFieldType.Bool,
            DefaultValue = "true",
            HelpText = "关掉则只在通知里显示识别到的文本。",
        },
    };

    private readonly IPluginContext _context;
    private readonly ActionDescriptor _descriptor;
    private readonly OcrBridge _bridge;

    public OcrCaptureAction(IPluginContext context)
    {
        _context = context;
        _bridge = new OcrBridge(context);

        _descriptor = new ActionDescriptor
        {
            Id = "captureOcr",
            DisplayName = "PrismShot 截图识字",
            Description = "用 PrismShot 选取区域并交给 StarPie 识别其中的文字。",
            Category = "PrismShot",
            IconKey = "Text",
            Kind = ActionKind.Sequential,
            TimeoutSeconds = 300,
        };
    }

    public ActionDescriptor Descriptor => _descriptor;

    public IReadOnlyList<ParameterField> Parameters => Fields;

    public string? Validate(IReadOnlyDictionary<string, string> parameters) => null;

    public string Preview(IReadOnlyDictionary<string, string> parameters) =>
        _bridge.Available ? "选取区域 → 识别文字" : "选取区域 → 识别文字（需宿主 OCR 接缝）";

    public async Task<ActionResult> ExecuteAsync(
        PluginActionInput input,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? problem = await ShotRunner
            .EnsureResidentAsync(_context, cancellationToken)
            .ConfigureAwait(false);
        if (problem is not null)
        {
            return ActionResult.Fail(problem);
        }

        // PNG 而不是 JPG：识别前的图不该先被 JPEG 咬一口，
        // 文字边缘的高频损失正是 OCR 最容易翻车的地方。
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string imagePath = Path.Combine(StagingDirectory(), $"PrismShot_ocr_{stamp}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);

        ShotRequest request = new()
        {
            Mode = "region",
            Format = "png",
            OutputPath = imagePath,

            // 这一条通路的结果是文字，不是图片：位图进剪贴板只会把用户刚复制的东西顶掉。
            SkipClipboard = true,
        };

        ShotResponse reply = await PrismShotClient
            .SendAsync(request, ShotRunner.ReplyTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (!reply.Ok)
        {
            TryDelete(imagePath);
            return ActionResult.Fail(reply.Error ?? "截图失败，原因未知。");
        }

        if (reply.Canceled)
        {
            TryDelete(imagePath);
            return ActionResult.Ok();
        }

        OcrOutcome outcome = await _bridge
            .RecognizeAsync(imagePath, cancellationToken)
            .ConfigureAwait(false);

        if (!outcome.Available)
        {
            _context.Log.Warn($"无法调用宿主 OCR：{outcome.Reason}");
            return ActionResult.Fail($"{outcome.Reason}\n选区图片已保留：{imagePath}");
        }

        if (!string.IsNullOrWhiteSpace(outcome.Text))
        {
            if (
                input.Bool("copyToClipboard", true)
                && _context.Info.HasCapability(PluginCapability.Clipboard)
            )
            {
                _ = _context.Host.SetClipboardText(outcome.Text);
            }

            TryDelete(imagePath);
            _context.Log.Info($"识别完成，{outcome.Text.Length} 字。");
            return ActionResult.Ok(Summarize(outcome.Text), silent: false);
        }

        // 识别不到字也是一种正常结果，不要让用户对着一张「成功」的提示去找文本在哪。
        string empty = outcome.Reason is null
            ? "这一段区域里没有识别到文字。"
            : $"识别失败：{outcome.Reason}";
        return ActionResult.Fail(empty);
    }

    /// <summary>中转图目录。用宿主的 DataDirectory：插件间互不干扰，且卸载时宿主一并带走。</summary>
    private string StagingDirectory() => Path.Combine(_context.DataDirectory, "ocr");

    /// <summary>通知里只能放一句话，取前 40 个字符足够辨认"是不是我要的那段"。</summary>
    private static string Summarize(string text)
    {
        string flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length <= 40 ? flat : flat[..40] + "…";
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _context.Log.Debug($"清理中转图失败（不影响结果）：{ex.Message}");
        }
    }
}
