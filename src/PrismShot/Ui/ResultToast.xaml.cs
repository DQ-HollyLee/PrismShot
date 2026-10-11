using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PrismShot.Core;
using PrismShot.Core.Capture;
using PrismShot.Core.Imaging;
using PrismShot.Core.Interop;

namespace PrismShot.Ui;

/// <summary>
/// 截图完成后右下角浮出来的那张小通知。
/// </summary>
/// <remarks>
/// 存在感必须低但不能没有：截图这件事经常是「按完就继续干活」，
/// 如果成功后屏幕上什么变化都没有，用户会怀疑到底截没截到；
/// 而一个「咔嚓」音效或者一个需要点确认的对话框，又比原生工具吵得多。
/// 这张卡片的折中就是：两秒后出现、两秒半后自己没入背景，全程不抢焦点不需要点。
/// </remarks>
internal sealed partial class ResultToast : Window
{
    private readonly DispatcherTimer _timer;
    private readonly string? _targetPath;
    private DateTime _opened;

    public ResultToast(Raster thumbnail, string title, string? targetPath, int seconds)
        : this(thumbnail, title, HintOf(targetPath), targetPath, seconds) { }

    /// <summary>
    /// 没有缩略图的纯文字提示。给「不是截图」的动作用 ——
    /// 录屏宏发了就是发了，没有图片可贴，但要让人知道它确实发了。
    /// </summary>
    public static ResultToast Notice(string title, string hint, int seconds) =>
        new(null, title, hint, null, seconds);

    private ResultToast(
        Raster? thumbnail,
        string title,
        string hint,
        string? targetPath,
        int seconds
    )
    {
        InitializeComponent();

        _targetPath = targetPath;

        if (thumbnail is null)
        {
            // 空留一个 96×60 的透明格子只会把文字挤到右边去。
            ThumbBorder.Visibility = Visibility.Collapsed;
        }
        else
        {
            Thumb.Source = Downscale(thumbnail);
        }

        TitleText.Text = title;
        HintText.Text = hint;

        PositionAtPrimaryBottomRight();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += OnTick;
        _opened = DateTime.UtcNow;
        LifetimeSeconds = Math.Clamp(seconds, 1, 30);

        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    private static string HintOf(string? targetPath) =>
        string.IsNullOrEmpty(targetPath) ? "已在剪贴板中" : "点击打开所在位置";

    private int LifetimeSeconds { get; }

    private void OnTick(object? sender, EventArgs e)
    {
        double elapsed = (DateTime.UtcNow - _opened).TotalSeconds;

        if (elapsed > LifetimeSeconds)
        {
            // 淡出而不是直接消失：忽然没了会在余光里留下一记闪烁，
            // 让人以为刚才错过了什么。
            double fade = 1d - ((elapsed - LifetimeSeconds) / 0.35d);
            Opacity = Math.Max(0, fade);
            if (fade <= 0.02)
            {
                _timer.Stop();
                Close();
            }
        }
    }

    private static BitmapSource Downscale(Raster raster)
    {
        // 缩略图直接交给 WPF 缩就够了：这张卡片总共存在两三秒，
        // 没人会凑上去核验它的采样质量，为了它再写一遍 resize 不划算。
        TransformedBitmap source = new(
            raster.ToBitmapSource(),
            new ScaleTransform(96d / raster.Width * 1.0, 60d / raster.Height * 1.0)
        );
        source.Freeze();
        return source;
    }

    private void PositionAtPrimaryBottomRight()
    {
        Loaded += (_, _) =>
        {
            DisplayTarget? primary = Primary();
            if (primary is null)
            {
                return;
            }

            WindowInteropHelper helper = new(this);
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;

            SnapsToDevicePixels = true;
            UpdateLayout();

            int physicalWidth = (int)Math.Ceiling(ActualWidth * scale);
            int physicalHeight = (int)Math.Ceiling(ActualHeight * scale);

            _ = Win32.SetWindowPos(
                helper.Handle,
                Win32.WindowHandleTopMost,
                primary.Bounds.Right - physicalWidth - 24,
                primary.Bounds.Bottom - physicalHeight - 24,
                physicalWidth,
                physicalHeight,
                Win32.SetWindowPosNoActivate | Win32.SetWindowPosShowWindow
            );
        };
    }

    private static DisplayTarget? Primary()
    {
        foreach (DisplayTarget target in MonitorEnumerator.Enumerate())
        {
            if (target.IsPrimary)
            {
                return target;
            }
        }

        return null;
    }

    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        if (string.IsNullOrEmpty(_targetPath) || !File.Exists(_targetPath))
        {
            Close();
            return;
        }

        try
        {
            _ = Process.Start(
                new ProcessStartInfo("explorer.exe", $"/select,\"{_targetPath}\"")
                {
                    UseShellExecute = true,
                }
            );
        }
        catch (Exception ex)
        {
            Log.Error("打开截图位置失败。", ex);
        }

        Close();
    }
}
