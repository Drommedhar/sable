using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Sable.App;

/// <summary>
/// Navigator panel body: a letterboxed composite thumbnail with the visible-viewport
/// rectangle drawn over it. Click or drag re-centres the canvas on that document point
/// (<see cref="ViewCenterRequested"/> carries doc-px coordinates).
/// </summary>
public sealed class NavigatorView : Control
{
    private Bitmap? _thumb;
    private int _docW, _docH;
    private Rect _visibleDoc;   // visible region in doc px
    private bool _dragging;

    /// <summary>Raised with the document point (doc px) the view should centre on.</summary>
    public event Action<double, double>? ViewCenterRequested;

    public void SetThumbnail(Bitmap? bmp, int docW, int docH)
    {
        _thumb?.Dispose();
        _thumb = bmp;
        _docW = docW; _docH = docH;
        InvalidateVisual();
    }

    public void SetVisibleRect(double x, double y, double w, double h)
    {
        _visibleDoc = new Rect(x, y, w, h);
        InvalidateVisual();
    }

    /// <summary>Letterbox fit of the document inside the control: (offset, scale).</summary>
    private (double ox, double oy, double s) Fit()
    {
        if (_docW <= 0 || _docH <= 0 || Bounds.Width <= 0 || Bounds.Height <= 0) return (0, 0, 0);
        double s = Math.Min(Bounds.Width / _docW, Bounds.Height / _docH);
        return ((Bounds.Width - _docW * s) / 2, (Bounds.Height - _docH * s) / 2, s);
    }

    public override void Render(DrawingContext ctx)
    {
        var (ox, oy, s) = Fit();
        if (_thumb is null || s <= 0) return;

        var dest = new Rect(ox, oy, _docW * s, _docH * s);
        ctx.DrawImage(_thumb, new Rect(_thumb.Size), dest);
        ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(70, 70, 70))), dest);

        // viewport rectangle (clamped to the doc area so a fully-zoomed-out view shows the border)
        if (_visibleDoc.Width > 0 && _visibleDoc.Height > 0)
        {
            var r = new Rect(
                ox + _visibleDoc.X * s, oy + _visibleDoc.Y * s,
                _visibleDoc.Width * s, _visibleDoc.Height * s).Intersect(dest.Inflate(1));
            ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(64, 156, 255)), 1.5), r);
        }
    }

    private void CenterAt(Point p)
    {
        var (ox, oy, s) = Fit();
        if (s <= 0) return;
        double dx = (p.X - ox) / s;
        double dy = (p.Y - oy) / s;
        ViewCenterRequested?.Invoke(Math.Clamp(dx, 0, _docW), Math.Clamp(dy, 0, _docH));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _dragging = true;
        e.Pointer.Capture(this);
        CenterAt(e.GetPosition(this));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_dragging) CenterAt(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _dragging = false;
        e.Pointer.Capture(null);
    }
}
