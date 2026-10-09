using System;
using System.IO;
using System.Threading;

namespace PrismShot.Core.Ipc;

/// <summary>
/// 单实例互斥体。第二个进程启动时会在这里掉头，把命令转给已经在跑的那个实例。
/// </summary>
/// <remarks>
/// 为什么用具名 Mutex 而不是「找同名窗口」：后者要在窗口创建之后才能判定，
/// 而我们常态压根没有窗口。Mutex 在创建窗口之前就能拿到，判定更早也更干净。
/// 名字固定不加用户后缀 —— 多用户会话各自有自己的对象命名空间，不会互相抢。
/// </remarks>
internal sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = "Local\\PrismShot.SingleInstance";

    private Mutex? _mutex;
    private bool _disposed;

    /// <summary>尝试成为主实例。抢到返回 true（调用方要起管道服务端），抢不到返回 false。</summary>
    public bool TryAcquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Mutex mutex = new(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // 别人持有着。这一份引用立刻放掉，免得拖到进程退出才释放，
            // 期间主实例万一崩了，下一个启动者会误以为还活着。
            mutex.Dispose();
            return false;
        }

        _mutex = mutex;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug($"释放单实例互斥体失败：{ex.Message}");
        }

        _mutex = null;
    }
}
