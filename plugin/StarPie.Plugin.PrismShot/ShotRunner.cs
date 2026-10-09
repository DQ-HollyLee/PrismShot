using System.Globalization;

namespace StarPie.Plugin.PrismShot;

/// <summary>
/// 四个截图动作共用的那部分逻辑：叫起常驻实例、算出 params/settings 的最终取值、拼文件名。
/// </summary>
/// <remarks>
/// 抽出来而不是每个动作各写一遍，是因为「用户取消要不要算失败」「常驻实例不在时怎么兜」
/// 这类判断一旦有两份实现，迟早会出现「全屏截图会自启、窗口截图不自启」这种只对
/// 某一个入口成立的行为差异，而它看起来像是偶发 bug。
/// </remarks>
internal static class ShotRunner
{
    /// <summary>jpg / png / bmp / tif / gif。与 PrismShot 的 ImageWriter 支持集一致。</summary>
    public static readonly string[] Formats = { "jpg", "png", "bmp", "tif", "gif" };

    /// <summary>用户画框的时间没有上限，等待应答要给足额度；真正的上限由动作的 TimeoutSeconds 承担。</summary>
    public static readonly TimeSpan ReplyTimeout = TimeSpan.FromMinutes(10);

    /// <summary>叫起 PrismShot.exe 之后，等它把互斥体与管道建好。</summary>
    private const int LaunchGraceMilliseconds = 6000;

    /// <summary>
    /// 三级取值：动作参数 → 插件设置页 → 交给 PrismShot 自己的默认值（返回 null）。
    /// </summary>
    /// <remarks>
    /// 留空必须一路落到「PrismShot 自己决定」，而不是在这里补一个写死的默认：
    /// 用户在 StarPie 里把格式设成 png，却因为插件这边写死了 jpg 而无效，属于最难受的一类失效。
    /// </remarks>
    public static string? Resolve(
        IReadOnlyDictionary<string, string> parameters,
        IPluginSettings settings,
        string key
    )
    {
        if (parameters.TryGetValue(key, out string? fromAction) && !string.IsNullOrWhiteSpace(fromAction))
        {
            return fromAction.Trim();
        }

        string? fromSettings = settings.Get(key);
        return string.IsNullOrWhiteSpace(fromSettings) ? null : fromSettings.Trim();
    }

    /// <summary>格式取值，并做一次白名单校验。认不出来的写法宁可丢掉，也不要把一个坏值塞过管道。</summary>
    public static string? ResolveFormat(IReadOnlyDictionary<string, string> parameters, IPluginSettings settings)
    {
        string? value = Resolve(parameters, settings, "format");
        if (value is null)
        {
            return null;
        }

        foreach (string known in Formats)
        {
            if (string.Equals(value, known, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        return null;
    }

    public static int? ResolveQuality(IReadOnlyDictionary<string, string> parameters, IPluginSettings settings)
    {
        string? text = Resolve(parameters, settings, "quality");
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? Math.Clamp(value, 1, 100)
            : null;
    }

    /// <summary>
    /// 拼输出文件的完整路径；没有指定目录时返回 null，交给 PrismShot 按自己的配置落盘。
    /// </summary>
    /// <remarks>
    /// 文件名由插件这边生成，而不是「把目录当 OutputPath 传过去」——
    /// 后者仰赖对端「路径没有文件名时回退到模板」这条隐含规则，
    /// 那种「两边都刚好对着写」的默契在升级后是最先碎的东西。
    /// </remarks>
    public static string? BuildOutputPath(string? directory, string extension)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string name = $"PrismShot_{stamp}.{extension.TrimStart('.')}";
        return Path.Combine(Environment.ExpandEnvironmentVariables(directory), name);
    }

    /// <summary>
    /// 保证常驻实例在线。不在时尝试用宿主的进程能力把它叫起来。
    /// </summary>
    /// <returns>null 表示一切就绪；否则是一条可以直接给用户看的失败原因。</returns>
    public static async Task<string?> EnsureResidentAsync(
        IPluginContext context,
        CancellationToken cancellationToken
    )
    {
        if (PrismShotClient.IsResidentRunning())
        {
            return null;
        }

        string? executable = ResolveExecutable(context);

        // 起进程是 Process 能力名下的动作。清单里必然声明了它，
        // 这里仍然再问一次 —— 宿主可能会因为用户的安装确认而给出不同的答案。
        if (executable is null || !context.Info.HasCapability(PluginCapability.Process))
        {
            return executable is null
                ? "没有找到 PrismShot.exe。请在插件设置里填写它的完整路径，或重新安装 PrismShot。"
                : "PrismShot 尚未运行，且宿主没有授予「进程」能力，插件无法代你启动它。请手动运行一次 PrismShot。";
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!context.Host.Launch(executable))
        {
            return $"无法启动 {executable}。";
        }

        context.Log.Info($"已请求宿主启动 {executable}，等待常驻实例就绪……");

        bool ready = await Task.Run(
                () => PrismShotClient.WaitUntilReady(LaunchGraceMilliseconds),
                cancellationToken
            )
            .ConfigureAwait(false);

        return ready ? null : "PrismShot 已经启动，但迟迟没有响应（详见 PrismShot 自己的日志）。";
    }

    /// <summary>动作参数里可以单独指定 PrismShot.exe 的位置，没有时走自动查找。</summary>
    private static string? ResolveExecutable(IPluginContext context)
    {
        string? configured = context.Settings.Get(SettingKeys.ExecutablePath);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string expanded = Environment.ExpandEnvironmentVariables(configured.Trim());
            return File.Exists(expanded) ? expanded : null;
        }

        return PrismShotClient.LocateExecutable();
    }

    /// <summary>把一次管道应答翻译成 StarPie 的动作结果。</summary>
    public static ActionResult ToResult(ShotResponse reply)
    {
        if (!reply.Ok)
        {
            return ActionResult.Fail(reply.Error ?? "截图失败，原因未知。");
        }

        // 用户按 ESC 取消不是错误：给他一句字面的结果就够了，不要弹提示、也不要算失败。
        if (reply.Canceled)
        {
            return ActionResult.Ok();
        }

        // PrismShot 自己会弹缩略图通知，插件这边再喊一次就是第二个打扰。
        return ActionResult.Ok(reply.Describe());
    }
}

/// <summary>插件设置页所用的键。宿主写的值与 <c>Settings.Get</c> 是同一个命名空间。</summary>
internal static class SettingKeys
{
    public const string ExecutablePath = "exePath";
    public const string Format = "format";
    public const string Quality = "quality";
    public const string Directory = "directory";
}
