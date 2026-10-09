namespace StarPie.Plugin.PrismShot;

/// <summary>与 Windows 截图工具对齐的四种选取方式。</summary>
internal enum ShotMode
{
    /// <summary>矩形区域（拖拽）。</summary>
    Region,

    /// <summary>任意圈选（套索）。</summary>
    Freeform,

    /// <summary>单击一个窗口。</summary>
    Window,

    /// <summary>整屏 / 虚拟桌面。</summary>
    FullScreen,
}

/// <summary>
/// 一个截图动作。四种模式共用这一个实现，只有描述符与 mode 文本不同。
/// </summary>
/// <remarks>
/// <b>为什么注册成四个动作而不是一个带下拉参数的动作</b>：
/// 用户在轮盘上常用的是「区域」一种，把它做成独立条目就能直接挂扇区、也给得起专属名字与图标；
/// 而塞进下拉的话，每次配置都要先选一下模式，轮盘上还看不出这个扇区到底在截什么。
/// 代价是四个条目，但它换来的是「一眼看懂」，这正是截图这种高频动作最值钱的东西。
/// </remarks>
internal sealed class CaptureShotAction : IActionContribution
{
    private readonly IPluginContext _context;
    private readonly ActionDescriptor _descriptor;
    private readonly string _modeText;

    private static readonly IReadOnlyList<ParameterField> Fields = new List<ParameterField>
    {
        new()
        {
            Key = "format",
            Label = "图片格式",
            Type = ParameterFieldType.Enum,
            Placeholder = "跟随插件设置",
            HelpText = "留空时先取插件设置页的值，再没有就沿用 PrismShot 的配置（默认 jpg）。",
            Options = new List<ParameterOption>
            {
                new() { Value = "jpg", Label = "JPG（StarPie OCR 最稳）" },
                new() { Value = "png", Label = "PNG（无损，支持透明）" },
                new() { Value = "bmp", Label = "BMP" },
                new() { Value = "tif", Label = "TIFF" },
                new() { Value = "gif", Label = "GIF" },
            },
        },
        new()
        {
            Key = "quality",
            Label = "JPG 质量",
            Type = ParameterFieldType.Number,
            Min = 1,
            Max = 100,
            Placeholder = "跟随插件设置",
            HelpText = "只对 JPG 生效。留空表示跟随插件设置页。",
        },
        new()
        {
            Key = "directory",
            Label = "保存目录",
            Type = ParameterFieldType.Folder,
            Placeholder = "跟随插件设置（默认 图片库\\PrismShot）",
            HelpText = "留空表示沿用 PrismShot 自己的输出目录设置。",
        },
    };

    public CaptureShotAction(IPluginContext context, ShotMode mode)
    {
        _context = context;
        (string displayName, string description, string modeText) = Describe(mode);

        _modeText = modeText;
        _descriptor = new ActionDescriptor
        {
            Id = IdOf(mode),
            DisplayName = displayName,
            Description = description,
            Category = "PrismShot",
            IconKey = "Camera",

            // 走串行动作：这一步要用宿主的 Launch 把 PrismShot 叫起来，
            // 而 Launch 只允许在串行动作里调用。期间动作线程会被占住，
            // 但用户此刻正在框选，本来也没有别的动作要排。
            Kind = ActionKind.Sequential,

            // 用户慢慢画框可能要几十秒，5 秒的默认值必然超时。
            TimeoutSeconds = 300,
        };
    }

    public ActionDescriptor Descriptor => _descriptor;

    public IReadOnlyList<ParameterField> Parameters => Fields;

    public string? Validate(IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters.TryGetValue("format", out string? format) && !string.IsNullOrWhiteSpace(format))
        {
            foreach (string known in ShotRunner.Formats)
            {
                if (string.Equals(format.Trim(), known, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            return $"不支持的格式「{format.Trim()}」。可选：{string.Join(" / ", ShotRunner.Formats)}。";
        }

        return null;
    }

    public string Preview(IReadOnlyDictionary<string, string> parameters)
    {
        string? format = ShotRunner.ResolveFormat(parameters, _context.Settings);
        return format is null ? _descriptor.DisplayName : $"{_descriptor.DisplayName} → {format.ToUpperInvariant()}";
    }

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

        string? format = ShotRunner.ResolveFormat(input.Parameters, _context.Settings);
        string? directory = ShotRunner.Resolve(input.Parameters, _context.Settings, SettingKeys.Directory);
        int? quality = ShotRunner.ResolveQuality(input.Parameters, _context.Settings);

        ShotRequest request = new()
        {
            Mode = _modeText,
            Format = format,
            Quality = quality ?? 0,
            OutputPath = ShotRunner.BuildOutputPath(directory, format ?? "jpg"),
        };

        _context.Log.Debug($"请求 PrismShot：{request.Mode} -> {request.OutputPath ?? "(默认目录)"}");

        ShotResponse reply = await PrismShotClient
            .SendAsync(request, ShotRunner.ReplyTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (reply.Ok && !reply.Canceled)
        {
            _context.Log.Info($"{_descriptor.DisplayName}完成：{reply.Describe()}");
            WarnIfDegraded(reply);
        }

        return ShotRunner.ToResult(reply);
    }

    /// <summary>
    /// 降级到 GDI 时留一条日志。这不是错误（功能不残废），但用户来问「怎么这张颜色不对」
    /// 的时候，这条日志是唯一能证明那一次到底走了哪条通道的东西。
    /// </summary>
    private void WarnIfDegraded(ShotResponse reply)
    {
        if (
            string.IsNullOrEmpty(reply.Pipeline)
            || string.Equals(reply.Pipeline, PrismShotClient.PipelineWgc, StringComparison.OrdinalIgnoreCase)
        )
        {
            return;
        }

        _context.Log.Warn(
            $"这一张走的是 {reply.Pipeline} 兜底通道：WGC 会话没能建立（远程桌面 / 锁屏 / 驱动异常常见），这一帧没有 HDR。"
        );
    }

    private static (string DisplayName, string Description, string ModeText) Describe(ShotMode mode) =>
        mode switch
        {
            ShotMode.Freeform => ("PrismShot 任意圈选", "套索画出任意形状，输出为透明的 PNG。", "freeform"),
            ShotMode.Window => ("PrismShot 窗口截图", "单击一个窗口，截取它的完整矩形。", "window"),
            ShotMode.FullScreen => ("PrismShot 全屏截图", "截取整个虚拟桌面（默认）或光标所在的那块屏。", "screen"),
            _ => ("PrismShot 区域截图", "拖拽矩形选区，HDR 屏上不会过曝。", "region"),
        };

    private static string IdOf(ShotMode mode) =>
        mode switch
        {
            ShotMode.Freeform => "captureFreeform",
            ShotMode.Window => "captureWindow",
            ShotMode.FullScreen => "captureScreen",
            _ => "captureRegion",
        };
}
