using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrismShot.Core.Ipc;

/// <summary>
/// 进程间命令。StarPie 插件、第二个 PrismShot 实例、命令行自测通道走的是同一套。
/// </summary>
/// <remarks>
/// 一开始考虑过直接把命令行参数扔给第二个实例去解析，但那样插件作者要自己拼字符串、
/// 还要处理转义，迟早写出 <c>--out "C:\Program Files\a b.png"</c> 这种 bug。
/// 这里统一成 JSON：一行一个命令、一行一个应答，管道语义天然就是「请求—应答」，
/// 而且将来要加字段（比如「这次要矩形的某个预设区域」）不用改协议版本。
/// </remarks>
internal sealed class IpcRequest
{
    /// <summary>动作名。见 <see cref="IpcActions"/>。</summary>
    public string Action { get; set; } = IpcActions.Capture;

    /// <summary>截图方式：screen / region / window / repeat。</summary>
    public string Mode { get; set; } = "region";

    /// <summary>存盘路径。留空表示由常驻实例按配置自己决定。</summary>
    public string? OutputPath { get; set; }

    /// <summary>输出格式。留空表示沿用配置。</summary>
    public string? Format { get; set; }

    /// <summary>0 或未指定表示沿用配置。</summary>
    public int Quality { get; set; }

    /// <summary>为 true 时只进内存不落盘（"只想要一张位图"的调用方用）。</summary>
    public bool NoSave { get; set; }

    /// <summary>为 true 时不走剪贴板。</summary>
    public bool SkipClipboard { get; set; }

    /// <summary>
    /// 日志里的样子。<see cref="Mode"/> 只对截图有意义，
    /// 否则会在日志里写出「收到 IPC 命令：reload region」这种让人以为
    /// 有人按了区域截图的东西。
    /// </summary>
    public override string ToString() =>
        string.Equals(Action, IpcActions.Capture, StringComparison.Ordinal) ? $"{Action} {Mode}" : Action;
}

/// <summary>动作名常量。字符串散落各处迟早拼错。</summary>
internal static class IpcActions
{
    /// <summary>抓一张。这是绝大多数调用的唯一目的。</summary>
    public const string Capture = "capture";

    /// <summary>探活。用来判断常驻实例是否还活着。</summary>
    public const string Ping = "ping";

    /// <summary>让常驻实例重新读配置。</summary>
    public const string Reload = "reload";

    /// <summary>退出常驻实例。卸载 / 升级前的收尾。</summary>
    public const string Quit = "quit";
}

/// <summary>
/// 应答。
/// </summary>
/// <remarks>
/// <see cref="Canceled"/> 与 <see cref="Ok"/> 是分开的两件事：用户按 ESC 取消是正常行为，
/// 不该让调用方以为出错然后弹个红叉。插件拿到 canceled = true 就该安安静静什么都不做。
/// </remarks>
internal sealed class IpcResponse
{
    public bool Ok { get; set; }

    /// <summary>用户主动取消（ESC / 右键）。不是错误。</summary>
    public bool Canceled { get; set; }

    public string? Error { get; set; }

    /// <summary>
    /// 给命令行看的一句话结果。截图类应答留空（尺寸和路径本身就是结果），
    /// reload / quit 这类没有产物的动作得自己说清楚做了什么 ——
    /// 否则命令行只能拼出一句「ok 0x0」，用户看着像出错。
    /// </summary>
    public string? Message { get; set; }

    public string? FilePath { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public string? Pipeline { get; set; }

    public bool HasHdrContent { get; set; }

    public long Bytes { get; set; }

    public static IpcResponse Success(
        string? filePath,
        int width,
        int height,
        string pipeline,
        bool hdr,
        long bytes
    ) =>
        new()
        {
            Ok = true,
            FilePath = filePath,
            Width = width,
            Height = height,
            Pipeline = pipeline,
            HasHdrContent = hdr,
            Bytes = bytes,
        };

    public static IpcResponse Cancelled() => new() { Ok = true, Canceled = true };

    /// <summary>没有产物、但确实做成了某件事的应答（reload / quit）。</summary>
    public static IpcResponse Done(string message) => new() { Ok = true, Message = message };

    public static IpcResponse Failed(string message) => new() { Ok = false, Error = message };
}

/// <summary>
/// 协议的读写。管道是字节流，没有消息边界，所以每行一条 JSON —— 换行天然是分隔符。
/// </summary>
internal static class IpcProtocol
{
    /// <summary>
    /// 管道名。没有全局前缀，因为 NamedPipeServerStream 会自己补上
    /// <c>\\.\pipe\</c>，Session 隔离由 pipe 的访问规则保证，不靠名字玩花样。
    /// </summary>
    public const string PipeName = "PrismShot.Control";

    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(IpcRequest request) =>
        JsonSerializer.Serialize(request, Options);

    public static string Serialize(IpcResponse response) =>
        JsonSerializer.Serialize(response, Options);

    /// <summary>
    /// 把应答转成给人看的一行字。
    /// </summary>
    /// <remarks>
    /// 这里必须按「这条应答有没有产物」分开说：截图有尺寸和路径，而 reload / quit
    /// 什么都没有。早先一律按截图的格式硬拼，于是 <c>--reload</c> 打印的是
    /// <c>ok 0x0</c> —— 一句话等于没说，还让人以为出了错。
    /// </remarks>
    public static string Describe(IpcResponse response)
    {
        if (!string.IsNullOrEmpty(response.Error))
        {
            return $"error: {response.Error}";
        }

        if (!string.IsNullOrEmpty(response.Message))
        {
            return $"ok {response.Message}";
        }

        if (response.Canceled)
        {
            return response.Width > 0 && response.Height > 0
                ? $"ok {response.Width}x{response.Height} canceled"
                : "canceled";
        }

        // 没有尺寸也没有路径 = 这条应答本来就不带产物，别再拼一个「0x0」出来。
        if (response.Width <= 0 && response.Height <= 0 && string.IsNullOrEmpty(response.FilePath))
        {
            return "ok";
        }

        return $"ok {response.Width}x{response.Height} {response.FilePath}";
    }
}
