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
    public bool TryAcquire() => TryAcquire(TimeSpan.Zero);

    /// <summary>
    /// 尝试成为主实例，抢不到时最多再等 <paramref name="waitForPrevious"/>。
    /// </summary>
    /// <remarks>
    /// 需要等待的唯一场景是「计划任务拉起的新实例」：它启动的那一刻，
    /// 上一个实例可能正在退出（用户刚双击过图标、或者刚从普通权限换成管理员权限）。
    /// 不等待的话它会看到互斥体还在，判定「已经有实例在跑」然后自己退出 ——
    /// 而那个正在退出的实例又马上把互斥体放掉了，最后就是两边都没有实例。
    /// </remarks>
    public bool TryAcquire(TimeSpan waitForPrevious)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        DateTime deadline = DateTime.UtcNow + waitForPrevious;
        while (true)
        {
            Mutex mutex = new(initiallyOwned: true, MutexName, out bool createdNew);
            if (createdNew)
            {
                _mutex = mutex;
                return true;
            }

            // 别人持有着。这一份引用立刻放掉，免得拖到进程退出才释放，
            // 期间主实例万一崩了，下一个启动者会误以为还活着。
            mutex.Dispose();

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            Thread.Sleep(120);
        }
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
