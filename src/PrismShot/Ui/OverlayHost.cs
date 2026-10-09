using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Threading;
using PrismShot.Core;
using PrismShot.Core.Capture;
using PrismShot.Core.Platform;

namespace PrismShot.Ui;

/// <summary>
/// 一次冻结帧选取的宿主：把每块显示器上的 <see cref="OverlayLayer"/> 拉起来，
/// 拿到结果后一起收掉。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要这么一个中转层：多屏时任意一张窗口都可能收到「回车确认」，谁去关谁就成了竞态。
/// 这里用一份 <c>TaskCompletionSource</c> 做裁判，第一个先到的给结果，其余窗口统一被关，
/// 彼此之间不互相 Dispose。
/// </para>
/// <para>
/// <b>所有对 WPF 对象的操作都只在 UI 线程上做。</b>
/// 键盘那条路是从低级钩子的线程池回调进来的（<see cref="KeyboardHook.KeyObserved"/>），
/// 直接在里面调 <c>Window.Close()</c> 会抛「调用线程无法访问此对象」——
/// 更糟的是异常被吞掉之后窗口还留在屏幕上，用户会觉得「按了 Esc 但蒙版不消失」。
/// 所以入口处统一切回 UI 线程。
/// </para>
/// </remarks>
internal sealed class OverlayHost
{
    private readonly List<OverlayLayer> _layers = new();
    private readonly AppSettings _settings;
    private readonly object _gate = new();

    private Dispatcher? _ui;
    private TaskCompletionSource<OverlayResult>? _completion;
    private KeyboardHook? _hook;
    private bool _closed;

    /// <summary>用户点了覆盖层上的录屏按钮。</summary>
    public event Action? RecorderRequested;

    public OverlayHost(AppSettings settings) => _settings = settings;

    /// <summary>必须在 UI 线程上调用。</summary>
    public Task<OverlayResult> ShowAsync(
        CaptureBundle bundle,
        SelectionMode mode,
        KeyboardHook hook
    )
    {
        _ui = Dispatcher.CurrentDispatcher;
        _closed = false;

        TaskCompletionSource<OverlayResult> completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        _completion = completion;

        List<WindowCandidate> windows = WindowEnumerator.Enumerate(MonitorEnumerator.VirtualScreen);
        SelectionState state = new() { Mode = mode };

        for (int index = 0; index < bundle.Captures.Count; index++)
        {
            DisplayCapture capture = bundle.Captures[index];

            // 工具栏只出现在主屏那一张上；主屏没被标出来时（虚拟机上偶发）让第一张顶上，
            // 否则用户会看到一张没有工具栏的蒙版，无从下手。
            bool toolbarHere = capture.Target.IsPrimary || index == 0;

            OverlayLayer layer = new(
                capture.Target,
                capture.Image.ToBitmapSource(),
                state,
                windows,
                toolbarHere,
                _settings.Behavior.OverlayMagnifier
            );

            layer.Completed += OnLayerCompleted;
            layer.RecorderRequested += OnRecorderRequested;
            _layers.Add(layer);
        }

        if (_layers.Count == 0)
        {
            completion.TrySetResult(OverlayResult.Cancelled());
            return completion.Task;
        }

        foreach (OverlayLayer layer in _layers)
        {
            layer.Show();

            if (!_settings.Behavior.OverlayDimBackground)
            {
                layer.SetDimAmount(0);
            }
        }

        hook.SetOverlayKeys(true);
        hook.KeyObserved += OnGlobalKey;
        _hook = hook;

        return completion.Task;
    }

    private void OnRecorderRequested(string source) => RecorderRequested?.Invoke();

    private void OnLayerCompleted(OverlayResult result)
    {
        if (!OnUiThread())
        {
            _ui?.BeginInvoke((Action)(() => OnLayerCompleted(result)));
            return;
        }

        TaskCompletionSource<OverlayResult>? completion = _completion;
        completion?.TrySetResult(result);
        Close();
    }

    /// <summary>
    /// 覆盖层期间的键盘。走钩子进来而不是 WPF 的 <c>KeyDown</c> ——
    /// 这几层窗口刻意不抢焦点，WPF 根本收不到键。
    /// </summary>
    private void OnGlobalKey(uint virtualKey, HotkeyModifiers modifiers)
    {
        // 这个回调来自钩子线程，必须切回 UI 线程再碰任何窗口。
        _ui?.BeginInvoke((Action)(() => HandleKey(virtualKey)));
    }

    private void HandleKey(uint virtualKey)
    {
        const uint VirtualKeyEscape = 0x1B;
        const uint VirtualKeyReturn = 0x0D;

        SelectionMode? mode = virtualKey switch
        {
            0x31 => SelectionMode.Rect,
            0x32 => SelectionMode.Freeform,
            0x33 => SelectionMode.Window,
            0x34 => SelectionMode.FullScreen,
            _ => null,
        };

        if (mode.HasValue)
        {
            foreach (OverlayLayer layer in _layers)
            {
                layer.ApplyModePublic(mode.Value);
            }

            return;
        }

        if (virtualKey == VirtualKeyEscape)
        {
            OnLayerCompleted(OverlayResult.Cancelled());
            return;
        }

        if (virtualKey == VirtualKeyReturn)
        {
            OnLayerCompleted(AcceptWorking());
        }
    }

    /// <summary>任取一张还没结束的层去算结果；实在没有就交整个虚拟桌面。</summary>
    private OverlayResult AcceptWorking()
    {
        foreach (OverlayLayer layer in _layers)
        {
            OverlayResult candidate = layer.AcceptCurrent();
            if (!candidate.Canceled)
            {
                return candidate;
            }
        }

        return OverlayResult.FromRect(MonitorEnumerator.VirtualScreen);
    }

    /// <summary>拆掉所有层并交还键盘。可重复调用。</summary>
    public void Close()
    {
        if (!OnUiThread())
        {
            _ui?.BeginInvoke((Action)Close);
            return;
        }

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
        }

        TaskCompletionSource<OverlayResult>? completion = _completion;
        _completion = null;

        KeyboardHook? hook = _hook;
        if (hook is not null)
        {
            hook.KeyObserved -= OnGlobalKey;
            hook.SetOverlayKeys(false);
            _hook = null;
        }

        foreach (OverlayLayer layer in _layers)
        {
            layer.RecorderRequested -= OnRecorderRequested;
            layer.Completed -= OnLayerCompleted;
            try
            {
                layer.Close();
            }
            catch (Exception ex)
            {
                Log.Debug($"关闭覆盖层时忽略异常：{ex.Message}");
            }
        }

        _layers.Clear();

        // 关闭也可能发生在拿到结果之前（窗口被系统收走之类），
        // 这时必须补一个「取消」，否则调用方会永远等下去。
        if (completion is not null && !completion.Task.IsCompleted)
        {
            completion.TrySetResult(OverlayResult.Cancelled());
        }
    }

    private bool OnUiThread() => _ui is null || _ui.CheckAccess();
}
