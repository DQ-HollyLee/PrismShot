using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace StarPie.Plugin.PrismShot;

/// <summary>PrismShot 常驻实例的一次应答。</summary>
internal sealed class ShotResponse
{
    public bool Ok { get; set; }

    public bool Canceled { get; set; }

    public string? Error { get; set; }

    public string? FilePath { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary>实际走的通道：<c>wgc</c>（HDR 正确）或 <c>gdi</c>（兜底）。</summary>
    public string? Pipeline { get; set; }

    public bool HasHdrContent { get; set; }

    public long Bytes { get; set; }

    /// <summary>给日志和通知用的一句话。</summary>
    public string Describe()
    {
        if (!Ok)
        {
            return Error ?? "未知错误";
        }

        if (Canceled)
        {
            return "用户取消了选择";
        }

        string size = $"{Width}×{Height}";
        string pipeline = string.Equals(Pipeline, PrismShotClient.PipelineWgc, StringComparison.OrdinalIgnoreCase)
            ? "WGC"
            : "GDI 兜底";

        return HasHdrContent ? $"{size} · HDR · {pipeline}" : $"{size} · {pipeline}";
    }
}

/// <summary>一次请求。</summary>
/// <remarks>
/// 字段与 PrismShot 的 <c>IpcRequest</c> 一一对应，JSON 走 camelCase 且忽略 null ——
/// 两端各存一份完全相同的约定，是「不引用 PrismShot.dll」能成立的前提。
/// </remarks>
internal sealed class ShotRequest
{
    public string Action { get; set; } = "capture";

    /// <summary>region / freeform / window / screen / repeat。</summary>
    public string Mode { get; set; } = "region";

    public string? OutputPath { get; set; }

    public string? Format { get; set; }

    public int Quality { get; set; }

    public bool NoSave { get; set; }

    public bool SkipClipboard { get; set; }
}

/// <summary>
/// 与 PrismShot 常驻实例对话。
/// </summary>
/// <remarks>
/// <para>
/// 这里没有引用 PrismShot 的程序集，而是照协议自己写了一遍管道客户端。
/// 理由有两条：插件与主程序是分别发布、分别升级的两个东西，共用一个 DLL 会把版本绑死；
/// 而协议只有「一行 JSON 进、一行 JSON 出」这么多，重复这点代码比引入耦合划算。
/// </para>
/// <para>
/// 起进程这一步不在这里做：宿主插件不允许自己 <c>Process.Start</c>，
/// 那属于 <see cref="PluginCapability.Process"/>，要走 <c>IPluginContext.Host.Launch</c>。
/// 这里只负责「发现路径」与「等它就绪」。
/// </para>
/// </remarks>
internal static class PrismShotClient
{
    /// <summary>通道名。<c>wgc</c> 表示拿到了 HDR 正确的帧。</summary>
    public const string PipelineWgc = "wgc";

    private const string PipeName = "PrismShot.Control";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// 常驻实例能否连上。连接本身就是一次探活 —— 管道是唯一的入口，
    /// 连得上就一定有一个活着的实例在服务另一头，不需要再发明 ping 语义。
    /// </summary>
    public static bool IsResidentRunning()
    {
        try
        {
            using NamedPipeClientStream pipe = new(".", PipeName, PipeDirection.InOut);
            pipe.Connect(400);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>等常驻实例把管道建好（进程由调用方启动）。轮询比死等一个固定时长稳。</summary>
    public static bool WaitUntilReady(int maxMilliseconds)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < maxMilliseconds)
        {
            if (IsResidentRunning())
            {
                return true;
            }

            Thread.Sleep(120);
        }

        return IsResidentRunning();
    }

    public static async Task<ShotResponse> SendAsync(
        ShotRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken
    )
    {
        try
        {
            using NamedPipeClientStream pipe = new(
                ".",
                PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous
            );

            using CancellationTokenSource connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken
            );
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(3));

            await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);

            // leaveOpen 是必须的，不是为了省事。
            // 默认构造下 StreamReader/StreamWriter 会「接管」这个管道对象，而 using 的释放顺序是反的：
            // 后声明的 reader 先 Dispose → 它把管道关掉 → 接着 writer 的 Dispose 冲 buffer 时管道已经没了，
            // 抛 ObjectDisposedException("Cannot access a closed pipe")。
            // 更糟的是这个异常发生在写柠条成功之后、方法返回之前，会被下面的 catch 抓走，
            // 于是「截图其实成功了」被报成「通信失败」—— 一次成功的截图配一条报错，最难排查的那种。
            using StreamReader reader = new(pipe, new UTF8Encoding(false), leaveOpen: true);
            using StreamWriter writer = new(pipe, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
            };

            string requestLine = JsonSerializer.Serialize(request, Options);
            await writer.WriteLineAsync(requestLine.AsMemory(), cancellationToken).ConfigureAwait(false);

            using CancellationTokenSource readTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken
            );
            readTimeout.CancelAfter(timeout);

            string? reply = await reader.ReadLineAsync(readTimeout.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(reply))
            {
                return new ShotResponse { Ok = false, Error = "PrismShot 没有返回结果。" };
            }

            return JsonSerializer.Deserialize<ShotResponse>(reply, Options)
                ?? new ShotResponse { Ok = false, Error = "PrismShot 的应答无法解析。" };
        }
        catch (OperationCanceledException)
        {
            return new ShotResponse { Ok = false, Error = "等待 PrismShot 超时。" };
        }
        catch (Exception ex)
        {
            return new ShotResponse { Ok = false, Error = $"与 PrismShot 通信失败：{ex.Message}" };
        }
    }

    /// <summary>找 PrismShot.exe。顺序按可靠性排：环境变量 → 安装记录 → 常见路径。</summary>
    public static string? LocateExecutable()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable("PRISMSHOT_EXE");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(fromEnvironment))
        {
            return fromEnvironment;
        }

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\PrismShot");
            if (key?.GetValue("InstallPath") is string installed)
            {
                string candidate = Path.Combine(installed, "PrismShot.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch
        {
            // 注册表读不到不算错，继续往下找。
        }

        string[] fallbacks =
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "PrismShot",
                "PrismShot.exe"
            ),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "PrismShot",
                "PrismShot.exe"
            ),
        };

        foreach (string candidate in fallbacks)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
