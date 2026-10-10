using System.Drawing;
using System.Globalization;
using System.IO;
using StarPie.Plugin.PrismShot.Upstream;

namespace StarPie.Plugin.PrismShot;

/// <summary>
/// 「截图 + 识别」：用 PrismShot 完成选区，用 StarPie 的 OCR 引擎认出其中的文字。
/// </summary>
/// <remarks>
/// <para>
/// 与宿主自带的 <c>截屏识字</c> 的差别只在<b>谁来做选区</b>：这一段换成了 PrismShot 的冻结帧覆盖层，
/// 于是 HDR 屏幕上的高亮内容不会在送入识别之前先被压成一团过曝的白 ——
/// 文字连笔画都糊掉了的话，再好的识别引擎也没用。这正是整个插件存在的理由。
/// </para>
/// <para>
/// 识别那一刀是<b>照搬</b>的：<c>upstream/OcrManager.cs</c> 与 StarPie 的
/// <c>WinPieGestures/OcrManager.cs</c> 逐行同源，四个引擎（本地离线 / AI 视觉 /
/// 自定义 HTTP / 云端）、几何重排、文案后处理、识别前的图像预处理全都在。
/// 插件不另写一套识别逻辑 —— 用户选的引擎、语言与后处理规则必须只有一份真相。
/// </para>
/// <para>
/// 照搬的那份只在宿主还没开放接缝时代班。宿主一旦把
/// <c>IHostOcrService</c> 补进 SDK（提案见 <c>upstream/ocr-seam.md</c>），
/// <see cref="OcrBridge"/> 会立刻探测到并优先走宿主的引擎 ——
/// 重复实现永远不如唯一真相，代班的就该让位。
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
            Description = "用 PrismShot 选取区域，再用 StarPie 的 OCR 引擎识别其中的文字。",
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
        "选取区域 → 识别文字";

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

        Recognition recognized = await RecognizeAsync(imagePath, cancellationToken)
            .ConfigureAwait(false);

        if (recognized.Failure is not null)
        {
            _context.Log.Warn($"OCR 未取得文本：{recognized.Failure}");
            return ActionResult.Fail($"{recognized.Failure}\n选区图片已保留：{imagePath}");
        }

        // 上游对「诊断文本」（以 [ 开头的提示语，比如本地引擎没装、HTTP 报错）的处理是：
        // 照常显示，但不写剪贴板、不拿去搜索 —— 那是给用户看的话，不是识别出来的内容。
        bool copy = !recognized.IsDiagnostic
            && input.Bool("copyToClipboard", true)
            && _context.Info.HasCapability(PluginCapability.Clipboard);

        if (copy && !string.IsNullOrWhiteSpace(recognized.Text))
        {
            _ = _context.Host.SetClipboardText(recognized.Text);
        }

        TryDelete(imagePath);

        _context.Log.Info($"识别完成（{recognized.EngineName}），{recognized.Text.Length} 字。");
        return ActionResult.Ok(Summarize(recognized.Text), silent: false);
    }

    /// <summary>
    /// 真正去认字。宿主有接缝就走宿主，没有就走照搬的那份。
    /// </summary>
    private async Task<Recognition> RecognizeAsync(string imagePath, CancellationToken cancellationToken)
    {
        if (_bridge.Available)
        {
            OcrOutcome outcome = await _bridge
                .RecognizeAsync(imagePath, cancellationToken)
                .ConfigureAwait(false);

            if (!outcome.Available)
            {
                _context.Log.Warn($"宿主 OCR 接缝不可用，改用照搬的引擎：{outcome.Reason}");
            }
            else if (!string.IsNullOrWhiteSpace(outcome.Text))
            {
                return new Recognition(outcome.Text!, "StarPie OCR", false);
            }
            else
            {
                return new Recognition(string.Empty, string.Empty, false, outcome.Reason ?? "这一段区域里没有识别到文字。");
            }
        }

        return await RecognizeLocallyAsync(imagePath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 用照搬过来的引擎识别。
    /// </summary>
    /// <remarks>
    /// 先确认宿主配置里到底选了哪个引擎再开工，因为 AI / 自定义 HTTP 要发网络请求，
    /// 而「发请求」这件事必须能在日志里对得上用户的选择 —— 否则排查时根本不知道该查哪条线。
    /// 只记引擎名，密钥一个字都不写进日志。
    /// </remarks>
    private async Task<Recognition> RecognizeLocallyAsync(
        string imagePath,
        CancellationToken cancellationToken
    )
    {
        OcrRecognition result;
        try
        {
            // 位图必须在这里释放完再删文件：System.Drawing 在 Dispose 之前一直握着文件句柄，
            // 提前删会抛 IOException，而那时候图已经没用了 —— 失败得毫无意义。
            using Bitmap bitmap = new(imagePath);
            _context.Log.Info(
                $"开始识别：{bitmap.Width}x{bitmap.Height}，引擎 {OcrConfigLoader.Current.Provider}"
                + $"（配置来自 {OcrConfigLoader.SourcePath}）"
            );

            result = await OcrManager.RecognizeAsync(bitmap, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new Recognition(string.Empty, string.Empty, false, $"识别失败：{ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(result.Text))
        {
            return new Recognition(string.Empty, string.Empty, false, "这一段区域里没有识别到文字。");
        }

        return new Recognition(result.Text, result.EngineName, result.IsDiagnostic);
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

    /// <summary>
    /// 一次识别的结局。<see cref="Failure"/> 非空表示没有拿到文本，此时 <see cref="Text"/> 为空串 ——
    /// 让两种结局共用一条记录而不是搞成可空字符串，是为了别在下面每处都补一次 <c>!</c>：
    /// 「失败必有原因、成功必有文本」这个不变量写在这一个类型里就够了。
    /// </summary>
    private sealed record Recognition(
        string Text,
        string EngineName,
        bool IsDiagnostic,
        string? Failure = null
    );
}
