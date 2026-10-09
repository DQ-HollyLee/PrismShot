using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PrismShot.Core;
using PrismShot.Core.Capture;
using PrismShot.Core.Platform;

namespace PrismShot.Ui;

/// <summary>
/// 一次冻结帧选取的宿主：把每块显示器上的 <see cref="OverlayLayer"/> 拉起来，
/// 统一拿到结果后一起收掉。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要这么一个中转层而不是让 <c>OverlayLayer</c> 自己管自己：
/// 多屏时任意一张都能收到「回车确认」，谁去关谁就成了竞态。
/// 这里用一份 <c>TaskCompletionSource</c> 做裁判，第一个先到的给结果，其余窗口统一被关，
/// 彼此之间不互相 Dispose —— 那会让 WPF 在窗口还在收焦时就被拆掉，是偶发崩溃的老源头。
/// </para>
/// </remarks>
internal sealed class OverlayHost
{
    private readonly List<OverlayLayer> _layers = new();
    private TaskCompletionSource<OverlayResult>? _completion;
    private KeyboardHook? _hook;
    private readonly AppSettings _settings;

    /// <summary>用户点了覆盖层上的录屏按钮。</summary>
    public event Action? RecorderRequested;

    public OverlayHost(AppSettings settings) => _settings = settings;

    public Task<OverlayResult> ShowAsync(CaptureBundle bundle, SelectionMode mode, KeyboardHook hook)
    {
        _hook = hook;
        TaskCompletionSource<OverlayResult> completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        _completion = completion;

        List<WindowCandidate> windows = WindowEnumerator.Enumerate(MonitorEnumerator.VirtualScreen);

        DisplayTarget? primary =
            bundle.Captures.Count > 0 ? bundle.Captures[0].Target : null;

        foreach (DisplayCapture capture in bundle.Captures)
        {
            bool toolbarHere =
                capture.Target.IsPrimary
                || (primary is null && ReferenceEquals(capture, bundle.Captures[0]));

            OverlayLayer layer = new(
                capture.Target,
                capture.Image.ToBitmapSource(),
                new SelectionState { Mode = mode },
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
        }

        if (_settings.Behavior.OverlayDimBackground == false)
        {
            foreach (OverlayLayer layer in _layers)
            {
                layer.SetDimAmount(0);
            }
        }

        _hook.SetOverlayKeys(true);
        _hook.KeyObserved += OnGlobalKey;

        return completion.Task;
    }

    private void OnRecorderRequested(string source) => RecorderRequested?.Invoke();

    private void OnLayerCompleted(OverlayResult result)
    {
        TaskCompletionSource<OverlayResult>? completion = _completion;
        if (completion is not null)
        {
            completion.TrySetResult(result);
        }

        Close();
    }

    /// <summary>
    /// 覆盖层期间的键盘。走钩子进来而不是 WPF 的 <c>KeyDown</c> ——
    /// 这几层窗口刻意不抢焦点，WPF 根本收不到键。
    /// </summary>
    private void OnGlobalKey(uint virtualKey, HotkeyModifiers modifiers)
    {
        const uint VirtualKeyEscape = 0x1B;
        const uint VirtualKeyReturn = 0x0D;
        const uint VirtualKey1 = 0x31;
        const uint VirtualKey2 = 0x32;
        const uint VirtualKey3 = 0x33;
        const uint VirtualKey4 = 0x34;

        SelectionMode? mode = virtualKey switch
        {
            VirtualKey1 => SelectionMode.Rect,
            VirtualKey2 => SelectionMode.Freeform,
            VirtualKey3 => SelectionMode.Window,
            VirtualKey4 => SelectionMode.FullScreen,
            _ => null,
        };

        if (mode.HasValue)
        {
            ApplyModeToAll(mode.Value);
            return;
        }

        if (virtualKey == VirtualKeyEscape)
        {
            OnLayerCompleted(OverlayResult.Cancelled());
            return;
        }

        if (virtualKey == VirtualKeyReturn)
        {
            OnLayerCompleted(AcceptWorking(giveSelectionElseWholeVirtualScreen: true));
        }
    }

    /// <summary>交给任一还没结束的层去算结果。多屏时任选一层都能给出全虚拟桌面的矩形。</summary>
    private OverlayResult AcceptWorking(bool giveSelectionElseWholeVirtualScreen)
    {
        foreach (OverlayLayer layer in _layers)
        {
            OverlayResult candidate = layer.AcceptCurrent();
            if (!candidate.Canceled)
            {
                return candidate;
            }
        }

        return giveSelectionElseWholeVirtualScreen
            ? OverlayResult.FromRect(MonitorEnumerator.VirtualScreen)
            : OverlayResult.Cancelled();
    }

    private void ApplyModeToAll(SelectionMode mode)
    {
        foreach (OverlayLayer layer in _layers)
        {
            layer.ApplyModePublic(mode);
        }
    }

    /// <summary>拆掉所有层并交还键盘。可以安全地重复调用。</summary>
    public void Close()
    {
        TaskCompletionSource<OverlayResult>? completion = _completion;
        _completion = null;

        if (_hook is not null)
        {
            _hook.KeyObserved -= OnGlobalKey;
            _hook.SetOverlayKeys(false);
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

        if (completion is not null && !completion.Task.IsCompleted)
        {
            completion.TrySetResult(OverlayResult.Cancelled());
        }
    }
}
