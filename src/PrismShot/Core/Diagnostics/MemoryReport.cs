using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;

namespace PrismShot.Core.Diagnostics;

/// <summary>
/// 一行内存快照。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要把这些数字一起打出来：进程「占了 700MB」这一句话至少对应四种完全不同的东西 ——
/// 托管堆里真的活着的对象、GC 收掉了但没还给系统的已提交内存、被大对象堆碎片撑住的空洞、
/// 以及 WIC / D3D / 显卡驱动那些 GC 根本看不见的非托管分配。
/// 只看任务管理器那一列数字，这四种分不开，于是「优化」只能靠猜。
/// </para>
/// <para>
/// 输出的字段含义：
/// <list type="bullet">
/// <item><c>ws</c> 工作集，任务管理器里显示的就是它。</item>
/// <item><c>priv</c> 私有提交量。它比工作集更能说明「我们到底申请了多少」。</item>
/// <item><c>managed</c> 托管堆里活着的对象（<c>GC.GetTotalMemory(false)</c>，不触发回收）。</item>
/// <item><c>heap</c> GC 自己认为的堆大小；和 managed 的差值就是碎片与待回收对象。</item>
/// <item><c>committed</c> GC 向系统要下来的内存。它不缩回去，工作集就不会缩回去。</item>
/// <item><c>loh</c> 大对象堆。截图这条路上每一块缓冲都 ≥85KB，全部落在这里。</item>
/// <item><c>gdi/user</c> GDI 与 USER 对象数。窗口/画刷/位图泄漏会在这里露头。
/// 覆盖层每次截图都要建一整屏窗口，这个数字只涨不落就说明窗口没被真正回收。</item>
/// </list>
/// </para>
/// </remarks>
internal static class MemoryReport
{
    private const uint GdiObjects = 0;
    private const uint UserObjects = 1;

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

    /// <summary>取一行快照。<paramref name="label"/> 是给这一行起的名字。</summary>
    public static string Line(string label)
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();

        GCMemoryInfo info = GC.GetGCMemoryInfo();
        long loh = info.GenerationInfo.Length > 3 ? info.GenerationInfo[3].SizeAfterBytes : 0;

        return string.Format(
            CultureInfo.InvariantCulture,
            "[mem] {0,-16} ws={1,5}MB envws={2,5}MB priv={3,5}MB managed={4,5}MB heap={5,5}MB "
                + "committed={6,5}MB frag={7,4}MB loh={8,4}MB gdi={9,4} user={10,4}",
            label,
            Mb(process.WorkingSet64),
            Mb(Environment.WorkingSet),
            Mb(process.PrivateMemorySize64),
            Mb(GC.GetTotalMemory(forceFullCollection: false)),
            Mb(info.HeapSizeBytes),
            Mb(info.TotalCommittedBytes),
            Mb(info.FragmentedBytes),
            Mb(loh),
            GetGuiResources(process.Handle, GdiObjects),
            GetGuiResources(process.Handle, UserObjects)
        );
    }

    /// <summary>
    /// 一次能把内存真正还回去的回收。直接转交给 <see cref="MemoryPressure"/>，
    /// 免得「怎么回收」这件事存在两份实现。
    /// </summary>
    public static void CollectFully() => MemoryPressure.CollectFully();

    private static long Mb(long bytes) => bytes / (1024 * 1024);
}
