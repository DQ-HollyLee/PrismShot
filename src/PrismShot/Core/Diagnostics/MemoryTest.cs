using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using PrismShot.Core.Capture;
using PrismShot.Core.Imaging;
using PrismShot.Core.Interop;
using PrismShot.Core.Ipc;
using PrismShot.Core.Platform;
using PrismShot.Ui;

namespace PrismShot.Core.Diagnostics;

/// <summary>
/// 内存实测通道。<c>PrismShot.exe --memtest</c>。
/// </summary>
/// <remarks>
/// <para>
/// 它不是一个「功能」，是一把尺子。用户报「内存占了一个 G」的时候，
/// 光看任务管理器那一列数字既分不清是托管堆还是非托管，也分不清是泄漏还是没还回去，
/// 最后只能靠猜着改。这里把整条链路按可重复的方式跑一遍，
/// 每一步都留一行快照，改前改后跑同一条命令就能直接对比。
/// </para>
/// <para>
/// 全程无副作用：不落盘、不碰剪贴板、不弹通知、不建可见窗口。
/// 它靠 <c>GC.GetAllocatedBytesForCurrentThread</c> 量「分配了多少」，
/// 而不是靠窗口真的画出来 —— 后者会受渲染线程节奏影响，反而是不可重复的。
/// </para>
/// </remarks>
internal static class MemoryTest
{
    public static int Run(string[] args)
    {
        int captures = ParseInt(args, "--count", 4);
        int refreshes = ParseInt(args, "--refresh", 0);
        bool trim = HasFlag(args, "--trim");

        Console.WriteLine(MemoryReport.Line("startup"));

        AppSettings settings = SettingsStore.Load();

        // 实测通道不许有任何副作用。否则跑一次就多一张图、多一次剪贴板覆盖。
        settings.Output.SaveToFile = false;
        settings.Output.CopyToClipboard = false;
        settings.Behavior.ShowThumbnail = false;

        string? engine = NameValue(args, "--engine");
        if (!string.IsNullOrWhiteSpace(engine))
        {
            settings.Behavior.CaptureEngine = engine;
        }

        using KeyboardHook hook = new();
        using CaptureSession session = new(settings, hook, Dispatcher.CurrentDispatcher);

        IpcRequest request = new()
        {
            Mode = "screen",
            NoSave = true,
            SkipClipboard = true,
        };

        for (int index = 1; index <= captures; index++)
        {
            Stopwatch watch = Stopwatch.StartNew();
            IpcResponse response = session
                .ExecuteAsync(request, SelectionMode.FullScreen)
                .GetAwaiter()
                .GetResult();
            watch.Stop();

            if (!response.Ok)
            {
                Console.WriteLine($"capture {index} failed: {response.Error}");
                return 1;
            }

            if (trim)
            {
                TrimWorkingSet();
            }

            Console.WriteLine(
                $"{MemoryReport.Line($"capture {index}/{captures}")} 用时={watch.ElapsedMilliseconds}ms"
            );
        }

        MemoryReport.CollectFully();
        Console.WriteLine(MemoryReport.Line("after collect"));

        if (refreshes > 0)
        {
            MeasureOverlayRefresh(refreshes);
            MemoryReport.CollectFully();
            Console.WriteLine(MemoryReport.Line("after collect"));
        }

        return 0;
    }

    /// <summary>
    /// 把本进程的物理页尽量还给系统。
    /// </summary>
    /// <remarks>
    /// 注意它减的是**工作集**（任务管理器里那一列），不是提交量：
    /// 那些页会进后备列表，系统随时可以拿去干别的，下次真要用到时再按需换回来。
    /// 对一个几小时才被人按一次的常驻工具来说，「占着物理内存不放」比「下次多几十毫秒」糟得多。
    /// </remarks>
    private static void TrimWorkingSet()
    {
        try
        {
            _ = EmptyWorkingSet(IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[mem] trim 失败：{ex.Message}");
        }
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr process);

    private static bool HasFlag(string[] args, string name)
    {
        foreach (string arg in args)
        {
            if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? NameValue(string[] args, string name)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    /// <summary>
    /// 量覆盖层「重画一次」要分配多少字节。
    /// </summary>
    /// <remarks>
    /// 这是整个程序里唯一一条由鼠标驱动的循环：拖动时每次 <c>MouseMove</c> 都会走一遍
    /// <see cref="OverlayLayer.Refresh"/>，而它每次都要重建一遍选中区域的几何图形。
    /// 一次框选持续十几秒、鼠标事件几千次，所以哪怕每次只分配几十 KB，
    /// 累计也是几百 MB 的分配量 —— 这些对象会被迅速提升到 Gen2，
    /// 一旦 GC 的节奏跟不上，工作集就会一路涨上去。
    /// </remarks>
    private static void MeasureOverlayRefresh(int iterations)
    {
        List<DisplayTarget> targets = MonitorEnumerator.Enumerate();
        if (targets.Count == 0)
        {
            Console.WriteLine("[mem] refresh          skipped: no display");
            return;
        }

        DisplayTarget target = targets[0];

        // 只为了给窗口一个尺寸，内容是什么无所谓，所以不去抓屏。
        Raster placeholder = Raster.CreateBgra32(target.PixelWidth, target.PixelHeight);

        Measure(target, placeholder, SelectionMode.Rect, iterations);
        Measure(target, placeholder, SelectionMode.Freeform, iterations);
    }

    private static void Measure(
        DisplayTarget target,
        Raster placeholder,
        SelectionMode mode,
        int iterations
    )
    {
        SelectionState state = new() { Mode = mode };
        OverlayLayer layer = new(
            target,
            placeholder.ToBitmapSource(),
            state,
            new List<WindowCandidate>(),
            runsToolbar: true,
            magnifierEnabled: true,
            recorderHint: "发送录屏组合键（内存测试不真发）"
        );

        if (mode == SelectionMode.Freeform)
        {
            // 两万个顶点：一个划得快的人能画出来的上限。
            for (int index = 0; index < 20000; index++)
            {
                state.AddFreeformPoint(
                    new Point32
                    {
                        X = 200 + (int)(Math.Sin(index / 40d) * 600),
                        Y = 200 + (int)(Math.Cos(index / 40d) * 400),
                    }
                );
            }
        }

        int warmup = Math.Min(32, iterations);
        for (int index = 0; index < warmup; index++)
        {
            Drag(state, layer, index);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < iterations; index++)
        {
            Drag(state, layer, index);
        }

        long perRefresh = (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
        Console.WriteLine(
            string.Format(
                CultureInfo.InvariantCulture,
                "[mem] refresh {0,-9} {1,8} B/次 × {2} 次 = {3} MB",
                mode,
                perRefresh,
                iterations,
                perRefresh * iterations / (1024 * 1024)
            )
        );
    }

    private static void Drag(SelectionState state, OverlayLayer layer, int step)
    {
        if (state.Mode == SelectionMode.Freeform)
        {
            state.Dragging = true;
        }

        state.UpdateRect(
            new Point32 { X = 100, Y = 100 },
            new Point32 { X = 200 + (step % 900), Y = 160 + (step % 520) }
        );

        layer.Refresh();
    }

    private static int ParseInt(string[] args, string name, int fallback)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (
                string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[index + 1], out int value)
            )
            {
                return value;
            }
        }

        return fallback;
    }
}
