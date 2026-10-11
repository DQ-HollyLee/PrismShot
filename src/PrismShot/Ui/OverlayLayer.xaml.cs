using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using PrismShot.Core;
using PrismShot.Core.Capture;
using PrismShot.Core.Interop;
using PrismShot.Core.Platform;

namespace PrismShot.Ui;

/// <summary>
/// 一块显示器上的那张冻结帧覆盖层。
/// </summary>
/// <remarks>
/// <para>
/// 每块屏幕单独一张窗口，而不是拼一个横跨虚拟桌面的巨窗：后者在多台不同缩放率的显示器上
/// 取不到唯一的 DPI 基准，内容会错位，而且 DWM 对超桌面尺寸的窗口处理并不友好。
/// 拆开之后每张窗口只关心自己那块屏的缩放，跨屏框选通过共享状态衔接。
/// </para>
/// <para>
/// 这层窗口不接受焦点（<c>ShowActivated=False</c> + <c>SWP_NOACTIVATE</c>），
/// 因为它一抢焦点，用户原本正在打的那一串字就断在前一个窗口里了 ——
/// 原生截图工具也正是这么处理的。
/// </para>
/// </remarks>
internal sealed partial class OverlayLayer : Window
{
    private readonly DisplayTarget _target;
    private readonly SelectionState _state;
    private readonly bool _runsToolbar;
    private readonly bool _magnifierEnabled;
    private readonly List<WindowCandidate> _windows;

    private Point32 _anchor;
    private bool _pressed;

    /// <summary>本层已完成的信号。只有真正跑完一次（成功或取消）才置 1。</summary>
    public event Action<OverlayResult>? Completed;

    public event Action<string>? RecorderRequested;

    public OverlayLayer(
        DisplayTarget target,
        BitmapSource frozenFrame,
        SelectionState state,
        List<WindowCandidate> windows,
        bool runsToolbar,
        bool magnifierEnabled,
        string recorderHint
    )
    {
        _target = target;
        _state = state;
        _windows = windows;
        _runsToolbar = runsToolbar;
        _magnifierEnabled = magnifierEnabled;

        InitializeComponent();

        // 按钮提示写死"向显卡驱动发送录屏组合键"是句没用的话：
        // 各家热键不同（Alt+F9 / Ctrl+Shift+R / Win+Alt+R），按钮到底会按哪家的门铃，
        // 只有配置说了算。把它说出来，用户至少能一眼看出「哦，是我没配」。
        if (runsToolbar)
        {
            RecordButton.ToolTip = recorderHint;
        }

        InitializeFrame(frozenFrame);
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
    }

    public DisplayTarget Target => _target;

    private void InitializeFrame(BitmapSource frame)
    {
        FrameImage.Source = frame;
        FrameImage.Width = frame.PixelWidth;
        FrameImage.Height = frame.PixelHeight;

        Surface.Width = _target.PixelWidth;
        Surface.Height = _target.PixelHeight;

        MagnifierFill.Fill = new ImageBrush(frame)
        {
            Stretch = Stretch.Fill,
            ViewboxUnits = BrushMappingMode.Absolute,
            ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
            Viewport = new Rect(0, 0, 1, 1),
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // 工具窗这一位（WS_EX_TOOLWINDOW）是为了把它从 Alt+Tab 列表里摘出去。
        // 覆盖层是「正在发生的一件事」，不是一个「可以切过去继续用的地方」。
        WindowInteropHelper helper = new(this);
        IntPtr exStyle = Win32.GetWindowLongPtr(helper.Handle, Win32.GetWindowLongExStyle);
        _ = Win32.SetWindowLongPtr(
            helper.Handle,
            Win32.GetWindowLongExStyle,
            (int)exStyle.ToInt64() | (int)Win32.WindowStyleExToolWindow
        );

        PositionToMonitor(helper.Handle);
    }

    private void PositionToMonitor(IntPtr handle)
    {
        double scale = ReadDpiScale(this);
        RootScale.ScaleX = 1d / scale;
        RootScale.ScaleY = 1d / scale;

        // 用 SetWindowPos 直接下物理像素，而不是给 Left/Top 赋值：
        // WPF 的那两个属性单位是 DIP，绕一圈换算难免在 125%、150% 这类缩放上丢掉半像素，
        // 半像素足够让冻结帧出现 1 像素错位 —— 截图工具最忌讳的就是这个。
        _ = Win32.SetWindowPos(
            handle,
            Win32.WindowHandleTopMost,
            _target.Bounds.Left,
            _target.Bounds.Top,
            _target.PixelWidth,
            _target.PixelHeight,
            Win32.SetWindowPosNoActivate | Win32.SetWindowPosShowWindow
        );
    }

    private static double ReadDpiScale(Visual visual)
    {
        try
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(visual);
            return dpi.DpiScaleX > 0.01 ? dpi.DpiScaleX : 1d;
        }
        catch
        {
            return 1d;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_runsToolbar)
        {
            Toolbar.Visibility = Visibility.Visible;
            PositionToolbar();
        }

        Refresh();
    }

    private void PositionToolbar()
    {
        double left = (_target.PixelWidth - Toolbar.ActualWidth) / 2d;
        Canvas.SetLeft(Toolbar, Math.Max(0, left));
        Canvas.SetTop(Toolbar, 24);
    }

    // ---------------- 输入 ----------------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Point32 point = ToVirtual(e.GetPosition(Surface));

        switch (_state.Mode)
        {
            case SelectionMode.Freeform:
                _state.Dragging = true;
                _state.AddFreeformPoint(point);
                break;

            case SelectionMode.Window:
                WindowCandidate? window = HitTestWindow(point);
                if (window is null)
                {
                    Complete(OverlayResult.Cancelled());
                    return;
                }

                Complete(OverlayResult.FromRect(window.Bounds));
                return;

            case SelectionMode.FullScreen:
                Complete(OverlayResult.FromRect(_target.Bounds));
                return;

            default:
                _anchor = point;
                _pressed = true;
                _state.Dragging = true;
                _state.UpdateRect(_anchor, point);
                break;
        }

        CaptureMouse();
        Refresh();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point32 point = ToVirtual(e.GetPosition(Surface));

        SelectionMode mode = _state.Mode;
        if (mode == SelectionMode.Window)
        {
            // 窗口模式不用拖拽，鼠标挪到哪儿就高亮哪儿，点一下定案。
            _state.Window = HitTestWindow(point);
        }
        else if (mode == SelectionMode.Rect && _pressed)
        {
            _state.UpdateRect(_anchor, point);
        }
        else if (mode == SelectionMode.Freeform && _state.Dragging)
        {
            List<Point32> points = _state.Freeform;
            if (points.Count > 0)
            {
                // 采样抽稀：手抖产生的相邻点不到 3 像素就丢掉，多边形顶点数量控制在几千以内，
                // 后面的 Alpha 遮罩是逐行扫描，顶点越少算得越快。
                Point32 last = points[^1];
                if (Math.Abs(point.X - last.X) + Math.Abs(point.Y - last.Y) >= 3)
                {
                    _state.AddFreeformPoint(point);
                }
            }
        }

        UpdateMagnifier(point);
        Refresh();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        Point32 point = ToVirtual(e.GetPosition(Surface));
        SelectionMode mode = _state.Mode;

        if (mode == SelectionMode.Rect)
        {
            _pressed = false;
            _state.Dragging = false;
            _state.UpdateRect(_anchor, point);

            // 鼠标没动过的「点一下」不算选区，否则一次误点就出来一张 1x1 的图。
            if (_state.Rect.Width < 4 || _state.Rect.Height < 4)
            {
                _pressed = false;
                Refresh();
                return;
            }

            Complete(_state.ToResult());
            return;
        }

        if (mode == SelectionMode.Freeform)
        {
            _state.Dragging = false;

            // 松开即定案，和原生截图工具一致：画完不必再点一次确认。
            // 原来这里只把 Dragging 置 false 就完事了，结果一次都没提交 ——
            // 画完停在原地，再按下又往同一个顶点列表里继续追加，看着像「还能接着画」。
            //
            // 顶点不足 3 个说明只是点了一下、没真的画，那就留在原地继续等，
            // 不要拿一个空结果把整个截图取消掉。
            if (_state.Freeform.Count >= 3)
            {
                Complete(_state.ToResult());
                return;
            }

            Refresh();
        }
    }

    protected override void OnMouseDoubleClick(MouseButtonEventArgs e)
    {
        base.OnMouseDoubleClick(e);

        // 画完马上双击 = 「就这个范围」。和原生工具一样，省得再去点一下确认。
        if (_state.Mode == SelectionMode.Freeform && _state.Freeform.Count >= 3)
        {
            Complete(_state.ToResult());
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        Complete(OverlayResult.Cancelled());
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        bool handled = true;

        switch (e.Key)
        {
            case Key.Escape:
                Complete(OverlayResult.Cancelled());
                break;

            case Key.Enter:
                Complete(AcceptCurrent());
                break;

            case Key.D1:
                ApplyMode(SelectionMode.Rect);
                break;

            case Key.D2:
                ApplyMode(SelectionMode.Freeform);
                break;

            case Key.D3:
                ApplyMode(SelectionMode.Window);
                break;

            case Key.D4:
                ApplyMode(SelectionMode.FullScreen);
                break;

            default:
                handled = false;
                break;
        }

        if (handled)
        {
            e.Handled = true;
        }
    }

    /// <summary>回车键的语义：有选区就交出选区，没有就当整屏。</summary>
    public OverlayResult AcceptCurrent()
    {
        if (_state.Window is not null)
        {
            return OverlayResult.FromRect(_state.Window.Bounds);
        }

        OverlayResult candidate = _state.ToResult();
        if (!candidate.Canceled)
        {
            return candidate;
        }

        return OverlayResult.FromRect(MonitorEnumerator.VirtualScreen);
    }

    private WindowCandidate? HitTestWindow(Point32 virtualPoint)
    {
        // 逆序遍历 = 从 Z 序栈顶往下找，先命中视觉上压在最前面的那个窗口。
        for (int index = _windows.Count - 1; index >= 0; index--)
        {
            WindowCandidate candidate = _windows[index];
            if (
                virtualPoint.X >= candidate.Bounds.Left
                && virtualPoint.X < candidate.Bounds.Right
                && virtualPoint.Y >= candidate.Bounds.Top
                && virtualPoint.Y < candidate.Bounds.Bottom
            )
            {
                return candidate;
            }
        }

        return null;
    }

    private Point32 ToVirtual(Point local) =>
        new()
        {
            X = _target.Bounds.Left + (int)Math.Round(local.X),
            Y = _target.Bounds.Top + (int)Math.Round(local.Y),
        };

    // ---------------- 绘制 ----------------

    /// <summary>关闭遮罩压暗。用户想要「原色挑选」时把它调成 0。</summary>
    public void SetDimAmount(byte alpha)
    {
        DimMask.Fill = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));
    }

    public void Refresh()
    {
        Rect32 localMonitor = new()
        {
            Left = 0,
            Top = 0,
            Right = _target.PixelWidth,
            Bottom = _target.PixelHeight,
        };

        Geometry? selection = BuildSelectionGeometry(localMonitor);
        GeometryGroup dim = new() { FillRule = FillRule.EvenOdd };
        dim.Children.Add(new RectangleGeometry(ToRect(localMonitor)));

        if (selection is not null)
        {
            dim.Children.Add(selection);
            Outline.Data = selection;
            Outline.Visibility = Visibility.Visible;
            ShowReadout(selection.Bounds);
        }
        else
        {
            Outline.Visibility = Visibility.Collapsed;
            Readout.Visibility = Visibility.Collapsed;
        }

        DimMask.Data = dim;
    }

    private Rect ToRect(Rect32 r) => new(r.Left, r.Top, r.Width, r.Height);

    private Geometry? BuildSelectionGeometry(Rect32 localMonitor)
    {
        SelectionMode mode = _state.Mode;

        if (mode == SelectionMode.Window)
        {
            WindowCandidate? window = _state.Window;
            return window is null ? null : Translate(window.Bounds);
        }

        if (mode == SelectionMode.Freeform)
        {
            return FreeformGeometry(_state.Freeform);
        }

        if (!_state.HasRect)
        {
            return null;
        }

        return Translate(_state.Rect);
    }

    // 自由绘制的几何图形必须**增量**搭出来，每次重画都从头 new 一遍是不可行的。
    //
    // 顶点是一边拖一边往列表里加的，一次框选下来能有几千个。原先是每收到一个
    // MouseMove 就把整条折线重建成新的 PathGeometry + 几千个 LineSegment ——
    // 实测两万个顶点时单次重画要分配 5.5 MB，一次十几秒的框选累计就是几个 GB 的
    // 垃圾，全部堆到 Gen2 上，工作集跟着一路涨（用户报的「内存暴涨」有一半是它）。
    //
    // 改成：几何对象只建一次，之后每来一个新顶点就补一段。顶点列表只会增长，
    // 于是每次重画的开销从「顶点数」降到「新增顶点数」—— 绝大多数时候就是 1。
    // 换了一轮框选（列表实例被 SelectionState 换掉）或顶点变少时，才整体重建。
    private List<Point32>? _freeformSource;
    private PathFigure? _freeformFigure;
    private PathGeometry? _freeformGeometry;
    private int _freeformVertices;

    private Geometry? FreeformGeometry(List<Point32> points)
    {
        if (points.Count < 2)
        {
            return null;
        }

        if (!ReferenceEquals(_freeformSource, points) || points.Count < _freeformVertices)
        {
            _freeformSource = points;
            _freeformFigure = new PathFigure { IsClosed = true, IsFilled = true };
            _freeformGeometry = new PathGeometry();
            _freeformGeometry.Figures.Add(_freeformFigure);
            _freeformVertices = 0;
        }

        if (_freeformVertices == 0)
        {
            Point32 start = Local(points[0], default);
            _freeformFigure!.StartPoint = new Point(start.X, start.Y);
            _freeformVertices = 1;
        }

        for (int index = _freeformVertices; index < points.Count; index++)
        {
            Point32 local = Local(points[index], default);
            _freeformFigure!.Segments.Add(new LineSegment(new Point(local.X, local.Y), true));
        }

        _freeformVertices = points.Count;
        return _freeformGeometry;
    }

    /// <summary>把虚拟坐标的矩形搬进本层自己的坐标系。</summary>
    private StreamGeometry Translate(Rect32 virtualRect)
    {
        Rect32 clipped = Intersect(Move(virtualRect, -_target.Bounds.Left, -_target.Bounds.Top));
        return Box(clipped);
    }

    private Rect32 Intersect(Rect32 r)
    {
        int left = Math.Max(r.Left, 0);
        int top = Math.Max(r.Top, 0);
        int right = Math.Min(r.Right, _target.PixelWidth);
        int bottom = Math.Min(r.Bottom, _target.PixelHeight);

        if (right < left)
        {
            right = left;
        }

        if (bottom < top)
        {
            bottom = top;
        }

        return new Rect32 { Left = left, Top = top, Right = right, Bottom = bottom };
    }

    private static Rect32 Move(Rect32 r, int dx, int dy) =>
        new() { Left = r.Left + dx, Top = r.Top + dy, Right = r.Right + dx, Bottom = r.Bottom + dy };

    private Point32 Local(Point32 virtualPoint, Rect32 localMonitor) =>
        new()
        {
            X = virtualPoint.X - _target.Bounds.Left,
            Y = virtualPoint.Y - _target.Bounds.Top,
        };

    private static StreamGeometry Box(Rect32 r)
    {
        StreamGeometry geometry = new();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(new Point(r.Left, r.Top), true, true);
            context.LineTo(new Point(r.Right, r.Top), true, false);
            context.LineTo(new Point(r.Right, r.Bottom), true, false);
            context.LineTo(new Point(r.Left, r.Bottom), true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private void ShowReadout(Rect bounds)
    {
        Readout.Visibility = Visibility.Visible;
        ReadoutText.Text = $"{Math.Round(bounds.Width)} × {Math.Round(bounds.Height)}";

        double left = Math.Clamp(bounds.Left, 0, Math.Max(0, _target.PixelWidth - Readout.ActualWidth));
        double top = Math.Clamp(bounds.Top - Readout.ActualHeight - 6, 0, Math.Max(0, _target.PixelHeight - Readout.ActualHeight));
        Canvas.SetLeft(Readout, left);
        Canvas.SetTop(Readout, top);
    }

    // ---------------- 放大镜 ----------------

    private void UpdateMagnifier(Point32 virtualPoint)
    {
        if (!_magnifierEnabled || _state.Mode == SelectionMode.FullScreen)
        {
            Magnifier.Visibility = Visibility.Collapsed;
            return;
        }

        const double Zoom = 4d;
        double sourceWidth = Magnifier.Width / Zoom;
        double sourceHeight = Magnifier.Height / Zoom;

        Point32 local = Local(virtualPoint, default);
        ((ImageBrush)MagnifierFill.Fill).Viewbox = new Rect(
            local.X - (sourceWidth / 2d),
            local.Y - (sourceHeight / 2d),
            sourceWidth,
            sourceHeight
        );

        Canvas.SetLeft(Magnifier, Clamp(local.X + 24, 0, _target.PixelWidth - Magnifier.Width));
        Canvas.SetTop(
            Magnifier,
            Clamp(local.Y + 24, 0, _target.PixelHeight - Magnifier.Height)
        );
        Magnifier.Visibility = Visibility.Visible;
    }

    private static double Clamp(double value, double min, double max) =>
        Math.Max(min, Math.Min(max, value));

    // ---------------- 工具栏 ----------------

    /// <summary>切换工具。由工具栏按钮和宿主的数字键共用。</summary>
    public void ApplyModePublic(SelectionMode mode) => ApplyMode(mode);

    private void ApplyMode(SelectionMode mode)
    {
        _state.Mode = mode;
        _pressed = false;

        RectButton.Style = (Style)Resources["ToolButton"];
        FreeformButton.Style = (Style)Resources["ToolButton"];
        WindowButton.Style = (Style)Resources["ToolButton"];
        FullButton.Style = (Style)Resources["ToolButton"];

        Button active = mode switch
        {
            SelectionMode.Rect => RectButton,
            SelectionMode.Freeform => FreeformButton,
            SelectionMode.Window => WindowButton,
            _ => FullButton,
        };
        active.Style = (Style)Resources["AccentToolButton"];

        Refresh();
    }

    private void OnRectClick(object sender, RoutedEventArgs e) => ApplyMode(SelectionMode.Rect);

    private void OnFreeformClick(object sender, RoutedEventArgs e) =>
        ApplyMode(SelectionMode.Freeform);

    private void OnWindowClick(object sender, RoutedEventArgs e) => ApplyMode(SelectionMode.Window);

    private void OnFullClick(object sender, RoutedEventArgs e) =>
        Complete(OverlayResult.FromRect(MonitorEnumerator.VirtualScreen));

    private void OnCloseClick(object sender, RoutedEventArgs e) => Complete(OverlayResult.Cancelled());

    private void OnRecordClick(object sender, RoutedEventArgs e) => RecorderRequested?.Invoke("toolbar");

    // ---------------- 收尾 ----------------

    private volatile bool _finished;

    /// <summary>
    /// 结束一次选取。<paramref name="result"/> 广播出去后，
    /// 由 <see cref="OverlayHost"/> 统一收拢其它层 —— 层与层之间不互相 Dispose，
    /// 否则「谁关谁」的竞态会在某次多屏截图上变成 Reference 计数的崩溃。
    /// </summary>
    private void Complete(OverlayResult result)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        try
        {
            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }
        }
        catch
        {
            // 已经不可捕获了，忽略。
        }

        Completed?.Invoke(result);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _finished = true;
        base.OnClosing(e);
    }
}
