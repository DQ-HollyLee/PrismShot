using System;
using System.Runtime;

namespace PrismShot.Core;

/// <summary>
/// 把截图用掉的大块内存还给系统。
/// </summary>
/// <remarks>
/// <para>
/// 截图这条路上的每一块缓冲都是十几 MB 起步的大对象：回读出来的一整帧、
/// 色调映射的输出、裁剪出的选区。它们全落在大对象堆上，而大对象堆默认**不压缩** ——
/// 每张图尺寸都不同，于是几十次之后它变成一块瑞士奶酪，
/// 空着的地方凑不出一块连续的大缓冲，每次还得再向系统要一段新的。
/// 结果就是任务管理器上那个数字只涨不落，而堆里其实没几个活对象。
/// </para>
/// <para>
/// 这里做的就是在一次截图彻底结束之后强制压缩一次：把空洞挤掉，把空段还给系统。
/// 代价是一次几十毫秒的停顿，而它发生在用户已经拿到图之后，感觉不到。
/// </para>
/// </remarks>
internal static class MemoryPressure
{
    /// <summary>工作集超过这个数才值得动手。低于它时让 GC 自己找时机就够了。</summary>
    private const long ThresholdBytes = 192L * 1024 * 1024;

    public static long WorkingSetMb => Environment.WorkingSet / (1024 * 1024);

    /// <summary>必要时回收一次。返回是否真的做了回收。</summary>
    public static bool ReleaseIfNeeded()
    {
        if (Environment.WorkingSet < ThresholdBytes)
        {
            return false;
        }

        CollectFully();
        return true;
    }

    /// <summary>
    /// 一次能把内存真正还回去的回收。
    /// </summary>
    /// <remarks>
    /// <c>CompactOnce</c> 是关键的一位。它只对**下一次**回收生效，用一次就自动复位，
    /// 所以这里两轮都重新置一次位：第一轮挤掉空洞，
    /// 第二轮收掉第一轮释放出来的东西（终结器常常还会带出新的可回收对象）。
    /// </remarks>
    public static void CollectFully()
    {
        for (int round = 0; round < 2; round++)
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }
}
