using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using PrismShot.Core.Imaging;
using PrismShot.Core.Interop;

namespace PrismShot.Core.Capture;

/// <summary>一块显示器抓到的成品。</summary>
internal sealed class DisplayCapture
{
    public DisplayCapture(DisplayTarget target, Raster image)
    {
        Target = target;
        Image = image;
    }

    public DisplayTarget Target { get; }

    /// <summary>已转成 8bit sRGB 的可直接显示 / 编码的图。</summary>
    public Raster Image { get; }

    /// <summary>这块屏上是否有真 HDR 内容。</summary>
    public bool HasHdrContent { get; set; }

    public Raster Crop(Rect32 regionInVirtualPixels)
    {
        int left = regionInVirtualPixels.Left - Target.Bounds.Left;
        int top = regionInVirtualPixels.Top - Target.Bounds.Top;
        return Image.Crop(left, top, regionInVirtualPixels.Width, regionInVirtualPixels.Height);
    }

    public Rect32 Intersect(Rect32 regionInVirtualPixels)
    {
        int left = Math.Max(regionInVirtualPixels.Left, Target.Bounds.Left);
        int top = Math.Max(regionInVirtualPixels.Top, Target.Bounds.Top);
        int right = Math.Min(regionInVirtualPixels.Right, Target.Bounds.Right);
        int bottom = Math.Min(regionInVirtualPixels.Bottom, Target.Bounds.Bottom);

        if (right <= left || bottom <= top)
        {
            return default;
        }

        return new Rect32 { Left = left, Top = top, Right = right, Bottom = bottom };
    }
}

/// <summary>一次「按下快门」抓到的所有显示器的画面。</summary>
internal sealed class CaptureBundle
{
    public CaptureBundle(List<DisplayCapture> captures, Rect32 virtualScreen, bool usedHdrPipeline)
    {
        Captures = captures;
        VirtualScreen = virtualScreen;
        UsedHdrPipeline = usedHdrPipeline;
    }

    public List<DisplayCapture> Captures { get; }

    public Rect32 VirtualScreen { get; }

    /// <summary>这一帧走的是 WGC 浮点路径（true）还是 GDI 兜底路径（false）。</summary>
    public bool UsedHdrPipeline { get; }

    public bool AnyHdrContent
    {
        get
        {
            foreach (DisplayCapture capture in Captures)
            {
                if (capture.HasHdrContent)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>把各屏拼成一张覆盖整个虚拟桌面的图。</summary>
    public Raster ComposeVirtualScreen()
    {
        int width = VirtualScreen.Width;
        int height = VirtualScreen.Height;
        Raster composite = Raster.CreateBgra32(width, height);

        foreach (DisplayCapture capture in Captures)
        {
            int left = capture.Target.Bounds.Left - VirtualScreen.Left;
            int top = capture.Target.Bounds.Top - VirtualScreen.Top;

            for (int row = 0; row < capture.Image.Height; row++)
            {
                int destinationRow = top + row;
                if ((uint)destinationRow >= (uint)height)
                {
                    continue;
                }

                Buffer.BlockCopy(
                    capture.Image.Pixels,
                    row * capture.Image.Stride,
                    composite.Pixels,
                    (destinationRow * composite.Stride) + (left * 4),
                    capture.Image.Width * 4
                );
            }
        }

        return composite;
    }

    /// <summary>按虚拟桌面坐标系裁一块。跨屏时自动拼接。</summary>
    public Raster Crop(Rect32 region)
    {
        Raster result = Raster.CreateBgra32(region.Width, region.Height);

        foreach (DisplayCapture capture in Captures)
        {
            Rect32 overlap = capture.Intersect(region);
            if (overlap.Width <= 0 || overlap.Height <= 0)
            {
                continue;
            }

            int sourceLeft = overlap.Left - capture.Target.Bounds.Left;
            int sourceTop = overlap.Top - capture.Target.Bounds.Top;
            int destinationLeft = overlap.Left - region.Left;
            int destinationTop = overlap.Top - region.Top;

            for (int row = 0; row < overlap.Height; row++)
            {
                Buffer.BlockCopy(
                    capture.Image.Pixels,
                    ((sourceTop + row) * capture.Image.Stride) + (sourceLeft * 4),
                    result.Pixels,
                    ((destinationTop + row) * result.Stride) + (destinationLeft * 4),
                    overlap.Width * 4
                );
            }
        }

        return result;
    }
}

/// <summary>
/// 抓帧总调度：先把每块屏的情况问清楚，再决定用哪条通道抓，最后统一转成 8bit sRGB。
/// </summary>
internal sealed class ScreenGrabber : IDisposable
{
    private readonly TimeSpan _perDisplayTimeout;
    private D3D11CaptureDevice? _device;
    private int _deviceCreationFailed;

    public ScreenGrabber(TimeSpan perDisplayTimeout)
    {
        _perDisplayTimeout = perDisplayTimeout;
    }

    /// <summary>D3D11 设备常驻。失败一次就永久转 GDI，别在每次按键时反复尝试。</summary>
    private D3D11CaptureDevice? EnsureDevice()
    {
        if (_deviceCreationFailed != 0)
        {
            return null;
        }

        if (_device is not null)
        {
            return _device;
        }

        try
        {
            _device = D3D11CaptureDevice.Create();
            return _device;
        }
        catch (Exception ex)
        {
            Log.Warn($"D3D11 硬件设备创建失败，尝试 WARP：{ex.Message}");
        }

        try
        {
            _device = D3D11CaptureDevice.Create(useWarp: true);
            Log.Info("D3D11 已降级到 WARP 软件渲染，抓帧会比较慢。");
            return _device;
        }
        catch (Exception ex)
        {
            Log.Error("D3D11 彻底不可用，本进程转为 GDI 兜底。", ex);
            Interlocked.Exchange(ref _deviceCreationFailed, 1);
            return null;
        }
    }

    public async Task<CaptureBundle> CaptureAsync(CancellationToken cancellationToken = default)
    {
        List<DisplayTarget> targets = MonitorEnumerator.Enumerate();
        Rect32 virtualScreen = MonitorEnumerator.VirtualScreen;

        D3D11CaptureDevice? device = EnsureDevice();
        if (device is null)
        {
            return CaptureByGdi(targets, virtualScreen);
        }

        Stopwatch watch = Stopwatch.StartNew();
        Task<DisplayCapture?>[] tasks = new Task<DisplayCapture?>[targets.Count];
        for (int index = 0; index < targets.Count; index++)
        {
            DisplayTarget target = targets[index];
            // 所有屏幕同时建立 session，一起等帧 —— 串行的话两屏会比较明显地看出
            // 「左边是现在右边是一秒前」的画面错位。
            tasks[index] = Task.Run(
                () => CaptureOneAsync(device, target, cancellationToken),
                cancellationToken
            );
        }

        DisplayCapture?[] results = await Task.WhenAll(tasks).ConfigureAwait(false);

        List<DisplayCapture> captures = new();
        foreach (DisplayCapture? result in results)
        {
            if (result is not null)
            {
                captures.Add(result);
            }
        }

        if (captures.Count == 0)
        {
            Log.Warn("WGC 一条屏都没抓到，整体降级 GDI。");
            return CaptureByGdi(targets, virtualScreen);
        }

        Log.Info(
            $"抓帧完成 {captures.Count}/{targets.Count} 块屏，用时 {watch.ElapsedMilliseconds} ms"
        );
        return new CaptureBundle(captures, virtualScreen, true);
    }

    private async Task<DisplayCapture?> CaptureOneAsync(
        D3D11CaptureDevice device,
        DisplayTarget target,
        CancellationToken cancellationToken
    )
    {
        try
        {
            Windows.Graphics.Capture.Direct3D11CaptureFrame frame =
                await WgcFrameGrabber
                    .CaptureMonitorAsync(
                        device.Direct3DDevice,
                        target.MonitorHandle,
                        target.ShouldCaptureAsFloat,
                        _perDisplayTimeout,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

            using (frame)
            {
                IntPtr texture = WgcFrameGrabber.GetTexturePointer(frame.Surface);
                if (texture == IntPtr.Zero)
                {
                    Log.Warn($"{target.DeviceName}：取不到纹理指针。");
                    return FallbackOne(target);
                }

                using var gpuTexture = new Vortice.Direct3D11.ID3D11Texture2D(texture);
                ReadbackTexture readback = device.Readback(gpuTexture);

                bool hasHdrContent =
                    readback.IsFloat16
                    && ToneMapper.HasRealHdrContent(readback, target.SdrWhiteLevelNits);

                Raster raster = ToneMapper.ToSdr(readback, target.SdrWhiteLevelNits);
                DisplayCapture capture = new(target, raster) { HasHdrContent = hasHdrContent };

                if (target.IsHdrEnabled && hasHdrContent)
                {
                    Log.Info(
                        $"{target.DeviceName}：确认有真 HDR 内容（白电平 {target.SdrWhiteLevelNits:0} 尼特）。"
                    );
                }

                return capture;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"{target.DeviceName}：WGC 抓帧失败，改用 GDI。 {ex.Message}");
            return FallbackOne(target);
        }
    }

    private DisplayCapture? FallbackOne(DisplayTarget target)
    {
        Raster? raster = GdiFrameGrabber.TryCapture(target.Bounds);
        return raster is null ? null : new DisplayCapture(target, raster);
    }

    private CaptureBundle CaptureByGdi(List<DisplayTarget> targets, Rect32 virtualScreen)
    {
        List<DisplayCapture> captures = new();
        foreach (DisplayTarget target in targets)
        {
            Raster? raster = GdiFrameGrabber.TryCapture(target.Bounds);
            if (raster is not null)
            {
                captures.Add(new DisplayCapture(target, raster));
            }
        }

        if (captures.Count == 0)
        {
            // 最后一根稻草：整体虚拟桌面一次性抓。
            Raster? whole = GdiFrameGrabber.TryCapture(virtualScreen);
            if (whole is not null)
            {
                captures.Add(
                    new DisplayCapture(
                        new DisplayTarget
                        {
                            MonitorHandle = IntPtr.Zero,
                            DeviceName = "GDI-VIRTUAL",
                            Bounds = virtualScreen,
                            IsPrimary = true,
                            Scale = 1.0,
                        },
                        whole
                    )
                );
            }
        }

        return new CaptureBundle(captures, virtualScreen, false);
    }

    public static void WarmUpJit()
    {
        // 首帧的 JIT + WinRT 激活开销会让人觉得「按了慢半拍」，趁还在启动阶段预热一下。
        _ = Task.Run(() =>
        {
            try
            {
                _ = Srgb.EncodeToByte(0.5f);
            }
            catch
            {
                // 预热而已，出错无所谓。
            }
        });
    }

    public void Dispose()
    {
        _device?.Dispose();
        _device = null;
    }
}
