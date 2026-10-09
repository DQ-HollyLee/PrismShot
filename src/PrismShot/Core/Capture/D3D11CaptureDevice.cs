using System;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Winrt = Windows.Graphics.DirectX.Direct3D11;

namespace PrismShot.Core.Capture;

/// <summary>一张从 GPU 回读到 CPU 的纹理。</summary>
/// <param name="Pixels">原始像素，可能带行尾对齐填充（所以必须同时看 <paramref name="RowPitch"/>）。</param>
internal readonly record struct ReadbackTexture(
    byte[] Pixels,
    int Width,
    int Height,
    int RowPitch,
    bool IsFloat16
);

/// <summary>
/// 为 WGC 服务的 D3D11 设备。
/// </summary>
/// <remarks>
/// 一个进程只建一台，长期持有：D3D11 设备创建要几十毫秒，每次截图重建完全是白白耽误按快门的手感。
/// 而它是纯粹的 GPU 句柄集合，常驻几乎不占什么内存 —— 真正吃内存的是每帧回读出来的那一大块字节，
/// 那部分用完立刻放手。
///
/// 这个类是整个 HDR 方案里唯一需要自己动手写 COM 的地方，三个动作：建设备、把 DXGI 设备包成
/// WinRT 设备交给 WGC、把 GPU 纹理拷到一张 CPU 可读的暂存纹理再读出来。
/// </remarks>
internal sealed class D3D11CaptureDevice : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIDevice _dxgiDevice;
    private readonly Winrt.IDirect3DDevice _winrtDevice;
    private bool _disposed;

    private D3D11CaptureDevice(ID3D11Device device, ID3D11DeviceContext context)
    {
        _device = device;
        _context = context;
        _dxgiDevice = device.QueryInterface<IDXGIDevice>();
        _winrtDevice = D3D11.CreateDirect3D11DeviceFromDXGIDevice<Winrt.IDirect3DDevice>(
            _dxgiDevice
        );
    }

    /// <summary>WGC 帧池要的那个东西。坐标是 WinRT，不是 Vortice。</summary>
    public Winrt.IDirect3DDevice Direct3DDevice => _winrtDevice;

    /// <summary>
    /// 建一台设备。失败会抛，调用方负责降级到 GDI。
    /// </summary>
    /// <param name="useWarp">
    /// 是否用 WARP 软件渲染兜底。某些远程桌面 / 虚拟机环境没有可用的硬件适配器，
    /// 但 WARP 跑 4K 回读慢得让人怀疑人生，所以只在硬件路径失败时用。
    /// </param>
    public static D3D11CaptureDevice Create(bool useWarp = false)
    {
        FeatureLevel[] levels =
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0,
        };

        Result result = D3D11.D3D11CreateDevice(
            IntPtr.Zero,
            useWarp ? DriverType.Warp : DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            levels,
            out ID3D11Device? device,
            out ID3D11DeviceContext? context
        );

        if (result.Failure || device is null || context is null)
        {
            throw new InvalidOperationException(
                $"D3D11CreateDevice 失败：0x{result.Code:X8}（{(useWarp ? "WARP" : "硬件")}）"
            );
        }

        return new D3D11CaptureDevice(device, context);
    }

    /// <summary>
    /// 把一张 GPU 纹理原样拷回 CPU。
    /// </summary>
    /// <param name="source">
    /// 已包装好的纹理对象。<b>ownership 由调用方持有</b>：Vortice 的指针构造函数不会自己 AddRef，
    /// 所以调用方把这一份引用计数原样移交进来，Dispose 时正好释放干净 —— 两边各加一次、
    /// 或各减一次，都会在几百次截图之后变成随机崩溃或显存泄漏，这里必须按这一条走。
    /// </param>
    public unsafe ReadbackTexture Readback(ID3D11Texture2D source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);

        Texture2DDescription description = source.Description;

        bool isFloat16 = description.Format == Format.R16G16B16A16_Float;
        int bytesPerPixel = isFloat16 ? 8 : 4;

        Texture2DDescription stagingDescription = new()
        {
            Width = description.Width,
            Height = description.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = description.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        };

        using ID3D11Texture2D staging = _device.CreateTexture2D(stagingDescription);
        _context.CopySubresourceRegion(staging, 0, 0, 0, 0, source, 0, null);

        int width = (int)description.Width;
        int height = (int)description.Height;
        MappedSubresource mapped = _context.Map(staging, 0u, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int rowPitch = (int)mapped.RowPitch;
            int usefulBytes = Math.Min(rowPitch, width * bytesPerPixel);
            byte[] buffer = new byte[(long)rowPitch * height];

            fixed (byte* destination = buffer)
            {
                byte* sourcePointer = (byte*)mapped.DataPointer;
                for (int row = 0; row < height; row++)
                {
                    Buffer.MemoryCopy(
                        sourcePointer + ((long)row * rowPitch),
                        destination + ((long)row * rowPitch),
                        rowPitch,
                        usefulBytes
                    );
                }
            }

            return new ReadbackTexture(buffer, width, height, rowPitch, isFloat16);
        }
        finally
        {
            _context.Unmap(staging, 0u);
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
            _winrtDevice.Dispose();
            _dxgiDevice.Dispose();
            _context.Dispose();
            _device.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug($"释放 D3D11 设备时的小异常：{ex.Message}");
        }
    }
}
