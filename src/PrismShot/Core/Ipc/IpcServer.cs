using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace PrismShot.Core.Ipc;

/// <summary>
/// 常驻实例上的管道服务端。
/// </summary>
/// <remarks>
/// <para>
/// 为什么管道要常开：一个冷启动的进程要做的事太多了 —— 起 XAML 栈、建 D3D 设备、挂 WGC，
/// 热键触发时用户已经在等这几百毫秒了。把命令转给已经在跑的那个实例，屏幕上看到的就是「即刻」。
/// 这也是 Windows 原生截图工具快的原因：它一直在那儿。
/// </para>
/// <para>
/// 一条连接一个线程就够了：本工具的调用频率是「用户按一次快门」量级，
/// 上异步 IO 管道只是把复杂度换来看不见的收益。
/// </para>
/// </remarks>
internal sealed class IpcServer : IDisposable
{
    private readonly Func<IpcRequest, Task<IpcResponse>> _handler;
    private readonly CancellationTokenSource _shutdown = new();
    private Thread? _loop;
    private bool _disposed;

    public IpcServer(Func<IpcRequest, Task<IpcResponse>> handler) => _handler = handler;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _loop = new Thread(ListenLoop) { IsBackground = true, Name = "PrismShot.IPC" };
        _loop.SetApartmentState(ApartmentState.MTA);
        _loop.Start();
    }

    private void ListenLoop()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                using NamedPipeServerStream pipe = new(
                    IpcProtocol.PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Message,
                    PipeOptions.Asynchronous
                );

                // WaitForConnectionAsync 对取消令牌的支持在 .NET 上是后来才补的，这里退一步用超时轮询，
                // 换来的是「关程序时管道线程一定会醒过来」这件确定性的事。
                Task connect = pipe.WaitForConnectionAsync();
                while (!connect.IsCompleted)
                {
                    if (_shutdown.IsCancellationRequested)
                    {
                        return;
                    }

                    connect.Wait(TimeSpan.FromMilliseconds(100));
                }

                HandleConnection(pipe);
            }
            catch (IOException ex)
            {
                Log.Debug($"管道连接中断：{ex.Message}");
            }
            catch (Exception ex)
            {
                Log.Error("管道服务端异常。", ex);
                Thread.Sleep(TimeSpan.FromMilliseconds(250));
            }
        }
    }

    private void HandleConnection(NamedPipeServerStream pipe)
    {
        using StreamReader reader = new(pipe, leaveOpen: true);
        using StreamWriter writer = new(pipe, leaveOpen: true) { AutoFlush = true };

        while (!_shutdown.IsCancellationRequested && pipe.IsConnected)
        {
            string? line = reader.ReadLine();
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            IpcResponse response = Dispatch(line);
            writer.WriteLine(IpcProtocol.Serialize(response));
        }
    }

    private IpcResponse Dispatch(string line)
    {
        try
        {
            IpcRequest? request = System.Text.Json.JsonSerializer.Deserialize<IpcRequest>(
                line,
                IpcProtocol.Options
            );
            if (request is null)
            {
                return IpcResponse.Failed("空请求。");
            }

            Log.Debug($"收到 IPC 命令：{request}");

            // 处理在 UI 线程上进行（ overlay 要开窗口），所以这里同步等结果。
            // 出异常的可能是 handler 内部的业务逻辑，包一层免得一条坏命令把服务端打死。
            return _handler(request).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Error("处理 IPC 命令失败。", ex);
            return IpcResponse.Failed(ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();

        // 自己连自己一下，把阻塞在 WaitForConnection 上的循环叫醒。
        // 不然关闭时要等到下一个连接才真正退出，用户会遇到「点了退出但进程还在」。
        try
        {
            using NamedPipeClientStream wakeup = new(
                ".",
                IpcProtocol.PipeName,
                PipeDirection.Out
            );
            wakeup.Connect(TimeSpan.FromMilliseconds(300));
        }
        catch
        {
            // 叫不醒说明本来就没阻塞，无所谓。
        }

        _loop?.Join(TimeSpan.FromSeconds(2));
        _shutdown.Dispose();
    }
}
