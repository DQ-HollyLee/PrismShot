namespace StarPie.Plugin.PrismShot;

/// <summary>
/// PrismShot 的 StarPie 插件入口。
/// </summary>
/// <remarks>
/// <para>
/// 这一层只做三件事：注册五个动作、声明一张插件级设置页、登记一枚图标。
/// 不做任何 IO、不启线程、不读 PrismShot 的管道 —— 宿主调用
/// <see cref="Initialize"/> 的那一刻用户正在等界面响应，那一两百毫秒没地方藏。
/// </para>
/// <para>
/// 「找不找得到 PrismShot.exe」这种事留到动作被触发时再做：
/// 用户在设置页里改了路径之后不需要重启 StarPie，下次按扇区就立即生效。
/// </para>
/// </remarks>
public sealed class PrismShotPlugin : IStarPiePlugin
{
    private readonly List<IDisposable> _registrations = new();
    private bool _shutDown;

    public void Initialize(IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _registrations.Add(context.Actions.Register(new CaptureShotAction(context, ShotMode.Region)));
        _registrations.Add(context.Actions.Register(new CaptureShotAction(context, ShotMode.Freeform)));
        _registrations.Add(context.Actions.Register(new CaptureShotAction(context, ShotMode.Window)));
        _registrations.Add(context.Actions.Register(new CaptureShotAction(context, ShotMode.FullScreen)));
        _registrations.Add(context.Actions.Register(new OcrCaptureAction(context)));

        _registrations.Add(context.SettingsPage.Register(BuildSettingsPage()));

        context.Log.Info(
            $"PrismShot 插件已装载：5 个动作，宿主 {context.Info.HostVersion} / SDK {context.Info.ApiVersion}。"
        );
    }

    /// <summary>
    /// 卸载。宿主会兜底撤销所有贡献点，这里仍然自己放一遍 ——
    /// 插件作者的意图就该由插件自己表达清楚，依赖宿主兜底等于把安全绳系在别人的决定上。
    /// </summary>
    public void Shutdown()
    {
        if (_shutDown)
        {
            return;
        }

        _shutDown = true;

        foreach (IDisposable registration in _registrations)
        {
            try
            {
                registration.Dispose();
            }
            catch
            {
                // 卸载路径上不允许抛：抛出去会让 ALC 卸不下来，表现为"重启 StarPie 才能重装插件"。
            }
        }

        _registrations.Clear();
    }

    /// <summary>
    /// 插件级设置页：四个动作共用的默认值。
    /// </summary>
    /// <remarks>
    /// 值落在 <c>Settings</c> 的同一个命名空间里，读取见 <see cref="ShotRunner.Resolve"/>。
    /// 全部留空仍是有效的：那样一路回退到 PrismShot 自己的配置。
    /// </remarks>
    private static SettingsPageDescriptor BuildSettingsPage() =>
        new()
        {
            Title = "PrismShot 截图",
            Description = "这里设置的是默认值。轮盘扇区上单独填了参数时，以那一处为准。",
            Fields = new List<ParameterField>
            {
                new()
                {
                    Key = SettingKeys.ExecutablePath,
                    Label = "PrismShot.exe 路径",
                    Type = ParameterFieldType.File,
                    Placeholder = "留空时自动查找",
                    HelpText = "一般用不着填。便携版或装到奇怪位置时才需要指定。",
                },
                new()
                {
                    Key = SettingKeys.Format,
                    Label = "默认图片格式",
                    Type = ParameterFieldType.Enum,
                    Placeholder = "跟随 PrismShot 配置（默认 jpg）",
                    HelpText = "StarPie 的 AI OCR 吃 JPG 最稳；要看无损或透明请选 PNG。",
                    Options = new List<ParameterOption>
                    {
                        new() { Value = "jpg", Label = "JPG" },
                        new() { Value = "png", Label = "PNG" },
                        new() { Value = "bmp", Label = "BMP" },
                        new() { Value = "tif", Label = "TIFF" },
                        new() { Value = "gif", Label = "GIF" },
                    },
                },
                new()
                {
                    Key = SettingKeys.Quality,
                    Label = "默认 JPG 质量",
                    Type = ParameterFieldType.Number,
                    Min = 1,
                    Max = 100,
                    Placeholder = "跟随 PrismShot 配置（默认 92）",
                    HelpText = "只对 JPG 生效。",
                },
                new()
                {
                    Key = SettingKeys.Directory,
                    Label = "默认保存目录",
                    Type = ParameterFieldType.Folder,
                    Placeholder = "跟随 PrismShot 配置（默认 图片库\\PrismShot）",
                },
            },
        };
}
