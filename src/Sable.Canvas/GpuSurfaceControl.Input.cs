using System.Runtime.InteropServices;
using Sable.Core.Undo;
using Sable.Engine;
using Sable.Engine.Commands;
using Sable.Engine.Layers;
using Sable.Tools;

namespace Sable.Canvas;

/// <summary>
/// Mouse input for the embedded GPU surface. The native child window receives OS
/// mouse messages directly (Avalonia can't see them over the surface — airspace),
/// so we subclass its WndProc, map surface pixels → document pixels via the inverse
/// viewport transform, and drive the brush. Windows-only; other platforms get
/// their own input path with the cross-platform surface work.
/// </summary>
public sealed unsafe partial class GpuSurfaceControl
{
    private const int GWLP_WNDPROC = -4;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_MOUSEWHEEL = 0x020A;
    private const uint WM_MBUTTONDOWN = 0x0207;
    private const uint WM_MBUTTONUP = 0x0208;
    private const uint WM_MOUSEACTIVATE = 0x0021;
    private const nint MA_NOACTIVATE = 3;

    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    private WndProcDelegate? _wndProc;
    private nint _origWndProc;
    private bool _painting;
    private double _lastDocX, _lastDocY;
    private StrokeSession? _session;
    private bool _panningMouse;
    private int _lastPanX, _lastPanY;
    private bool _moving;
    private double _moveStartX, _moveStartY;
    private int _moveOrigX, _moveOrigY;
    private bool _selecting;        // rubber-band drag for rect + ellipse marquee
    private double _selStartX, _selStartY;
    private bool _lassoing;
    private readonly List<(double X, double Y)> _lassoPts = new();
    // GIMP-style selection grips
    private bool _selResizing, _selMoving;
    private bool _hL, _hR, _hT, _hB;            // which edges follow the cursor
    private double _selL0, _selR0, _selT0, _selB0;
    private double _selMoveStartX, _selMoveStartY;

    // transform gizmo state
    private const float RotHandleDist = 28f;
    private bool _transforming;
    private int _xfMode;                 // 1 = move, 2 = rotate, 3 = scale
    private LayerXform _xfStart;
    private double _xfCenterX, _xfCenterY, _xfStartAngle, _xfStartDist;
    private float _xfStartSx, _xfStartSy;
    private double _xfMoveDocX, _xfMoveDocY;
    private int _xfOrigOffX, _xfOrigOffY;

    /// <summary>The active layer for paint/move. Set from the selected layer in the UI.</summary>
    public PixelLayer? ActiveLayer { get; set; }

    private ToolKind _activeTool = ToolKind.Brush;

    /// <summary>Active toolbar tool (PLAN §14). Raises <see cref="ToolChanged"/> on change.</summary>
    public ToolKind ActiveTool
    {
        get => _activeTool;
        set { if (_activeTool == value) return; _activeTool = value; ToolChanged?.Invoke(value); }
    }

    /// <summary>Raised when the active tool changes (so the toolbar can sync highlight).</summary>
    public event Action<ToolKind>? ToolChanged;

    /// <summary>When true the brush edits the layer's mask (black hides) instead of its pixels.</summary>
    public bool PaintMask { get; set; }

    public BrushTool Brush { get; } = new();

    /// <summary>Raised (R,G,B) when the eyedropper (Alt+click) samples a color.</summary>
    public Action<byte, byte, byte>? ColorPicked { get; set; }

    [DllImport("user32.dll")] private static extern short GetKeyState(int vKey);
    private static bool AltDown => (GetKeyState(0x12) & 0x8000) != 0;   // VK_MENU

    private StrokeSession? CreateSession()
    {
        if (ActiveLayer is not { } layer) return null;
        // honor an active selection (paint only inside it): rect bbox + optional mask
        Brush.Clip = _doc?.Selection is { } s ? (s.X, s.Y, s.W, s.H) : null;
        Brush.ClipMask = _doc?.SelectionMask;
        Brush.ClipMaskW = _doc?.Width ?? 0;
        // brush color is user-chosen (black on a mask = hide, white = reveal)
        if (PaintMask)
        {
            Brush.Erase = false;
            if (!layer.HasMask) layer.AddWhiteMask(layer.Width, layer.Height);
            // mask upload is full-buffer for now (partial mask upload later)
            return new StrokeSession(layer.Mask!, layer.Width, layer.Height, Brush,
                _ => { layer.MaskDirty = true; layer.Dirty = true; });
        }
        Brush.Erase = ActiveTool == ToolKind.Eraser;
        return new StrokeSession(layer.Pixels, layer.Width, layer.Height, Brush,
            tiles => layer.MarkTilesDirty(tiles));
    }

    /// <summary>Raised with the undoable command when a brush gesture completes.</summary>
    public Action<IUndoableCommand>? CommandProduced { get; set; }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern nint CallWindowProc(nint prev, nint hWnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint SetCapture(nint hWnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();

    private void HookInput()
    {
        if (_hwnd == 0) return;
        _wndProc = WndProc;
        _origWndProc = SetWindowLongPtr(_hwnd, GWLP_WNDPROC,
            Marshal.GetFunctionPointerForDelegate(_wndProc));
    }

    private void UnhookInput()
    {
        if (_hwnd != 0 && _origWndProc != 0)
            SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _origWndProc);
        _origWndProc = 0;
        _wndProc = null;
    }

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        // don't steal keyboard focus from the Avalonia window when the canvas is clicked,
        // so window shortcuts (tool keys, Ctrl+Z/S/O) keep working
        if (msg == WM_MOUSEACTIVATE) return MA_NOACTIVATE;

        if (msg == WM_MOUSEMOVE)
        {
            int lp = (int)lParam;
            _lastMouseX = (short)(lp & 0xFFFF);
            _lastMouseY = (short)((lp >> 16) & 0xFFFF);
        }

        switch (msg)
        {
            case WM_LBUTTONDOWN:
                OnLeftDown(hWnd, lParam);
                break;

            case WM_MOUSEMOVE when _painting:
            {
                var (dx, dy) = MapToDoc(lParam);
                _session?.StrokeTo(_lastDocX, _lastDocY, dx, dy);
                _lastDocX = dx; _lastDocY = dy;
                break;
            }
            case WM_MOUSEMOVE when _moving && ActiveLayer is not null:
            {
                var (dx, dy) = MapToDoc(lParam);
                ActiveLayer.OffsetX = _moveOrigX + (int)System.Math.Round(dx - _moveStartX);
                ActiveLayer.OffsetY = _moveOrigY + (int)System.Math.Round(dy - _moveStartY);
                _doc?.MarkStructureChanged();   // recomposite (no re-upload; pixels unchanged)
                break;
            }
            case WM_MOUSEMOVE when _transforming:
                TransformDrag(lParam);
                break;
            case WM_MOUSEMOVE when _selecting && _doc is not null:
            {
                var (dx, dy) = MapToDoc(lParam);
                _doc.Selection = SelRect.FromCorners(_selStartX, _selStartY, dx, dy, _doc.Width, _doc.Height);
                break;
            }
            case WM_MOUSEMOVE when _lassoing && _doc is not null:
            {
                var (dx, dy) = MapToDoc(lParam);
                _lassoPts.Add((dx, dy));
                _doc.Selection = LassoBounds();   // live bbox while drawing
                break;
            }
            case WM_MOUSEMOVE when _selResizing && _doc is not null:
            {
                var (dx, dy) = MapToDoc(lParam);
                double l = _selL0, r = _selR0, t = _selT0, b = _selB0;
                if (_hL) l = dx;
                if (_hR) r = dx;
                if (_hT) t = dy;
                if (_hB) b = dy;
                _doc.Selection = SelRect.FromCorners(l, t, r, b, _doc.Width, _doc.Height);
                break;
            }
            case WM_MOUSEMOVE when _selMoving && _doc is not null:
            {
                var (dx, dy) = MapToDoc(lParam);
                double w = _selR0 - _selL0, h = _selB0 - _selT0;
                double nl = Math.Clamp(_selL0 + (dx - _selMoveStartX), 0, _doc.Width - w);
                double nt = Math.Clamp(_selT0 + (dy - _selMoveStartY), 0, _doc.Height - h);
                _doc.Selection = SelRect.FromCorners(nl, nt, nl + w, nt + h, _doc.Width, _doc.Height);
                break;
            }
            case WM_MOUSEMOVE when _panningMouse:
            {
                int lp = (int)lParam;
                short x = (short)(lp & 0xFFFF), y = (short)((lp >> 16) & 0xFFFF);
                PanBy(x - _lastPanX, y - _lastPanY);
                _lastPanX = x; _lastPanY = y;
                break;
            }
            case WM_LBUTTONUP:
                OnLeftUp();
                break;

            // middle-drag = pan
            case WM_MBUTTONDOWN:
            {
                int lp = (int)lParam;
                _lastPanX = (short)(lp & 0xFFFF);
                _lastPanY = (short)((lp >> 16) & 0xFFFF);
                _panningMouse = true;
                SetCapture(hWnd);
                break;
            }
            case WM_MBUTTONUP:
                _panningMouse = false;
                ReleaseCapture();
                break;

            // wheel = zoom
            case WM_MOUSEWHEEL:
            {
                short delta = (short)(((int)wParam >> 16) & 0xFFFF);
                ZoomAt(delta > 0 ? 1.1 : 1.0 / 1.1, _lastMouseX, _lastMouseY);
                break;
            }
        }
        return CallWindowProc(_origWndProc, hWnd, msg, wParam, lParam);
    }

    private void OnLeftDown(nint hWnd, nint lParam)
    {
        var (dx, dy) = MapToDoc(lParam);
        bool paintTool = ActiveTool is ToolKind.Brush or ToolKind.Eraser or ToolKind.Fill;
        if (AltDown && ActiveLayer is not null && paintTool) { SampleColor(dx, dy); return; }

        switch (ActiveTool)
        {
            case ToolKind.Eyedropper:
                SampleColor(dx, dy);
                break;
            case ToolKind.Fill:
                DoFill(dx, dy);
                break;
            case ToolKind.Zoom:
                ZoomAt(AltDown ? 1.0 / 1.1 : 1.1, _lastMouseX, _lastMouseY);
                break;
            case ToolKind.Hand:
                StartPan(hWnd, lParam);
                break;
            case ToolKind.Transform:
                BeginTransform(lParam);
                break;
            case ToolKind.EllipseMarquee:
                if (_doc is not null)
                {
                    _selecting = true; SetCapture(hWnd);
                    _selStartX = dx; _selStartY = dy;
                    _doc.SelectionMask = null;
                }
                break;

            case ToolKind.Lasso:
                if (_doc is not null)
                {
                    _lassoing = true; SetCapture(hWnd);
                    _lassoPts.Clear(); _lassoPts.Add((dx, dy));
                    _doc.SelectionMask = null;
                }
                break;

            case ToolKind.MagicWand:
                if (_doc is not null && ActiveLayer is { } wl)
                {
                    var m = Sable.Engine.Selections.Wand(wl.Pixels, wl.Width, wl.Height, (int)dx, (int)dy, 32);
                    _doc.SetMaskSelection(m);
                }
                break;

            case ToolKind.Marquee:
                if (_doc is not null)
                {
                    var (ssx, ssy) = SurfaceOf(lParam);
                    int hit = _doc.SelectionMask is null ? HitSelHandle(ssx, ssy) : -1;
                    if (hit is >= 0 and < 8 && _doc.Selection is { } rs)   // grip → resize
                    {
                        _selResizing = true; SetCapture(hWnd);
                        _selL0 = rs.X; _selR0 = rs.Right; _selT0 = rs.Y; _selB0 = rs.Bottom;
                        _hL = hit is 0 or 6 or 7;
                        _hR = hit is 2 or 3 or 4;
                        _hT = hit is 0 or 1 or 2;
                        _hB = hit is 4 or 5 or 6;
                    }
                    else if (hit == 8 && _doc.Selection is { } ms)         // interior → move
                    {
                        _selMoving = true; SetCapture(hWnd);
                        _selL0 = ms.X; _selR0 = ms.Right; _selT0 = ms.Y; _selB0 = ms.Bottom;
                        _selMoveStartX = dx; _selMoveStartY = dy;
                    }
                    else                                                   // empty → new selection
                    {
                        _selecting = true; SetCapture(hWnd);
                        _selStartX = dx; _selStartY = dy;
                        _doc.SelectionMask = null;
                    }
                }
                break;

            case ToolKind.Move:
                if (ActiveLayer is { } ml)
                {
                    _moving = true; SetCapture(hWnd);
                    _moveStartX = dx; _moveStartY = dy;
                    _moveOrigX = ml.OffsetX; _moveOrigY = ml.OffsetY;
                }
                break;
            default: // Brush / Eraser
                _session = CreateSession();
                if (_session is not null)
                {
                    SetCapture(hWnd);
                    _painting = true;
                    _lastDocX = dx; _lastDocY = dy;
                    _session.StrokeTo(dx, dy, dx, dy);
                }
                break;
        }
    }

    private void OnLeftUp()
    {
        if (_painting)
        {
            _painting = false;
            ReleaseCapture();
            var cmd = _session?.Finalize();
            _session = null;
            if (cmd is not null) CommandProduced?.Invoke(cmd);
        }
        else if (_moving && ActiveLayer is { } layer)
        {
            _moving = false;
            ReleaseCapture();
            if (_doc is not null && (layer.OffsetX != _moveOrigX || layer.OffsetY != _moveOrigY))
                CommandProduced?.Invoke(new MoveOffsetCommand(_doc, layer, _moveOrigX, _moveOrigY, layer.OffsetX, layer.OffsetY));
        }
        else if (_transforming && ActiveLayer is { } tl)
        {
            _transforming = false;
            ReleaseCapture();
            if (_doc is not null)
                CommandProduced?.Invoke(new TransformLayerCommand(_doc, tl, _xfStart, LayerXform.From(tl)));
        }
        else if (_lassoing)
        {
            _lassoing = false;
            ReleaseCapture();
            if (_doc is not null)
            {
                if (_lassoPts.Count >= 3)
                    _doc.SetMaskSelection(Sable.Engine.Selections.Polygon(_doc.Width, _doc.Height, _lassoPts));
                else _doc.ClearSelection();
            }
            _lassoPts.Clear();
        }
        else if (_selecting || _selResizing || _selMoving)
        {
            bool wasEllipse = _selecting && ActiveTool == ToolKind.EllipseMarquee;
            _selecting = _selResizing = _selMoving = false;
            ReleaseCapture();
            if (_doc?.Selection is { W: < 3 } or { H: < 3 }) { _doc!.ClearSelection(); }
            else if (wasEllipse && _doc?.Selection is { } e)
                _doc.SetMaskSelection(Sable.Engine.Selections.Ellipse(_doc.Width, _doc.Height, e));
        }
        else if (_panningMouse)
        {
            _panningMouse = false;
            ReleaseCapture();
        }
    }

    /// <summary>Clear any active selection.</summary>
    public void Deselect() => _doc?.ClearSelection();

    /// <summary>Erase the selected region of the active layer (undoable). No-op without a selection.</summary>
    public void DeleteSelection()
    {
        if (_doc?.Selection is not { } sel || ActiveLayer is not { } layer || sel.W <= 0 || sel.H <= 0) return;
        var target = layer.Pixels;
        int w = layer.Width, h = layer.Height;
        var before = SnapshotAllTiles(target, w, h);
        var mask = _doc?.SelectionMask;
        int mw = _doc?.Width ?? w;
        for (int y = Math.Max(0, sel.Y); y < Math.Min(h, sel.Bottom); y++)
        for (int x = Math.Max(0, sel.X); x < Math.Min(w, sel.Right); x++)
        {
            if (mask is not null && mask[y * mw + x] == 0) continue;
            int i = (y * w + x) * 4;
            target[i] = target[i + 1] = target[i + 2] = target[i + 3] = 0;
        }
        var after = SnapshotAllTiles(target, w, h);
        layer.MarkTilesDirty(after.Keys);
        CommandProduced?.Invoke(new PaintRasterCommand(target, w, h, before, after, t => layer.MarkTilesDirty(t)));
    }

    /// <summary>Active layer's 4 transformed corners (TL,TR,BR,BL) in surface pixels.</summary>
    public float[] CornersSurface(PixelLayer l)
    {
        var c = AffineMath.Corners(l.Width, l.Height, l.OffsetX, l.OffsetY, l.ScaleX, l.ScaleY, l.Rotation);
        var vp = ComputeViewport();
        var r = new float[8];
        for (int i = 0; i < 4; i++)
        {
            r[2 * i] = vp.Ox + c[2 * i] * vp.Scale;
            r[2 * i + 1] = vp.Oy + c[2 * i + 1] * vp.Scale;
        }
        return r;
    }

    private void BeginTransform(nint lParam)
    {
        if (ActiveLayer is not { } l) return;
        var (sx, sy) = SurfaceOf(lParam);
        var cs = CornersSurface(l);
        double cx = (cs[0] + cs[2] + cs[4] + cs[6]) * 0.25;
        double cy = (cs[1] + cs[3] + cs[5] + cs[7]) * 0.25;
        double tmx = (cs[0] + cs[2]) * 0.5, tmy = (cs[1] + cs[3]) * 0.5;   // top mid
        double dl = Math.Sqrt((tmx - cx) * (tmx - cx) + (tmy - cy) * (tmy - cy));
        double rpx = tmx, rpy = tmy;
        if (dl > 1e-3) { rpx = tmx + (tmx - cx) / dl * RotHandleDist; rpy = tmy + (tmy - cy) / dl * RotHandleDist; }

        _xfMode = 0;
        if (Dist(sx, sy, rpx, rpy) <= 8) _xfMode = 2;                       // rotate handle
        else if (NearestCornerDist(cs, sx, sy) <= 8) _xfMode = 3;          // corner → scale
        else if (PointInQuad(cs, sx, sy)) _xfMode = 1;                     // inside → move

        if (_xfMode == 0) return;
        _transforming = true;
        SetCapture(_hwnd);
        _xfStart = LayerXform.From(l);
        _xfCenterX = cx; _xfCenterY = cy;
        _xfStartAngle = Math.Atan2(sy - cy, sx - cx);
        _xfStartDist = Math.Max(1e-3, Dist(sx, sy, cx, cy));
        _xfStartSx = l.ScaleX; _xfStartSy = l.ScaleY;
        var (ddx, ddy) = MapToDoc(lParam);
        _xfMoveDocX = ddx; _xfMoveDocY = ddy;
        _xfOrigOffX = l.OffsetX; _xfOrigOffY = l.OffsetY;
    }

    private void TransformDrag(nint lParam)
    {
        if (ActiveLayer is not { } l) return;
        var (sx, sy) = SurfaceOf(lParam);
        switch (_xfMode)
        {
            case 2: // rotate about center
            {
                double ang = Math.Atan2(sy - _xfCenterY, sx - _xfCenterX);
                l.Rotation = _xfStart.Rotation + (float)((ang - _xfStartAngle) * 180.0 / Math.PI);
                break;
            }
            case 3: // uniform scale about center
            {
                double ratio = Dist(sx, sy, _xfCenterX, _xfCenterY) / _xfStartDist;
                l.ScaleX = (float)(_xfStartSx * ratio);
                l.ScaleY = (float)(_xfStartSy * ratio);
                break;
            }
            default: // move
            {
                var (dx, dy) = MapToDoc(lParam);
                l.OffsetX = _xfOrigOffX + (int)Math.Round(dx - _xfMoveDocX);
                l.OffsetY = _xfOrigOffY + (int)Math.Round(dy - _xfMoveDocY);
                break;
            }
        }
        _doc?.MarkStructureChanged();
    }

    /// <summary>
    /// Hit-test the current selection's GIMP grips at a surface point. Returns the
    /// handle index 0..7 (TL,T,TR,R,BR,B,BL,L), 8 for the interior (move), or -1 for none.
    /// </summary>
    private int HitSelHandle(double sx, double sy)
    {
        if (_doc?.Selection is not { } s) return -1;
        var vp = ComputeViewport();
        double scale = vp.Scale;
        double l = vp.Ox + s.X * scale, r = vp.Ox + s.Right * scale;
        double t = vp.Oy + s.Y * scale, b = vp.Oy + s.Bottom * scale;
        double mx = (l + r) * 0.5, my = (t + b) * 0.5;
        Span<(double x, double y)> hs = stackalloc (double, double)[8]
        { (l, t), (mx, t), (r, t), (r, my), (r, b), (mx, b), (l, b), (l, my) };
        for (int i = 0; i < 8; i++)
            if (Dist(sx, sy, hs[i].x, hs[i].y) <= 7) return i;
        if (sx >= l && sx <= r && sy >= t && sy <= b) return 8;   // interior → move
        return -1;
    }

    /// <summary>Bounding rect (doc px) of the in-progress lasso points, clamped to the doc.</summary>
    private SelRect LassoBounds()
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x, y) in _lassoPts)
        {
            minX = Math.Min(minX, x); minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
        }
        return SelRect.FromCorners(minX, minY, maxX, maxY, _doc?.Width ?? 0, _doc?.Height ?? 0);
    }

    private static (double x, double y) SurfaceOf(nint lParam)
    {
        int lp = (int)lParam;
        return ((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
    }
    private static double Dist(double ax, double ay, double bx, double by)
        => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
    private static double NearestCornerDist(float[] cs, double x, double y)
    {
        double m = double.MaxValue;
        for (int i = 0; i < 4; i++) m = Math.Min(m, Dist(x, y, cs[2 * i], cs[2 * i + 1]));
        return m;
    }
    private static bool PointInQuad(float[] cs, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = 3; i < 4; j = i++)
        {
            double xi = cs[2 * i], yi = cs[2 * i + 1], xj = cs[2 * j], yj = cs[2 * j + 1];
            if (((yi > y) != (yj > y)) && (x < (xj - xi) * (y - yi) / (yj - yi) + xi)) inside = !inside;
        }
        return inside;
    }

    private void StartPan(nint hWnd, nint lParam)
    {
        int lp = (int)lParam;
        _lastPanX = (short)(lp & 0xFFFF);
        _lastPanY = (short)((lp >> 16) & 0xFFFF);
        _panningMouse = true;
        SetCapture(hWnd);
    }

    private void DoFill(double dx, double dy)
    {
        if (ActiveLayer is not { } layer) return;
        var target = layer.Pixels;
        int w = layer.Width, h = layer.Height;
        var before = SnapshotAllTiles(target, w, h);
        var clip = _doc?.Selection is { } s ? ((int, int, int, int)?)(s.X, s.Y, s.W, s.H) : null;
        int changed = FillTool.Flood(target, w, h, (int)dx, (int)dy, Brush.R, Brush.G, Brush.B, 255, 32, clip,
            _doc?.SelectionMask, _doc?.Width ?? 0);
        if (changed == 0) return;
        var after = SnapshotAllTiles(target, w, h);
        layer.MarkTilesDirty(after.Keys);
        CommandProduced?.Invoke(new PaintRasterCommand(target, w, h, before, after, t => layer.MarkTilesDirty(t)));
    }

    private static Dictionary<(int, int), byte[]> SnapshotAllTiles(byte[] px, int w, int h)
    {
        var snap = new Dictionary<(int, int), byte[]>();
        for (int ty = 0; ty < RasterTiles.TilesY(h); ty++)
        for (int tx = 0; tx < RasterTiles.TilesX(w); tx++)
            snap[(tx, ty)] = RasterTiles.GetTile(px, w, h, tx, ty);
        return snap;
    }

    private void SampleColor(double dx, double dy)
    {
        if (ActiveLayer is not { } layer) return;
        int x = (int)Math.Clamp(dx, 0, layer.Width - 1);
        int y = (int)Math.Clamp(dy, 0, layer.Height - 1);
        int i = (y * layer.Width + x) * 4;
        byte r = layer.Pixels[i], g = layer.Pixels[i + 1], b = layer.Pixels[i + 2];
        Brush.R = r; Brush.G = g; Brush.B = b;
        ColorPicked?.Invoke(r, g, b);
    }

    private (double x, double y) MapToDoc(nint lParam)
    {
        int lp = (int)lParam;
        short sx = (short)(lp & 0xFFFF);
        short sy = (short)((lp >> 16) & 0xFFFF);
        var vp = ComputeViewport();
        double scale = vp.Scale > 0 ? vp.Scale : 1;
        return ((sx - vp.Ox) / scale, (sy - vp.Oy) / scale);
    }
}
