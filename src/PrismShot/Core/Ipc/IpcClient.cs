using System;
using System.IO;
using System.IO.Pipes;
using System.Threading.Tasks;

namespace PrismShot.Core.Ipc;

/// <summary>
/// 连到常驻实例的客户端。插件、第二个实例、命令行自测都走这里。
/// </summary>
internal static class IpcClient
{
    /// <summary>连接超时。常驻实例「活着但不响应」时，宁可快失败也不要把调用方挂死。</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    public static async Task<IpcResponse> SendAsync(
        IpcRequest request,
        TimeSpan? timeout = null
    )
    {
        try
        {
            using NamedPipeClientStream pipe = new(
                ".",
                IpcProtocol.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous
            );

            await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds).ConfigureAwait(false);
            pipe.ReadMode = PipeTransmissionMode.Message;

            using StreamReader reader = new(pipe, leaveOpen: true);
            using StreamWriter writer = new(pipe, leaveOpen: true) { AutoFlush = true };

            await writer.WriteLineAsync(IpcProtocol.Serialize(request)).ConfigureAwait(false);

            Task<string?> read = reader.ReadLineAsync();
            Task done = await Task
                .WhenAny(read, Task.Delay(timeout ?? TimeSpan.FromSeconds(120)))
                .ConfigureAwait(false);

            if (done != read)
            {
                return IpcResponse.Failed("等待常驻实例应答超时。");
            }

            string? line = await read.ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
            {
                return IpcResponse.Failed("常驻实例返回空应答。");
            }

            IpcResponse? response = System.Text.Json.JsonSerializer.Deserialize<IpcResponse>(
                line,
                IpcProtocol.Options
            );
            return response ?? IpcResponse.Failed("应答无法解析。");
        }
        catch (TimeoutException)
        {
            return IpcResponse.Failed("连不上常驻实例（超时）。");
        }
        catch (IOException ex)
        {
            return IpcResponse.Failed($"与常驻实例通信失败：{ex.Message}");
        }
        catch (Exception ex)
        {
            return IpcResponse.Failed(ex.Message);
        }
    }

    /// <summary>常驻实例是否在线。Ping 不打橄榄 Exception 到日志里，因为它本来就可能失败。</summary>
    public static async Task<bool> IsAliveAsync()
    {
        IpcResponse response = await SendAsync(
                new IpcRequest { Action = IpcActions.Ping },
                TimeSpan.FromSeconds(2)
            )
            .ConfigureAwait(false);
        return response.Ok && !string.IsNullOrEmpty(response.Error) == false;
    }
}
