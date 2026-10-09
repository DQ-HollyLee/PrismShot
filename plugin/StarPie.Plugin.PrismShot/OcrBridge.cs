using System.Reflection;
using System.Threading.Tasks;

namespace StarPie.Plugin.PrismShot;

/// <summary>一次 OCR 的结果。<see cref="Available"/> 为 false 表示宿主这一侧还没有这个接缝。</summary>
internal readonly record struct OcrOutcome(bool Available, string? Text, string? Reason);

/// <summary>
/// 通向 StarPie OCR 引擎的<b>可选</b>接缝。
/// </summary>
/// <remarks>
/// <para>
/// 当前 StarPie SDK（1.10）对外部开放的只有一个 <c>IHostScreenCaptureService.CaptureAndRecognize()</c>：
/// 它会<b>再弹一次自己的框选窗口</b>。而需求要的是「用 PrismShot 完成选区，让 StarPie 认这段区域里的字」——
/// 那需要一个「拿一张图、返回识别文本」的入口，而它不在已发布的契约里。
/// </para>
/// <para>
/// 所以这里做的事情是：<b>先按约定去试，试不到就说实话</b>。
/// 宿主未来若把下面这个形态补进 <c>StarPie.Plugin.Abstractions</c>（补丁见
/// 同目录 <c>upstream/</c> 的说明），插件无需重新编译就能用上：
/// <code>
/// public interface IHostOcrService { Task&lt;string?&gt; RecognizeFileAsync(string imagePath, CancellationToken ct); }
/// public interface IOcrPluginContext : IPluginContext { IHostOcrService Ocr { get; } }
/// </code>
/// </para>
/// <para>
/// 用反射而不是编译期引用，是因为同一个插件 DLL 要能同时跑在「没打补丁的 1.10」与
/// 「打了补丁的宿主」上。名字匹配失败就是没有能力，不抛异常。
/// </para>
/// </remarks>
internal sealed class OcrBridge
{
    private const string ContextInterfaceName = "StarPie.Plugin.IOcrPluginContext";
    private const string ServicePropertyName = "Ocr";
    private const string MethodName = "RecognizeFileAsync";

    private readonly IPluginContext _context;
    private readonly object? _service;

    public OcrBridge(IPluginContext context)
    {
        _context = context;
        _service = TryResolveService(context);
        Available = _service is not null;
    }

    /// <summary>宿主是否提供了这个接缝。</summary>
    public bool Available { get; }

    /// <summary>不可用时的一句人话，直接给用户看。</summary>
    public string UnavailableReason =>
        "当前版本的 StarPie 还没有开放「对指定图片做文字识别」的接口。"
        + "选区图已保存，可以用 StarPie 自带的「截屏识字」识别它，"
        + "或给宿主打上 PrismShot 附带的 OCR 接缝补丁。";

    /// <summary>识别一张图片。<see cref="Available"/> 为 false 时返回 Available=false，不做任何调用。</summary>
    public async Task<OcrOutcome> RecognizeAsync(string imagePath, CancellationToken cancellationToken)
    {
        if (_service is null)
        {
            return new OcrOutcome(false, null, UnavailableReason);
        }

        MethodInfo? method = _service
            .GetType()
            .GetMethod(MethodName, BindingFlags.Public | BindingFlags.Instance);

        if (method is null)
        {
            return new OcrOutcome(false, null, $"宿主提供了 {ContextInterfaceName}，但没有 {MethodName} 方法。");
        }

        try
        {
            object? returned = method.Invoke(_service, new object[] { imagePath, cancellationToken });

            if (returned is not Task task)
            {
                return new OcrOutcome(false, null, $"{MethodName} 的返回值不是 Task。");
            }

            await task.ConfigureAwait(false);

            // 不想把插件绑定到 Task<string> 这一个具体实例化上：宿主那边写成 Task<string?> 或将来
            // 换成 Task<string> 都还是要一枚 string，统一从 Task.Result 上取最省心。
            PropertyInfo? result = task.GetType().GetProperty("Result");
            string? text = result?.GetValue(task) as string;

            return new OcrOutcome(true, text, null);
        }
        catch (TargetInvocationException ex)
        {
            return new OcrOutcome(true, null, ex.InnerException?.Message ?? ex.Message);
        }
        catch (Exception ex)
        {
            return new OcrOutcome(true, null, ex.Message);
        }
    }

    /// <summary>沿 SDK 1.10「可选扩展上下文」的形态去找服务实例。</summary>
    private static object? TryResolveService(IPluginContext context)
    {
        try
        {
            Type type = context.GetType();
            bool shapeMatches = type.GetInterfaces()
                .Any(item => string.Equals(item.FullName, ContextInterfaceName, StringComparison.Ordinal));

            if (!shapeMatches)
            {
                return null;
            }

            PropertyInfo? property = type.GetProperty(
                ServicePropertyName,
                BindingFlags.Public | BindingFlags.Instance
            );

            return property?.GetValue(context);
        }
        catch (Exception ex)
        {
            context.Log.Debug($"探查 OCR 接缝时出错（按不可用处理）：{ex.Message}");
            return null;
        }
    }
}
