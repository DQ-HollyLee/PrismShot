using System;
using System.Runtime.InteropServices;
using PrismShot.Core.Capture;
using PrismShot.Core.Imaging;

namespace PrismShot.Core.Diagnostics;

/// <summary>
/// 抓帧流水线的分段内存探测。<c>PrismShot.exe --pipetest</c>。
/// </summary>
/// <remarks>
/// 一次抓帧要占掉一整个 G 的时候，光知道「是它占的」没有用 ——
/// 这一路上有 D3D11 设备、WGC 的 capture item / frame pool / session、暂存纹理、
/// 回读缓冲、色调映射输出这么五六处，全都可能是嫌疑人。
/// 这个探测把每一步都停一下、记一笔，谁把内存撑起来一目了然。
/// </remarks>
internal static class CapturePipelineProbe
{
    public static int Run(string[] args)
    {
        int loops = ParseInt(args, "--count", 2);
        bool keepDevice = HasFlag(args, "--keep-device");
        bool gdiOnly = HasFlag(args, "--gdi");

        if (gdiOnly)
        {
            return RunGdi(loops);
        }

        if (HasFlag(args, "--devices"))
        {
            return RunDeviceLoop(loops);
        }

        if (HasFlag(args, "--d3dmin"))
        {
            return RunMinimalD3d();
        }

        DisplayTarget target = MonitorEnumerator.Enumerate()[0];
        bool asFloat = !HasFlag(args, "--sdr");
        Console.WriteLine($"display={target.DeviceName} {target.PixelWidth}x{target.PixelHeight} "
            + $"float={target.ShouldCaptureAsFloat} 本次按 {(asFloat ? "float16" : "8bit")} 抓");

        D3D11CaptureDevice? device = null;

        for (int loop = 1; loop <= loops; loop++)
        {
            Console.WriteLine($"---- loop {loop}/{loops} ----");
            Console.WriteLine(MemoryReport.Line("start"));

            if (device is null)
            {
                device = D3D11CaptureDevice.Create();
                Console.WriteLine(MemoryReport.Line("+ D3D11 device"));
            }

            var frame = WgcFrameGrabber
                .CaptureMonitorAsync(
                    device.Direct3DDevice,
                    target.MonitorHandle,
                    asFloat,
                    TimeSpan.FromSeconds(5)
                )
                .GetAwaiter()
                .GetResult();
            Console.WriteLine(MemoryReport.Line("+ frame(item/pool)"));

            IntPtr texture = WgcFrameGrabber.GetTexturePointer(frame.Surface);
            Console.WriteLine(MemoryReport.Line("+ texture ptr"));

            using (var gpuTexture = new Vortice.Direct3D11.ID3D11Texture2D(texture))
            {
                ReadbackTexture readback = device.Readback(gpuTexture);
                Console.WriteLine(MemoryReport.Line("+ readback"));

                Raster raster = ToneMapper.ToSdr(readback, target.SdrWhiteLevelNits);
                Console.WriteLine(MemoryReport.Line("+ tone map"));

                GC.KeepAlive(raster);
            }

            frame.Dispose();
            Console.WriteLine(MemoryReport.Line("+ frame disposed"));

            if (!keepDevice)
            {
                MemoryReport.CollectFully();
                Console.WriteLine(MemoryReport.Line("+ collect"));
            }
        }

        device?.Dispose();
        MemoryReport.CollectFully();
        Console.WriteLine(MemoryReport.Line("device disposed"));

        return 0;
    }

    /// <summary>
    /// 反复「建 D3D11 设备 → 释放」，看这笔内存到底还不还得回来。
    /// </summary>
    /// <remarks>
    /// 这是决定「能不能靠用完就放来压内存」的关键一问：
    /// 如果每轮都回到起点，那么把设备改成按次持有就能把常驻占用打回原形；
    /// 如果每轮都在涨，那说明涨的是驱动/DLL 级别的常驻，只能换个思路。
    /// </remarks>
    private static int RunDeviceLoop(int loops)
    {
        Console.WriteLine(MemoryReport.Line("start"));

        for (int loop = 1; loop <= loops; loop++)
        {
            using (D3D11CaptureDevice device = D3D11CaptureDevice.Create())
            {
                Console.WriteLine(MemoryReport.Line($"+ device {loop}"));
                GC.KeepAlive(device.Direct3DDevice);
            }

            MemoryReport.CollectFully();
            Console.WriteLine(MemoryReport.Line($"- device {loop}"));
        }

        return 0;
    }

    /// <summary>
    /// 用最少的参数建 D3D11 设备，和 <see cref="D3D11CaptureDevice"/> 的建法逐个特征对比。
    /// </summary>
    /// <remarks>
    /// 目的是回答一个很具体的问题：那几百 MB 到底是「这台机器上碰 D3D11 就得付」的固定成本，
    /// 还是我们自己的创建参数（特征级别数组、BGRA 支持位、再包成 WinRT 设备）
    /// 把驱动推到了某条更肥的路径上。两组参数各建一次，数字一比就知道了。
    /// </remarks>
    private static int RunMinimalD3d()
    {
        const uint BgraSupport = 0x20;

        Console.WriteLine(MemoryReport.Line("start"));

        Try("A 无特征级别数组 + flags=0", null, 0);
        Try("B 无特征级别数组 + BGRA", null, BgraSupport);
        Try("C 特征级别数组 + flags=0", Levels, 0);
        Try("D 特征级别数组 + BGRA（项目用的）", Levels, BgraSupport);

        MemoryReport.CollectFully();
        Console.WriteLine(MemoryReport.Line("都释放后"));
        return 0;

        void Try(string label, uint[]? levels, uint flags)
        {
            CreateDevice(levels, flags, out IntPtr device, out IntPtr context);
            Console.WriteLine(MemoryReport.Line(label));
            Release(device, context);
        }
    }

    private static readonly uint[] Levels = { 0xB100, 0xB000, 0xA100, 0xA000 };

    private static void CreateDevice(
        uint[]? levels,
        uint flags,
        out IntPtr device,
        out IntPtr context
    )
    {
        // 关键：levels 为 null 时必须传真正的空指针，不能传「指向空数组的指针」——
        // 后者会让 D3D11CreateDevice 直接返回 DXGI_ERROR_UNSUPPORTED，
        // 那样量到的就不是「建设备有多贵」，而是「建设备失败了」。
        if (levels is null || levels.Length == 0)
        {
            int hrPlain = D3D11CreateDevice(
                IntPtr.Zero,
                1,
                IntPtr.Zero,
                flags,
                IntPtr.Zero,
                0,
                7,
                out device,
                out _,
                out context
            );

            if (hrPlain < 0)
            {
                Console.WriteLine($"  建设备失败 0x{hrPlain:X8}");
            }

            return;
        }

        GCHandle pinned = GCHandle.Alloc(levels, GCHandleType.Pinned);
        try
        {
            int hr = D3D11CreateDevice(
                IntPtr.Zero,
                1, // D3D_DRIVER_TYPE_HARDWARE
                IntPtr.Zero,
                flags,
                pinned.AddrOfPinnedObject(),
                (uint)levels.Length,
                7,
                out device,
                out _,
                out context
            );

            if (hr < 0)
            {
                Console.WriteLine($"  建设备失败 0x{hr:X8}");
            }
        }
        finally
        {
            pinned.Free();
        }
    }

    private static void Release(IntPtr device, IntPtr context)
    {
        if (context != IntPtr.Zero)
        {
            Marshal.Release(context);
        }

        if (device != IntPtr.Zero)
        {
            Marshal.Release(device);
        }
    }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        IntPtr adapter,
        int driverType,
        IntPtr software,
        uint flags,
        IntPtr featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out IntPtr device,
        out int featureLevel,
        out IntPtr immediateContext
    );

    /// <summary>对照组：完全不走 D3D11 的 GDI 抓屏。</summary>
    private static int RunGdi(int loops)
    {
        DisplayTarget target = MonitorEnumerator.Enumerate()[0];
        for (int loop = 1; loop <= loops; loop++)
        {
            Raster? raster = GdiFrameGrabber.TryCapture(target.Bounds);
            Console.WriteLine(MemoryReport.Line($"+ gdi {loop}"));
            GC.KeepAlive(raster);
        }

        MemoryReport.CollectFully();
        Console.WriteLine(MemoryReport.Line("+ collect"));
        return 0;
    }

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
