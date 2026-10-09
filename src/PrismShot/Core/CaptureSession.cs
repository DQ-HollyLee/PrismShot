using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using PrismShot.Core.Capture;
using PrismShot.Core.Imaging;
using PrismShot.Core.Interop;
using PrismShot.Core.Ipc;
using PrismShot.Core.Platform;
using PrismShot.Ui;
using IoDirectory = System.IO.Directory;

namespace PrismShot.Core;

/// <summary>
/// 一次完整截图动作：抓帧 → 选取 → 裁剪 → 编码 → 落盘 / 剪贴板 → 通知。
/// </summary>
/// <remarks>
/// 这里刻意把工程上的每一环写成独立的一步，而不是塞进一个三百行的方法：
/// 用户来报「截图偏色」「文件没保存」的时候，要能一眼看出是哪一环出了问题，
/// 而不是在一段长流水里比谁先谁后。
/// </remarks>
internal sealed class CaptureSession : IDisposable
{
    private readonly AppSettings _settings;
    private readonly KeyboardHook _hook;
    private readonly Dispatcher _ui;
    private ScreenGrabber? _grabber;
    private Rect32 _lastRegion;
    private bool _hasLastRegion;
    private bool _disposed;

    public CaptureSession(AppSettings settings, KeyboardHook hook, Dispatcher ui)
    {
        _settings = settings;
        _hook = hook;
        _ui = ui;
    }

    /// <summary>按请求走一遍完整流程。</summary>
    /// <param name="request">来自热键、管道或命令行的统一请求。</param>
    /// <param name="mode">已经翻译好的选取方式。</param>
    public async Task<IpcResponse> ExecuteAsync(IpcRequest request, SelectionMode mode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        CaptureBundle? bundle = await GrabAsync().ConfigureAwait(false);
        if (bundle is null)
        {
            return IpcResponse.Failed("抓帧失败：一块屏都没抓到，通常是显卡驱动拒绝了 WGC 会话。");
        }

        if (mode == SelectionMode.FullScreen)
        {
            return await FinishAsync(bundle, ResolveFullScreenScope(bundle), null, request)
                .ConfigureAwait(false);
        }

        OverlayResult picked = await PickRegionAsync(bundle, mode).ConfigureAwait(false);
        if (picked.Canceled || picked.Width <= 0 || picked.Height <= 0)
        {
            return IpcResponse.Cancelled();
        }

        return await FinishAsync(bundle, picked.Region, picked.Polygon, request)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 按给定的矩形直接抓一张，不弹覆盖层。「重复上次选区」走这条。
    /// </summary>
    public async Task<IpcResponse> ExecuteAtAsync(Rect32 region, IpcRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (region.Width <= 0 || region.Height <= 0)
        {
            return IpcResponse.Failed("上一次的选区无效。");
        }

        CaptureBundle? bundle = await GrabAsync().ConfigureAwait(false);
        if (bundle is null)
        {
            return IpcResponse.Failed("抓帧失败，详见日志。");
        }

        return await FinishAsync(bundle, region, null, request).ConfigureAwait(false);
    }

    /// <summary>抓帧放在线程池线程上：多屏并行要等帧，占住 UI 线程会让覆盖层僵住。</summary>
    private async Task<CaptureBundle?> GrabAsync()
    {
        try
        {
            ScreenGrabber grabber =
                _grabber ??= new ScreenGrabber(
                    TimeSpan.FromSeconds(_settings.Behavior.PerDisplayTimeoutSeconds)
                );

            CaptureBundle bundle = await grabber.CaptureAsync().ConfigureAwait(false);
            return bundle.Captures.Count == 0 ? null : bundle;
        }
        catch (Exception ex)
        {
            Log.Error("抓帧失败。", ex);
            return null;
        }
    }

    /// <summary>裁剪 → 编码 → 落盘 / 剪贴板 → 通知。</summary>
    private async Task<IpcResponse> FinishAsync(
        CaptureBundle bundle,
        Rect32 region,
        List<Point32>? polygon,
        IpcRequest request
    )
    {
        _lastRegion = region;
        _hasLastRegion = true;

        Raster raster;
        try
        {
            raster = bundle.Crop(region);
        }
        catch (Exception ex)
        {
            Log.Error("裁剪失败。", ex);
            return IpcResponse.Failed($"裁剪失败：{ex.Message}");
        }

        ImageOutputFormat format = ImageWriter.Parse(request.Format ?? _settings.Output.Format);
        int quality = request.Quality > 0 ? request.Quality : _settings.Output.JpegQuality;

        // 圈选出来的不规则形状，只有带 alpha 通道的格式才装得下。
        // 而默认格式是 JPG（为了 OCR 顺手），它偏偏没有 alpha ——
        // 照原样存的话，用户圈了半天存出来的还是一整张外接矩形，等于白圈。
        // 所以这里一律升级成 PNG：「圈了个形状」是用户明确表达过的意图，比默认格式优先。
        if (polygon is { Count: >= 3 })
        {
            if (!SupportsAlpha(format))
            {
                Log.Info($"圈选结果需要透明通道，输出格式已由 {format} 升级为 PNG。");
                format = ImageOutputFormat.Png;
            }

            raster.ApplyPolygonAlpha(polygon, region.Left, region.Top);
        }

        string? savedPath = null;
        if (_settings.Output.SaveToFile && !request.NoSave)
        {
            savedPath = SaveToDisk(raster, request, format, quality);
            if (savedPath is null)
            {
                return IpcResponse.Failed("保存文件失败，详见日志。");
            }
        }

        bool clipboardDone = false;
        if (_settings.Output.CopyToClipboard && !request.SkipClipboard)
        {
            clipboardDone = await PushClipboardAsync(raster, savedPath).ConfigureAwait(false);
        }

        Log.Info(
            $"截图完成 {region.Width}x{region.Height} "
                + $"通道={(bundle.UsedHdrPipeline ? "WGC" : "GDI")} -> {savedPath ?? "(仅剪贴板)"}"
        );

        if (_settings.Behavior.ShowThumbnail)
        {
            ShowToast(raster, savedPath, clipboardDone);
        }

        long bytes =
            savedPath is not null && File.Exists(savedPath)
                ? new FileInfo(savedPath).Length
                : 0;

        return IpcResponse.Success(
            savedPath,
            region.Width,
            region.Height,
            bundle.UsedHdrPipeline ? "wgc" : "gdi",
            bundle.AnyHdrContent,
            bytes
        );
    }

    /// <summary>
    /// 整屏时的范围。virtual = 所有显示器拼起来的虚拟桌面；cursor = 光标所在那一块。
    /// </summary>
    private Rect32 ResolveFullScreenScope(CaptureBundle bundle)
    {
        if (
            string.Equals(
                _settings.Behavior.FullScreenScope,
                "cursor",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            if (bundle.Captures.Count == 1)
            {
                return bundle.Captures[0].Target.Bounds;
            }

            if (Win32.GetCursorPos(out Point32 cursor))
            {
                foreach (DisplayCapture capture in bundle.Captures)
                {
                    Rect32 bounds = capture.Target.Bounds;
                    if (
                        cursor.X >= bounds.Left
                        && cursor.X < bounds.Right
                        && cursor.Y >= bounds.Top
                        && cursor.Y < bounds.Bottom
                    )
                    {
                        return bounds;
                    }
                }
            }
        }

        return bundle.VirtualScreen;
    }

    /// <summary>把冻结帧铺到各显示器上等用户选。必须在 UI 线程执行。</summary>
    private Task<OverlayResult> PickRegionAsync(CaptureBundle bundle, SelectionMode mode)
    {
        TaskCompletionSource<OverlayResult> bridge = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        _ = _ui.BeginInvoke(
            (Action)(async () =>
            {
                OverlayHost host = new(_settings);
                host.RecorderRequested += () => RecorderMacro.Fire(_settings);
                try
                {
                    OverlayResult picked = await host
                        .ShowAsync(bundle, mode, _hook)
                        .ConfigureAwait(true);
                    bridge.TrySetResult(picked);
                }
                catch (Exception ex)
                {
                    Log.Error("覆盖层选取失败。", ex);
                    bridge.TrySetResult(OverlayResult.Cancelled());
                }
                finally
                {
                    host.Close();
                }
            })
        );

        return bridge.Task;
    }

    private string? SaveToDisk(
        Raster raster,
        IpcRequest request,
        ImageOutputFormat format,
        int quality
    )
    {
        try
        {
            string directory;
            string name;

            if (string.IsNullOrWhiteSpace(request.OutputPath))
            {
                directory = IoDirectory.CreateDirectory(ResolveOutputDirectory()).FullName;
                name = ExpandPattern(
                    _settings.Output.FileNamePattern,
                    ImageWriter.ExtensionOf(format)
                );
            }
            else
            {
                string full = Environment.ExpandEnvironmentVariables(request.OutputPath);
                directory = IoDirectory.CreateDirectory(Path.GetDirectoryName(full) ?? ".").FullName;
                name = Path.GetFileName(full);
                if (string.IsNullOrEmpty(name))
                {
                    name = ExpandPattern(
                        _settings.Output.FileNamePattern,
                        ImageWriter.ExtensionOf(format)
                    );
                }
            }

            return ImageWriter.Save(raster, Path.Combine(directory, name), quality);
        }
        catch (Exception ex)
        {
            Log.Error("写盘失败。", ex);
            return null;
        }
    }

    private string ResolveOutputDirectory()
    {
        string configured = _settings.Output.Directory;
        return string.IsNullOrWhiteSpace(configured)
            ? AppPaths.DefaultScreenshotDirectory
            : Environment.ExpandEnvironmentVariables(configured);
    }

    /// <summary>文件名模板：<c>{date}</c>、<c>{time}</c>、<c>{ms}</c>、<c>{yyyy}</c>。</summary>
    private static string ExpandPattern(string pattern, string extension)
    {
        DateTime now = DateTime.Now;
        string filled = pattern
            .Replace("{date}", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Replace("{time}", now.ToString("HH-mm-ss", CultureInfo.InvariantCulture))
            .Replace("{ms}", now.ToString("fff", CultureInfo.InvariantCulture))
            .Replace("{yyyy}", now.Year.ToString(CultureInfo.InvariantCulture));

        return $"{MakeFileNameSafe(filled)}.{extension.TrimStart('.')}";
    }

    private static string MakeFileNameSafe(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        char[] buffer = value.ToCharArray();
        for (int index = 0; index < buffer.Length; index++)
        {
            if (Array.IndexOf(invalid, buffer[index]) >= 0)
            {
                buffer[index] = '-';
            }
        }

        return new string(buffer);
    }

    private Task<bool> PushClipboardAsync(Raster raster, string? savedPath)
    {
        bool asFile = _settings.Output.ClipboardAsFile;
        return Task.Run(() =>
        {
            try
            {
                return asFile && savedPath is not null
                    ? ClipboardWriter.TrySetFile(savedPath)
                    : ClipboardWriter.TrySetBitmap(raster);
            }
            catch (Exception ex)
            {
                Log.Error("写剪贴板失败。", ex);
                return false;
            }
        });
    }

    private void ShowToast(Raster raster, string? savedPath, bool clipboardDone)
    {
        _ = _ui.BeginInvoke(
            (Action)(() =>
            {
                try
                {
                    string summary =
                        savedPath is not null
                            ? Path.GetFileName(savedPath)
                            : clipboardDone
                                ? "已复制到剪贴板"
                                : "未完成";

                    ResultToast toast = new(
                        raster,
                        summary,
                        savedPath,
                        _settings.Behavior.ThumbnailSeconds
                    );
                    toast.Show();
                }
                catch (Exception ex)
                {
                    Log.Error("显示结果通知失败。", ex);
                }
            })
        );
    }

    private static bool SupportsAlpha(ImageOutputFormat format) =>
        format is ImageOutputFormat.Png or ImageOutputFormat.Tiff;

    /// <summary>上一次的选区。供「重复上次选区」热键使用。</summary>
    public bool TryGetLastRegion(out Rect32 region)
    {
        region = _lastRegion;
        return _hasLastRegion;
    }

    /// <summary>
    /// 预热。第一次按快门会慢几百毫秒，其中相当一部分是 JIT 在翻译抓帧这条路径；
    /// 开机后先把这笔时间花掉，用户按下键那一刻才是真的「即刻」。
    /// </summary>
    public void Prewarm()
    {
        _ = Task.Run(() =>
        {
            try
            {
                ScreenGrabber.WarmUpJit();
                _grabber ??= new ScreenGrabber(
                    TimeSpan.FromSeconds(_settings.Behavior.PerDisplayTimeoutSeconds)
                );
            }
            catch (Exception ex)
            {
                Log.Debug($"预热失败（不影响使用）：{ex.Message}");
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _grabber?.Dispose();
        _grabber = null;
    }
}
