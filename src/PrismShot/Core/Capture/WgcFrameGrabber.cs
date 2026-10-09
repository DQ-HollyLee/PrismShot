using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Winrt = Windows.Graphics.DirectX.Direct3D11;

namespace PrismShot.Core.Capture;

/// <summary>
/// Windows Graphics Capture 的单帧抓取。
/// </summary>
/// <remarks>
/// 这是整个项目能截对 HDR 的唯一原因，值得把话说清楚：
/// <para>
/// DWM 内部合成用的是浮点 scRGB，高光能超过 1.0（对应上千尼特）。而 GDI 是 8bit SDR 时代的接口，
/// 为了兼容，DWM 会把合成结果往 8bit 塞一份给 GDI —— 任何走 <c>BitBlt</c> /
/// <c>Graphics.CopyFromScreen</c> 的截图工具拿到的都是这份降级副本，也就是用户在不同地方吐槽的那种「严重过曝」。
/// WGC 则直接把手伸进 DWM 的输出层，能要到原始的 <c>R16G16B16A16Float</c>。
/// </para>
/// <para>
/// 思路参考Starshot（同为 MIT），但那边对 Win10 的兼容处理在本项目里被简化成「直接判定不支持 float」，
/// 因为 Win10 的 WGC 本来也给不出浮点帧，多留一套分支只是多一处出错的面。
/// </para>
/// </remarks>
internal static class WgcFrameGrabber
{
    /// <summary>GraphicsCaptureItem 的 COM 类 GUID。走 interop 创建 item 时必须原样带上。</summary>
    private static readonly Guid GraphicsCaptureItemClassId = new(
        "79C3F95B-31F7-4EC2-A464-632EF5D30760"
    );

    private static readonly Guid Texture2DInterfaceId = new(
        "6F15AAF2-D208-4E89-9AB4-489535D34F9C"
    );

    private static readonly bool HasTryCreateFromDisplayId =
        Windows.Foundation.Metadata.ApiInformation.IsMethodPresent(
            "Windows.Graphics.Capture.GraphicsCaptureItem",
            "TryCreateFromDisplayId"
        );

    private static readonly bool HasIsBorderRequired =
        Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent(
            "Windows.Graphics.Capture.GraphicsCaptureSession",
            "IsBorderRequired"
        );

    private static readonly bool HasIsCursorCaptureEnabled =
        Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent(
            "Windows.Graphics.Capture.GraphicsCaptureSession",
            "IsCursorCaptureEnabled"
        );

    /// <summary>抓一帧指定显示器的画面。</summary>
    /// <param name="floatFormat">
    /// true 要求 <c>R16G16B16A16Float</c>。只在 Win11 及以上管用；
    /// Win10 上这条请求会被忽略并退回 8bit，所以上层要先按系统版本判断要不要问。
    /// </param>
    /// <returns>未 Dispose 的帧。调用方必须在取完纹理后 Dispose。</returns>
    public static async Task<Direct3D11CaptureFrame> CaptureMonitorAsync(
        Winrt.IDirect3DDevice device,
        IntPtr monitorHandle,
        bool floatFormat,
        TimeSpan timeout,
        CancellationToken cancellationToken = default
    )
    {
        GraphicsCaptureItem item = CreateItemForMonitor(monitorHandle);

        DirectXPixelFormat pixelFormat = floatFormat
            ? DirectXPixelFormat.R16G16B16A16Float
            : DirectXPixelFormat.R8G8B8A8UIntNormalized;

        // 必须 CreateFreeThreaded：多线程并行抓多屏时发起方是线程池线程，
        // 没有 DispatcherQueue，普通 Create 的回调永远不会来。
        using Direct3D11CaptureFramePool framePool =
            Direct3D11CaptureFramePool.CreateFreeThreaded(
                device,
                pixelFormat,
                1,
                item.Size
            );
        using GraphicsCaptureSession session = framePool.CreateCaptureSession(item);

        if (HasIsBorderRequired)
        {
            session.IsBorderRequired = false;
        }

        if (HasIsCursorCaptureEnabled)
        {
            session.IsCursorCaptureEnabled = false;
        }

        TaskCompletionSource<Direct3D11CaptureFrame> completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        using CancellationTokenSource timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        CancellationTokenRegistration cancel = cancellationToken.Register(() =>
            completion.TrySetCanceled()
        );
        CancellationTokenRegistration timeoutCancel = timeoutSource.Token.Register(() =>
            completion.TrySetException(new TimeoutException($"WGC 抓帧超时（{timeout}）"))
        );

        void OnFrameArrived(Direct3D11CaptureFramePool sender, object arguments)
        {
            if (sender.TryGetNextFrame() is Direct3D11CaptureFrame frame)
            {
                // 只要第一帧，会话留着也没用，立刻停掉还能顺手降低 GPU 负担。
                session.Dispose();
                completion.TrySetResult(frame);
            }
        }

        framePool.FrameArrived += OnFrameArrived;
        try
        {
            session.StartCapture();
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            framePool.FrameArrived -= OnFrameArrived;
            await cancel.DisposeAsync().ConfigureAwait(false);
            await timeoutCancel.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static GraphicsCaptureItem CreateItemForMonitor(IntPtr monitorHandle)
    {
        if (HasTryCreateFromDisplayId)
        {
            return GraphicsCaptureItem.TryCreateFromDisplayId(
                new DisplayId((ulong)monitorHandle)
            );
        }

        IntPtr abi = GraphicsCaptureItem
            .As<IGraphicsCaptureItemInterop>()
            .CreateForMonitor(monitorHandle, GraphicsCaptureItemClassId);
        return GraphicsCaptureItem.FromAbi(abi);
    }

    /// <summary>
    /// 从 WinRT 的 <c>IDirect3DSurface</c> 里取出底层的 <c>ID3D11Texture2D</c> 指针。
    /// </summary>
    /// <remarks>
    /// 这一步绕不过：拿到 surface 不等于拿到像素，还要走「拷到暂存纹理 + Map」这一趟才能读到 CPU。
    /// 接口 GUID 取自 Windows SDK 的 <c>windows.graphics.directx.direct3d11.interop.h</c>。
    /// </remarks>
    public static IntPtr GetTexturePointer(Winrt.IDirect3DSurface surface)
    {
        IntPtr unknown = Marshal.GetIUnknownForObject(surface);
        try
        {
            Guid accessInterfaceId = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
            Marshal.QueryInterface(unknown, ref accessInterfaceId, out IntPtr access);
            try
            {
                var dxgiAccess =
                    (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(access);
                return dxgiAccess.GetInterface(Texture2DInterfaceId);
            }
            finally
            {
                Marshal.Release(access);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, in Guid riid);

        IntPtr CreateForMonitor(IntPtr monitor, in Guid riid);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface(in Guid riid);
    }
}
