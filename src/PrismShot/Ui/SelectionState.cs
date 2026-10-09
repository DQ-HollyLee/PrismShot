using System;
using System.Collections.Generic;
using PrismShot.Core.Capture;
using PrismShot.Core.Interop;

namespace PrismShot.Ui;

/// <summary>四种选取方式。与 Windows 原生截图工具一一对应。</summary>
internal enum SelectionMode
{
    /// <summary>矩形。默认。</summary>
    Rect,

    /// <summary>任意形状。按住左键随手画。</summary>
    Freeform,

    /// <summary>窗格。自动吸附到某个顶层窗口。</summary>
    Window,

    /// <summary>整屏。</summary>
    FullScreen,
}

/// <summary>
/// 一次选取的共享状态。
/// </summary>
/// <remarks>
/// 多个显示器各有一张覆盖层窗口，鼠标滑过哪块屏就由那张窗口处理事件，
/// 但它们改的是同一份状态 —— 否则跨屏框选会断在显示器交界处。
/// </remarks>
internal sealed class SelectionState
{
    private readonly object _gate = new();

    private SelectionMode _mode = SelectionMode.Rect;
    private Rect32 _rect;
    private List<Point32> _freeform = new();
    private WindowCandidate? _window;
    private bool _dragging;
    private bool _hasRect;

    public SelectionMode Mode
    {
        get
        {
            lock (_gate)
            {
                return _mode;
            }
        }
        set
        {
            lock (_gate)
            {
                _mode = value;
                _dragging = false;
                _hasRect = false;
                _freeform = new List<Point32>();
                _window = null;
            }
        }
    }

    public bool Dragging
    {
        get
        {
            lock (_gate)
            {
                return _dragging;
            }
        }
        set
        {
            lock (_gate)
            {
                _dragging = value;
            }
        }
    }

    public bool HasRect
    {
        get
        {
            lock (_gate)
            {
                return _hasRect;
            }
        }
    }

    public Rect32 Rect
    {
        get
        {
            lock (_gate)
            {
                return _rect;
            }
        }
    }

    public List<Point32> Freeform => _freeform;

    public WindowCandidate? Window
    {
        get
        {
            lock (_gate)
            {
                return _window;
            }
        }
        set
        {
            lock (_gate)
            {
                _window = value;
            }
        }
    }

    /// <summary>更新矩形。两点可以出现在虚拟桌面的任意位置，这里统一规整成左上—右下。</summary>
    public void UpdateRect(Point32 anchor, Point32 current)
    {
        Rect32 normalized = new()
        {
            Left = Math.Min(anchor.X, current.X),
            Top = Math.Min(anchor.Y, current.Y),
            Right = Math.Max(anchor.X, current.X),
            Bottom = Math.Max(anchor.Y, current.Y),
        };

        lock (_gate)
        {
            _rect = normalized;
            _hasRect = true;
        }
    }

    public void AddFreeformPoint(Point32 point) => _freeform.Add(point);

    /// <summary>把目前的存货变成一次确定的结果。</summary>
    public OverlayResult ToResult()
    {
        lock (_gate)
        {
            if (_mode == SelectionMode.Freeform && _freeform.Count >= 3)
            {
                return OverlayResult.FromPolygon(_freeform, BoundsOf(_freeform));
            }

            if (_window is not null)
            {
                return OverlayResult.FromRect(_window.Bounds);
            }

            if (_hasRect && _rect.Width > 0 && _rect.Height > 0)
            {
                return OverlayResult.FromRect(_rect);
            }

            return OverlayResult.Cancelled();
        }
    }

    public static Rect32 BoundsOf(List<Point32> points)
    {
        if (points.Count == 0)
        {
            return new Rect32();
        }

        int left = int.MaxValue;
        int top = int.MaxValue;
        int right = int.MinValue;
        int bottom = int.MinValue;

        foreach (Point32 point in points)
        {
            left = Math.Min(left, point.X);
            top = Math.Min(top, point.Y);
            right = Math.Max(right, point.X);
            bottom = Math.Max(bottom, point.Y);
        }

        return new Rect32 { Left = left, Top = top, Right = right, Bottom = bottom };
    }
}

/// <summary>覆盖层走完一圈的结果。</summary>
internal sealed class OverlayResult
{
    public bool Canceled { get; private init; }

    public Rect32 Region { get; private init; }

    public List<Point32>? Polygon { get; private init; }

    public int Width => Region.Width;

    public int Height => Region.Height;

    public static OverlayResult Cancelled() => new() { Canceled = true };

    public static OverlayResult FromRect(Rect32 region) => new() { Region = region };

    public static OverlayResult FromPolygon(List<Point32> polygon, Rect32 bounds) =>
        new() { Region = bounds, Polygon = new List<Point32>(polygon) };

    public override string ToString() =>
        Canceled
            ? "(已取消)"
            : $"{Region.Left},{Region.Top} {Width}x{Height}{(Polygon is null ? "" : " 多边形")}";
}
