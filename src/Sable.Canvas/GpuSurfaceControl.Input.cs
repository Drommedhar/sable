using Sable.Canvas.Platform;
using Sable.Core.Undo;
using Sable.Engine;
using Sable.Engine.Commands;
using Sable.Engine.Layers;
using Sable.Tools;

namespace Sable.Canvas;

/// <summary>
/// Canvas input. The OS-specific event source (<see cref="IInputSource"/>) decodes
/// native mouse/keys into the shared <see cref="ICanvasInputSink"/> callbacks below;
/// ALL the tool logic here is platform-agnostic, working in surface pixels (mapped to
/// document space via the inverse viewport transform). A new OS only needs a new
/// <see cref="IInputSource"/> — none of this file changes.
/// </summary>
public sealed unsafe partial class GpuSurfaceControl : ICanvasInputSink
{
    private IInputSource? _input;

    private bool _painting;
    private bool _hudAdjust;                       // Ctrl+Alt brush size/hardness HUD
    private double _hudStartSx, _hudStartSy;
    private float _hudStartRadius, _hudStartHardness;
    private double _lastDocX, _lastDocY;
    private StrokeSession? _session;
    private PixelLayer? _strokeLayer;          // active pixel-paint layer (null for mask paint)
    private RasterState _strokeBefore;         // pre-stroke raster snapshot (whole-raster undo + auto-crop)
    private bool _panningMouse;
    private int _lastPanX, _lastPanY;
    private bool _moving;
    private double _moveStartX, _moveStartY;
    private int _moveOrigX, _moveOrigY;
    private bool _gradienting;
    private double _gradStartDocX, _gradStartDocY;       // gradient start (doc px)
    private double _gradStartSx, _gradStartSy, _gradEndSx, _gradEndSy;   // line ends (surface px, overlay)
    private bool _cropping;
    private double _cropStartDocX, _cropStartDocY;
    private SelRect? _cropRect;                          // pending crop rect (doc px); Enter commits
    private bool _shaping;
    private double _shapeStartDocX, _shapeStartDocY;
    private double _shapeStartSx, _shapeStartSy, _shapeEndSx, _shapeEndSy;
    private double _cloneSrcX, _cloneSrcY;     // clone-stamp source point (doc px)
    private bool _cloneSet;
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

    /// <summary>The active PIXEL layer for paint (brush/fill/eyedropper). Null if a non-pixel layer is selected.</summary>
    public PixelLayer? ActiveLayer { get; set; }

    /// <summary>The selected layer of ANY type — drives Move + the bounds overlay (pixel, shape, group, …).</summary>
    public Layer? SelLayer { get; set; }

    private ToolKind _activeTool = ToolKind.Brush;

    /// <summary>Active toolbar tool (PLAN §14). Raises <see cref="ToolChanged"/> on change.</summary>
    public ToolKind ActiveTool
    {
        get => _activeTool;
        set
        {
            if (_activeTool == value) return;
            _activeTool = value;
            if (value != ToolKind.Type) CommitTextEdit();   // leaving Type ends editing
            ToolChanged?.Invoke(value);
        }
    }

    /// <summary>Raised when the active tool changes (so the toolbar can sync highlight).</summary>
    public event Action<ToolKind>? ToolChanged;

    /// <summary>When true the brush edits the layer's mask (black hides) instead of its pixels.</summary>
    public bool PaintMask { get; set; }

    public BrushTool Brush { get; } = new();

    /// <summary>The gradient the Gradient tool paints (edited in the Gradients panel).</summary>
    public GradientDef Gradient { get; } =
        new(new GradientStop(0f, 0, 0, 0, 255), new GradientStop(1f, 255, 255, 255, 255));

    /// <summary>Raised (R,G,B) when the eyedropper (Alt+click) samples a color.</summary>
    public Action<byte, byte, byte>? ColorPicked { get; set; }

    /// <summary>Raised after a Ctrl+Alt HUD brush adjust so the options-bar sliders can resync.</summary>
    public Action? BrushAdjusted { get; set; }

    /// <summary>Raised with the undoable command when a brush gesture completes.</summary>
    public Action<IUndoableCommand>? CommandProduced { get; set; }

    /// <summary>Raised with a freshly-built layer (e.g. a drawn shape) to add via the document VM + select.</summary>
    public Action<Layer>? LayerProduced { get; set; }

    /// <summary>Defaults for a NEW text layer (set from the Type options bar).</summary>
    public float TypeFontSize { get; set; } = 48f;
    public string TypeFontFamily { get; set; } = "";
    public bool TypeBold { get; set; }
    public bool TypeItalic { get; set; }
    public bool TypeUnderline { get; set; }
    public bool TypeStrike { get; set; }
    public int TypeAlign { get; set; }          // 0=L,1=C,2=R
    public float TypeLineSpacing { get; set; } = 1f;

    // on-canvas text editing: the layer currently being typed into (caret + window text input)
    private TextLayer? _editingText;
    public TextLayer? EditingText => _editingText;
    public bool TextEditing => _editingText is not null;
    /// <summary>Raised when text editing starts on a layer (so the UI selects it + syncs font controls).</summary>
    public event Action<TextLayer>? TextEditStarted;

    private void BeginTextEdit(TextLayer t) { _editingText = t; TextEditStarted?.Invoke(t); }

    /// <summary>Insert typed text at the end of the layer being edited (live).</summary>
    public void TextInsert(string s)
    {
        if (_editingText is not { } t || string.IsNullOrEmpty(s)) return;
        t.Text += s; t.Name = t.Text.Length > 0 ? t.Text : "Text"; t.Dirty = true;
    }

    public void TextBackspace()
    {
        if (_editingText is { } t && t.Text.Length > 0) { t.Text = t.Text[..^1]; t.Dirty = true; }
    }

    /// <summary>Finish on-canvas text editing.</summary>
    public void CommitTextEdit() => _editingText = null;

    private TextLayer? HitTextLayer(double dx, double dy)
    {
        if (_doc is null) return null;
        for (int i = _doc.Layers.Count - 1; i >= 0; i--)   // top-down
            if (_doc.Layers[i] is TextLayer t && t.Visible)
            {
                var (bx, by, bw, bh) = t.ContentBounds(_doc.Width, _doc.Height);
                if (dx >= bx + t.OffsetX && dx < bx + t.OffsetX + bw &&
                    dy >= by + t.OffsetY && dy < by + t.OffsetY + bh) return t;
            }
        return null;
    }

    // --- ICanvasInputSink: OS-agnostic pointer handlers (surface pixels) ----------

    void ICanvasInputSink.PointerDown(CanvasButton button, double sx, double sy, CanvasMods mods)
    {
        _lastMouseX = sx; _lastMouseY = sy;
        if (button == CanvasButton.Middle) { StartPan(sx, sy); return; }   // middle-drag = pan
        if (button == CanvasButton.Left) OnLeftDown(sx, sy, mods);
    }

    void ICanvasInputSink.PointerUp(CanvasButton button, double sx, double sy, CanvasMods mods)
    {
        _lastMouseX = sx; _lastMouseY = sy;
        if (button == CanvasButton.Middle)
        {
            if (_panningMouse) { _panningMouse = false; _input?.ReleaseCapture(); }
            return;
        }
        if (button == CanvasButton.Left) OnLeftUp();
    }

    void ICanvasInputSink.Wheel(double sx, double sy, int delta, CanvasMods mods)
    {
        _lastMouseX = sx; _lastMouseY = sy;
        ZoomAt(delta > 0 ? 1.1 : 1.0 / 1.1, sx, sy);
    }

    void ICanvasInputSink.PointerMove(double sx, double sy, CanvasMods mods)
    {
        _lastMouseX = sx; _lastMouseY = sy;
        if (CursorDocMoved is not null) { var (cdx, cdy) = MapToDoc(sx, sy); CursorDocMoved(cdx, cdy); }

        if (_guideAxis != 0 && _doc is { } gd)
        {
            var (gdx, gdy) = MapToDoc(sx, sy);
            if (_guideAxis == 1) gd.GuidesX[_guideIdx] = (float)Math.Round(gdx);
            else gd.GuidesY[_guideIdx] = (float)Math.Round(gdy);
            return;
        }

        if (_hudAdjust)
        {
            // Affinity HUD: horizontal drag = size, vertical = hardness. Ring stays anchored
            // at the drag-start point (don't let the cursor move the brush position).
            Brush.Radius = Math.Clamp(_hudStartRadius + (float)(sx - _hudStartSx) * 0.5f, 1f, 500f);
            Brush.Hardness = Math.Clamp(_hudStartHardness - (float)(sy - _hudStartSy) * 0.005f, 0f, 1f);
            _lastMouseX = _hudStartSx; _lastMouseY = _hudStartSy;   // anchor the preview ring
            UpdatePreviewDab();
            return;
        }

        if (_painting)
        {
            var (dx, dy) = MapToDoc(sx, sy);
            _session?.StrokeTo(_lastDocX, _lastDocY, dx, dy);
            _lastDocX = dx; _lastDocY = dy;
        }
        else if (_moving && SelLayer is { } al)
        {
            var (dx, dy) = MapToDoc(sx, sy);
            double rawX = _moveOrigX + (dx - _moveStartX);
            double rawY = _moveOrigY + (dy - _moveStartY);
            // smart guides (align to other layers / doc) first; fall back to guide/grid snap per axis
            var (snX, snY) = SmartSnap(al, rawX, rawY);
            al.OffsetX = (int)Math.Round(_smartX.Count > 0 ? snX : SnapAxis(snX, true));
            al.OffsetY = (int)Math.Round(_smartY.Count > 0 ? snY : SnapAxis(snY, false));
            _doc?.MarkStructureChanged();   // recomposite (no re-upload; pixels unchanged)
        }
        else if (_transforming)
        {
            TransformDrag(sx, sy);
        }
        else if (_selecting && _doc is not null)
        {
            var (dx, dy) = MapToDoc(sx, sy);
            (dx, dy) = Snap(dx, dy);   // marquee corner snaps to guides/grid/edges
            var rect = SelRect.FromCorners(_selStartX, _selStartY, dx, dy, _doc.Width, _doc.Height);
            if (ActiveTool == ToolKind.EllipseMarquee && rect.W > 0 && rect.H > 0)
                _doc.SetMaskSelection(Selections.Ellipse(_doc.Width, _doc.Height, rect));  // live ellipse outline
            else
                _doc.Selection = rect;        // rect marquee: live bbox (it IS a box)
        }
        else if (_lassoing && _doc is not null)
        {
            var (dx, dy) = MapToDoc(sx, sy);
            _lassoPts.Add((dx, dy));
            if (_lassoPts.Count >= 3)
                _doc.SetMaskSelection(Selections.Polygon(_doc.Width, _doc.Height, _lassoPts));  // live freehand path
            else
                _doc.Selection = LassoBounds();
        }
        else if (_selResizing && _doc is not null)
        {
            var (dx, dy) = MapToDoc(sx, sy);
            double l = _selL0, r = _selR0, t = _selT0, b = _selB0;
            if (_hL) l = dx;
            if (_hR) r = dx;
            if (_hT) t = dy;
            if (_hB) b = dy;
            _doc.Selection = SelRect.FromCorners(l, t, r, b, _doc.Width, _doc.Height);
        }
        else if (_selMoving && _doc is not null)
        {
            var (dx, dy) = MapToDoc(sx, sy);
            double w = _selR0 - _selL0, h = _selB0 - _selT0;
            double nl = Math.Clamp(_selL0 + (dx - _selMoveStartX), 0, _doc.Width - w);
            double nt = Math.Clamp(_selT0 + (dy - _selMoveStartY), 0, _doc.Height - h);
            _doc.Selection = SelRect.FromCorners(nl, nt, nl + w, nt + h, _doc.Width, _doc.Height);
        }
        else if (_gradienting)
        {
            _gradEndSx = sx; _gradEndSy = sy;   // track for the live line overlay
        }
        else if (_cropping && _doc is not null)
        {
            var (dx, dy) = MapToDoc(sx, sy);
            _cropRect = SelRect.FromCorners(_cropStartDocX, _cropStartDocY, dx, dy, _doc.Width, _doc.Height);
        }
        else if (_shaping)
        {
            _shapeEndSx = sx; _shapeEndSy = sy;   // track for the live outline overlay
        }
        else if (_panningMouse)
        {
            PanBy(sx - _lastPanX, sy - _lastPanY);
            _lastPanX = (int)sx; _lastPanY = (int)sy;
        }
    }

    // selection combine: Shift=add, Alt=subtract, Shift+Alt=intersect, none=replace.
    private SelMode _selMode = SelMode.Replace;
    private byte[]? _baseSelMask;   // existing selection snapshot at gesture start (for combine)

    /// <summary>Read the combine mode from modifiers and snapshot the current selection.</summary>
    private void CaptureSelMode(CanvasMods mods)
    {
        bool shift = mods.HasFlag(CanvasMods.Shift), alt = mods.HasFlag(CanvasMods.Alt);
        _selMode = (shift, alt) switch
        {
            (true, true) => SelMode.Intersect,
            (true, false) => SelMode.Add,
            (false, true) => SelMode.Subtract,
            _ => SelMode.Replace
        };
        _baseSelMask = _selMode == SelMode.Replace ? null : _doc?.SnapshotSelectionMask();
    }

    /// <summary>Edge softness (px) applied to a selection; 0 = hard edge. Set from the options bar.</summary>
    public float SelectionFeather { get; set; }

    /// <summary>Last committed selection shape BEFORE feather (so the feather amount can be re-adjusted live).</summary>
    private byte[]? _lastSelShape;

    /// <summary>
    /// Re-feather the current selection to <paramref name="px"/> without redrawing it. Lets the
    /// options-bar Feather slider adjust an existing selection. Converts a plain rect to a mask.
    /// </summary>
    public void SetSelectionFeather(float px)
    {
        SelectionFeather = px;
        if (_doc is null) return;
        var baseM = _lastSelShape
            ?? (_doc.Selection is { W: > 0, H: > 0 } r ? Selections.Rect(_doc.Width, _doc.Height, r) : null);
        if (baseM is null) return;   // nothing selected

        _lastSelShape = baseM;
        int fr = (int)Math.Round(px);
        var shown = fr > 0 ? Selections.Feather(baseM, _doc.Width, _doc.Height, fr) : (byte[])baseM.Clone();
        _doc.SetMaskSelection(shown);
    }

    /// <summary>Commit a freshly-drawn coverage mask, combined with the gesture-start selection, then feathered.</summary>
    private void ApplyMask(byte[] newMask)
    {
        if (_doc is null) return;

        byte[] result;
        if (_selMode == SelMode.Replace)
        {
            result = newMask;
        }
        else if (_baseSelMask is null)
        {
            if (_selMode == SelMode.Add) result = newMask;
            else { _doc.ClearSelection(); return; }   // subtract/intersect from nothing = nothing
        }
        else
        {
            result = Selections.Combine(_baseSelMask, newMask, _selMode);
            _baseSelMask = null;
        }

        _lastSelShape = result;   // remember the hard shape for live feather re-adjust
        int fr = (int)Math.Round(SelectionFeather);
        var shown = fr > 0 ? Selections.Feather(result, _doc.Width, _doc.Height, fr) : result;
        _doc.SetMaskSelection(shown);
    }

    private StrokeSession? CreateSession()
    {
        if (ActiveLayer is not { } layer) return null;
        Brush.Clone = false;   // reset; clone configured per-stroke below
        Brush.Mode = ActiveTool switch
        {
            ToolKind.Dodge => BrushMode.Dodge,
            ToolKind.Burn => BrushMode.Burn,
            ToolKind.Sponge => BrushMode.Sponge,
            ToolKind.BlurBrush => BrushMode.Blur,
            ToolKind.SharpenBrush => BrushMode.Sharpen,
            ToolKind.Smudge => BrushMode.Smudge,
            _ => BrushMode.Paint
        };
        Brush.BeginStroke();
        bool maskMode = PaintMask;
        // pixel paint: capture the pre-stroke raster state (whole-raster undo, since the gesture
        // grows the buffer to paint then auto-crops it to content). Mask paint keeps tile undo.
        if (!maskMode)
        {
            if (layer.LockPixels) return null;   // locked pixels: no painting
            _strokeLayer = layer;
            _strokeBefore = RasterState.Capture(layer);
        }
        else _strokeLayer = null;
        // grow a sub-document / offset layer so it covers the whole canvas (keeps off-canvas pixels);
        // after this the buffer's (0,0) sits at (OffsetX,OffsetY) in document space.
        if (_doc is { } d) layer.ExpandToCover(d.Width, d.Height);
        Brush.OriginX = layer.OffsetX;
        Brush.OriginY = layer.OffsetY;
        // honor an active selection (paint only inside it): rect bbox + optional mask (doc-space)
        Brush.Clip = _doc?.Selection is { } s ? (s.X, s.Y, s.W, s.H) : null;
        Brush.ClipMask = _doc?.SelectionMask;
        Brush.ClipMaskW = _doc?.Width ?? 0;
        // brush color is user-chosen (black on a mask = hide, white = reveal)
        if (maskMode)
        {
            Brush.Erase = false;
            Brush.LockAlpha = false;
            if (!layer.HasMask) layer.AddWhiteMask(layer.Width, layer.Height);
            // mask upload is full-buffer for now (partial mask upload later)
            return new StrokeSession(layer.Mask!, layer.Width, layer.Height, Brush,
                _ => { layer.MaskDirty = true; layer.Dirty = true; }, layer.OffsetX, layer.OffsetY);
        }
        Brush.Erase = ActiveTool == ToolKind.Eraser;
        Brush.Pencil = ActiveTool == ToolKind.Pencil;   // hard aliased edge
        Brush.LockAlpha = layer.LockAlpha;   // preserve existing alpha (transparency lock)
        return new StrokeSession(layer.Pixels, layer.Width, layer.Height, Brush,
            tiles => layer.MarkTilesDirty(tiles), layer.OffsetX, layer.OffsetY);
    }

    // guide drag state: 0 = none, 1 = vertical guide (GuidesX), 2 = horizontal guide (GuidesY)
    private int _guideAxis;
    private int _guideIdx;

    /// <summary>Snap to guides / grid / document edges (View ▸ Snap). PLAN §2.5.</summary>
    public bool SnapEnabled { get; set; } = true;

    private double SnapAxis(double v, bool xAxis)
    {
        if (!SnapEnabled || _doc is not { } d) return v;
        double th = 6.0 / Math.Max(0.0001, EffectiveScale);   // ~6 screen px in doc units
        double best = v, bestD = th;
        void Try(double c) { double dd = Math.Abs(v - c); if (dd < bestD) { bestD = dd; best = c; } }
        Try(0); Try(xAxis ? d.Width : d.Height);                       // document edges
        foreach (var g in xAxis ? d.GuidesX : d.GuidesY) Try(g);       // guides
        if (ShowGrid && GridSpacing > 0) Try(Math.Round(v / GridSpacing) * GridSpacing);   // grid
        return best;
    }

    private (double x, double y) Snap(double dx, double dy) => (SnapAxis(dx, true), SnapAxis(dy, false));

    // smart-guide alignment lines (doc px) collected during the current move; drawn magenta
    private readonly List<float> _smartX = new();
    private readonly List<float> _smartY = new();

    /// <summary>
    /// Smart guides (PLAN §2.5): snap the moved layer's left/centre/right + top/centre/bottom to
    /// other layers' edges/centres and the document edges/centre, recording the alignment lines.
    /// Returns the snapped offset.
    /// </summary>
    private (double x, double y) SmartSnap(Sable.Engine.Layers.Layer moving, double rawX, double rawY)
    {
        _smartX.Clear(); _smartY.Clear();
        if (!SnapEnabled || _doc is not { } d) return (rawX, rawY);
        double th = 6.0 / Math.Max(0.0001, EffectiveScale);
        var cb = moving.ContentBounds(d.Width, d.Height);
        double mw = cb.w, mh = cb.h;

        var vcand = new List<double> { 0, d.Width / 2.0, d.Width };
        var hcand = new List<double> { 0, d.Height / 2.0, d.Height };
        CollectLayerLines(d.Layers, moving, vcand, hcand);

        double Best(double raw, double origin, double size, List<double> cand, List<float> lines)
        {
            double l = raw + origin, c = l + size / 2.0, r = l + size;
            double bestD = th, dxBest = 0; bool found = false; double line = 0;
            foreach (var cc in cand)
                foreach (var e in stackalloc[] { l, c, r })
                {
                    double dd = Math.Abs(e - cc);
                    if (dd < bestD) { bestD = dd; dxBest = cc - e; found = true; line = cc; }
                }
            if (found) { lines.Add((float)line); return raw + dxBest; }
            return raw;
        }

        double outX = Best(rawX, cb.x, mw, vcand, _smartX);
        double outY = Best(rawY, cb.y, mh, hcand, _smartY);
        return (outX, outY);
    }

    private void CollectLayerLines(List<Sable.Engine.Layers.Layer> layers, Sable.Engine.Layers.Layer moving,
        List<double> vcand, List<double> hcand)
    {
        foreach (var l in layers)
        {
            if (ReferenceEquals(l, moving) || !l.Visible) continue;
            if (l is Sable.Engine.Layers.GroupLayer g) { CollectLayerLines(g.Children, moving, vcand, hcand); continue; }
            if (l is not (Sable.Engine.Layers.PixelLayer or Sable.Engine.Layers.ShapeLayer or Sable.Engine.Layers.TextLayer)) continue;
            var cb = l.ContentBounds(_doc!.Width, _doc.Height);
            double L = l.OffsetX + cb.x, T = l.OffsetY + cb.y;
            vcand.Add(L); vcand.Add(L + cb.w / 2.0); vcand.Add(L + cb.w);
            hcand.Add(T); hcand.Add(T + cb.h / 2.0); hcand.Add(T + cb.h);
        }
    }

    /// <summary>Grab a guide line under the cursor (any tool) for move/delete. Returns true if grabbed.</summary>
    private bool TryGrabGuide(double dx, double dy)
    {
        if (_doc is not { } d) return false;
        double th = 5.0 / Math.Max(0.0001, EffectiveScale);   // ~5 screen px in doc units
        for (int i = 0; i < d.GuidesX.Count; i++)
            if (Math.Abs(dx - d.GuidesX[i]) <= th) { _guideAxis = 1; _guideIdx = i; return true; }
        for (int i = 0; i < d.GuidesY.Count; i++)
            if (Math.Abs(dy - d.GuidesY[i]) <= th) { _guideAxis = 2; _guideIdx = i; return true; }
        return false;
    }

    private void OnLeftDown(double sx, double sy, CanvasMods mods)
    {
        var (dx, dy) = MapToDoc(sx, sy);
        bool alt = mods.HasFlag(CanvasMods.Alt);

        // grab a guide line first (works under any tool)
        if (TryGrabGuide(dx, dy)) { _input?.Capture(); return; }
        bool brushy = ActiveTool is ToolKind.Brush or ToolKind.Pencil or ToolKind.Eraser
            or ToolKind.CloneStamp or ToolKind.Dodge or ToolKind.Burn or ToolKind.Sponge
            or ToolKind.BlurBrush or ToolKind.SharpenBrush or ToolKind.Smudge;

        // Affinity HUD: Ctrl+Alt + drag adjusts brush size/hardness (intercept before painting)
        if (mods.HasFlag(CanvasMods.Ctrl) && alt && brushy)
        {
            _hudAdjust = true; _input?.Capture();
            _input?.HideCursor();   // Affinity: hide the OS cursor; restore at start pos on release
            _hudStartSx = sx; _hudStartSy = sy;
            _hudStartRadius = Brush.Radius; _hudStartHardness = Brush.Hardness;
            return;
        }

        bool paintTool = ActiveTool is ToolKind.Brush or ToolKind.Pencil or ToolKind.Eraser or ToolKind.Fill;
        if (alt && ActiveLayer is not null && paintTool) { SampleColor(dx, dy); return; }
        // clone stamp: Alt+click sets the source point
        if (ActiveTool == ToolKind.CloneStamp && alt)
        {
            if (ActiveLayer is not null) { _cloneSrcX = dx; _cloneSrcY = dy; _cloneSet = true; }
            return;
        }

        switch (ActiveTool)
        {
            case ToolKind.Eyedropper:
                SampleColor(dx, dy);
                break;
            case ToolKind.Fill:
                DoFill(dx, dy);
                break;
            case ToolKind.Gradient:
                if (ActiveLayer is not null)
                {
                    _gradienting = true; _input?.Capture();
                    _gradStartDocX = dx; _gradStartDocY = dy;
                    _gradStartSx = sx; _gradStartSy = sy;
                    _gradEndSx = sx; _gradEndSy = sy;
                }
                break;
            case ToolKind.Crop:
                if (_doc is not null)
                {
                    _cropping = true; _input?.Capture();
                    _cropStartDocX = dx; _cropStartDocY = dy;
                    _cropRect = null;
                }
                break;
            case ToolKind.ShapeRect:
            case ToolKind.ShapeEllipse:
            case ToolKind.ShapeLine:
                if (_doc is not null)
                {
                    _shaping = true; _input?.Capture();
                    _shapeStartDocX = dx; _shapeStartDocY = dy;
                    _shapeStartSx = sx; _shapeStartSy = sy;
                    _shapeEndSx = sx; _shapeEndSy = sy;
                }
                break;
            case ToolKind.Zoom:
                ZoomAt(alt ? 1.0 / 1.1 : 1.1, _lastMouseX, _lastMouseY);
                break;
            case ToolKind.Hand:
                StartPan(sx, sy);
                break;
            case ToolKind.Transform:
                BeginTransform(sx, sy);
                break;
            case ToolKind.EllipseMarquee:
                if (_doc is not null)
                {
                    CaptureSelMode(mods);
                    _selecting = true; _input?.Capture();
                    _selStartX = dx; _selStartY = dy;
                    _doc.SelectionMask = null;
                }
                break;

            case ToolKind.Lasso:
                if (_doc is not null)
                {
                    CaptureSelMode(mods);
                    _lassoing = true; _input?.Capture();
                    _lassoPts.Clear(); _lassoPts.Add((dx, dy));
                    _doc.SelectionMask = null;
                }
                break;

            case ToolKind.MagicWand:
                if (_doc is not null && ActiveLayer is { } wl)
                {
                    CaptureSelMode(mods);
                    var m = Selections.Wand(wl.Pixels, wl.Width, wl.Height, (int)dx, (int)dy, 32);
                    ApplyMask(m);
                }
                break;

            case ToolKind.Marquee:
                if (_doc is not null)
                {
                    int hit = _doc.SelectionMask is null ? HitSelHandle(sx, sy) : -1;
                    if (hit is >= 0 and < 8 && _doc.Selection is { } rs)   // grip → resize
                    {
                        _selResizing = true; _input?.Capture();
                        _selL0 = rs.X; _selR0 = rs.Right; _selT0 = rs.Y; _selB0 = rs.Bottom;
                        _hL = hit is 0 or 6 or 7;
                        _hR = hit is 2 or 3 or 4;
                        _hT = hit is 0 or 1 or 2;
                        _hB = hit is 4 or 5 or 6;
                    }
                    else if (hit == 8 && _doc.Selection is { } ms)         // interior → move
                    {
                        _selMoving = true; _input?.Capture();
                        _selL0 = ms.X; _selR0 = ms.Right; _selT0 = ms.Y; _selB0 = ms.Bottom;
                        _selMoveStartX = dx; _selMoveStartY = dy;
                    }
                    else                                                   // empty → new selection
                    {
                        CaptureSelMode(mods);
                        _selecting = true; _input?.Capture();
                        _selStartX = dx; _selStartY = dy;
                        _doc.SelectionMask = null;
                    }
                }
                break;

            case ToolKind.Type:
                if (_doc is not null)
                {
                    var hitText = HitTextLayer(dx, dy);
                    if (hitText is not null) BeginTextEdit(hitText);   // click existing text → edit it
                    else
                    {
                        var t = new TextLayer("", (float)dx, (float)dy, TypeFontSize, Brush.R, Brush.G, Brush.B)
                        {
                            FontFamily = TypeFontFamily, Bold = TypeBold, Italic = TypeItalic,
                            Underline = TypeUnderline, Strikethrough = TypeStrike,
                            Align = (TextAlign)TypeAlign, LineSpacing = TypeLineSpacing
                        };
                        LayerProduced?.Invoke(t);
                        BeginTextEdit(t);
                    }
                }
                break;
            case ToolKind.Move:
                if (SelLayer is { } ml && !ml.LockPosition)
                {
                    _moving = true; _input?.Capture();
                    _moveStartX = dx; _moveStartY = dy;
                    _moveOrigX = ml.OffsetX; _moveOrigY = ml.OffsetY;
                }
                break;
            default: // Brush / Eraser / CloneStamp
                if (ActiveTool == ToolKind.CloneStamp && !_cloneSet) break;   // need a source first (Alt+click)
                _session = CreateSession();
                if (_session is not null)
                {
                    if (ActiveTool == ToolKind.CloneStamp && ActiveLayer is { } cl)
                    {
                        Brush.Clone = true;
                        Brush.CloneSrc = (byte[])cl.Pixels.Clone();   // snapshot avoids feedback during the stroke
                        Brush.CloneSrcW = cl.Width; Brush.CloneSrcH = cl.Height;
                        Brush.CloneOffX = (int)Math.Round(dx - _cloneSrcX);
                        Brush.CloneOffY = (int)Math.Round(dy - _cloneSrcY);
                    }
                    _input?.Capture();
                    _painting = true;
                    _lastDocX = dx; _lastDocY = dy;
                    _session.StrokeTo(dx, dy, dx, dy);
                }
                break;
        }
    }

    private void OnLeftUp()
    {
        if (_guideAxis != 0)
        {
            // dropped outside the document → delete the guide
            if (_doc is { } gd)
            {
                var (ux, uy) = MapToDoc(_lastMouseX, _lastMouseY);
                if (_guideAxis == 1 && (ux < 0 || ux > gd.Width) && _guideIdx < gd.GuidesX.Count) gd.GuidesX.RemoveAt(_guideIdx);
                else if (_guideAxis == 2 && (uy < 0 || uy > gd.Height) && _guideIdx < gd.GuidesY.Count) gd.GuidesY.RemoveAt(_guideIdx);
            }
            _guideAxis = 0; _input?.ReleaseCapture();
            return;
        }

        if (_hudAdjust)
        {
            _hudAdjust = false;
            _input?.RestoreCursor();   // warp the OS cursor back to where the drag began
            _input?.ReleaseCapture();
            BrushAdjusted?.Invoke();   // resync the options-bar sliders
            return;
        }
        if (_painting)
        {
            _painting = false;
            _input?.ReleaseCapture();
            Brush.Clone = false;   // clone is per-stroke; clear after
            if (_strokeLayer is { } pl)
            {
                // pixel paint: auto-crop the layer to its painted content, then record the whole
                // gesture (grow + paint + trim) as one undoable raster-state swap.
                _session = null;
                pl.TrimToContent();
                var after = RasterState.Capture(pl);
                CommandProduced?.Invoke(new RasterStateCommand(pl, _strokeBefore, after, () => pl.Dirty = true));
                _strokeLayer = null;
            }
            else
            {
                var cmd = _session?.Finalize();   // mask paint keeps tile-diff undo
                _session = null;
                if (cmd is not null) CommandProduced?.Invoke(cmd);
            }
        }
        else if (_moving && SelLayer is { } layer)
        {
            _moving = false;
            _smartX.Clear(); _smartY.Clear();   // hide alignment lines
            _input?.ReleaseCapture();
            if (_doc is not null && (layer.OffsetX != _moveOrigX || layer.OffsetY != _moveOrigY))
                CommandProduced?.Invoke(new MoveOffsetCommand(_doc, layer, _moveOrigX, _moveOrigY, layer.OffsetX, layer.OffsetY));
        }
        else if (_transforming && ActiveLayer is { } tl)
        {
            _transforming = false;
            _input?.ReleaseCapture();
            if (_doc is not null)
                CommandProduced?.Invoke(new TransformLayerCommand(_doc, tl, _xfStart, LayerXform.From(tl)));
        }
        else if (_gradienting && ActiveLayer is { } gl)
        {
            _gradienting = false;
            _input?.ReleaseCapture();
            var (ex, ey) = MapToDoc(_gradEndSx, _gradEndSy);
            var target = gl.Pixels;
            int w = gl.Width, h = gl.Height;
            var before = SnapshotAllTiles(target, w, h);
            var clip = _doc?.Selection is { } s ? ((int, int, int, int)?)(s.X, s.Y, s.W, s.H) : null;
            int changed = GradientTool.Apply(target, w, h, _gradStartDocX, _gradStartDocY, ex, ey,
                Gradient, clip, _doc?.SelectionMask, _doc?.Width ?? 0);
            if (changed > 0)
            {
                var after = SnapshotAllTiles(target, w, h);
                gl.MarkTilesDirty(after.Keys);
                CommandProduced?.Invoke(new PaintRasterCommand(target, w, h, before, after, t => gl.MarkTilesDirty(t)));
            }
        }
        else if (_lassoing)
        {
            _lassoing = false;
            _input?.ReleaseCapture();
            if (_doc is not null)
            {
                if (_lassoPts.Count >= 3)
                    ApplyMask(Selections.Polygon(_doc.Width, _doc.Height, _lassoPts));
                else if (_selMode == SelMode.Replace) _doc.ClearSelection();
            }
            _lassoPts.Clear();
        }
        else if (_selecting || _selResizing || _selMoving)
        {
            bool wasNewDraw = _selecting;   // a fresh drag (not grip resize/move)
            bool wasEllipse = _selecting && ActiveTool == ToolKind.EllipseMarquee;
            _selecting = _selResizing = _selMoving = false;
            _input?.ReleaseCapture();
            if (_doc?.Selection is { W: < 3 } or { H: < 3 } && _selMode == SelMode.Replace)
            {
                _doc!.ClearSelection();
            }
            else if (wasEllipse && _doc?.Selection is { } e)
            {
                ApplyMask(Selections.Ellipse(_doc.Width, _doc.Height, e));
            }
            else if (wasNewDraw && _selMode != SelMode.Replace && _doc?.Selection is { } r)
            {
                // rect marquee with a modifier → rasterize + combine (loses grips, like a mask sel)
                ApplyMask(Selections.Rect(_doc.Width, _doc.Height, r));
            }
            else if (wasNewDraw && SelectionFeather > 0 && _doc?.Selection is { } rf)
            {
                // feathered rect → becomes a soft coverage mask (grips drop, like other masks)
                ApplyMask(Selections.Rect(_doc.Width, _doc.Height, rf));
            }
            // else: plain rect Replace keeps Selection rect + null mask (grips stay editable)
        }
        else if (_cropping)
        {
            _cropping = false;
            _input?.ReleaseCapture();
            if (_cropRect is { W: < 3 } or { H: < 3 }) _cropRect = null;   // too small → discard
        }
        else if (_shaping && _doc is not null)
        {
            _shaping = false;
            _input?.ReleaseCapture();
            var (ex, ey) = MapToDoc(_shapeEndSx, _shapeEndSy);
            var kind = ActiveTool switch
            {
                ToolKind.ShapeEllipse => ShapeKind.Ellipse,
                ToolKind.ShapeLine => ShapeKind.Line,
                _ => ShapeKind.Rectangle
            };
            float sx0 = (float)_shapeStartDocX, sy0 = (float)_shapeStartDocY;
            float sw = (float)(ex - sx0), sh = (float)(ey - sy0);
            if (Math.Abs(sw) >= 2 || Math.Abs(sh) >= 2)   // ignore a tiny accidental drag
            {
                // each shape is its own PARAMETRIC layer (editable fill + tight bounds; Move grabs it)
                var shape = new ShapeLayer(kind, sx0, sy0, sw, sh, Brush.R, Brush.G, Brush.B)
                {
                    StrokeWidth = (float)(Brush.Radius * 2)
                };
                LayerProduced?.Invoke(shape);
            }
        }
        else if (_panningMouse)
        {
            _panningMouse = false;
            _input?.ReleaseCapture();
        }
    }

    /// <summary>Commit the pending crop rectangle (Enter), resizing the document. Undoable.</summary>
    public void CommitCrop()
    {
        if (_doc is null || _cropRect is not { } r || r.W < 1 || r.H < 1) return;
        CommandProduced?.Invoke(new CropCommand(_doc, r.X, r.Y, r.W, r.H));
        _cropRect = null;
        ResetView();
    }

    /// <summary>Discard a pending crop rectangle (Esc).</summary>
    public void CancelCrop() => _cropRect = null;

    /// <summary>Clear any active selection.</summary>
    public void Deselect() { _doc?.ClearSelection(); _lastSelShape = null; }

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

    /// <summary>Copy the selected region of the active layer (or the whole layer if no selection) → RGBA8 region.</summary>
    public (byte[] px, int w, int h)? CopyRegion()
    {
        if (_doc is null || ActiveLayer is not { } layer) return null;
        if (_doc.Selection is { } sel && sel.W > 0 && sel.H > 0)
        {
            var mask = _doc.SelectionMask; int mw = _doc.Width;
            int x0 = Math.Max(0, sel.X), y0 = Math.Max(0, sel.Y);
            int x1 = Math.Min(layer.Width, sel.Right), y1 = Math.Min(layer.Height, sel.Bottom);
            int w = x1 - x0, h = y1 - y0;
            if (w <= 0 || h <= 0) return null;
            var src = layer.Pixels; var outp = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int si = ((y0 + y) * layer.Width + (x0 + x)) * 4;
                int di = (y * w + x) * 4;
                byte cov = mask is not null ? mask[(y0 + y) * mw + (x0 + x)] : (byte)255;
                outp[di] = src[si]; outp[di + 1] = src[si + 1]; outp[di + 2] = src[si + 2];
                outp[di + 3] = (byte)(src[si + 3] * cov / 255);
            }
            return (outp, w, h);
        }
        return ((byte[])layer.Pixels.Clone(), layer.Width, layer.Height);
    }

    /// <summary>Copy-merged: the flattened composite, cropped to the selection (or whole doc) → RGBA8 region.</summary>
    public (byte[] px, int w, int h)? CopyMerged()
    {
        var comp = ReadComposite();
        if (comp is null || _doc is null) return null;
        if (_doc.Selection is { } sel && sel.W > 0 && sel.H > 0)
        {
            var mask = _doc.SelectionMask; int mw = _doc.Width;
            int x0 = Math.Max(0, sel.X), y0 = Math.Max(0, sel.Y);
            int x1 = Math.Min(_doc.Width, sel.Right), y1 = Math.Min(_doc.Height, sel.Bottom);
            int w = x1 - x0, h = y1 - y0;
            if (w <= 0 || h <= 0) return null;
            var outp = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int si = ((y0 + y) * _doc.Width + (x0 + x)) * 4;
                int di = (y * w + x) * 4;
                byte cov = mask is not null ? mask[(y0 + y) * mw + (x0 + x)] : (byte)255;
                outp[di] = comp[si]; outp[di + 1] = comp[si + 1]; outp[di + 2] = comp[si + 2];
                outp[di + 3] = (byte)(comp[si + 3] * cov / 255);
            }
            return (outp, w, h);
        }
        return (comp, _doc.Width, _doc.Height);
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

    private void BeginTransform(double sx, double sy)
    {
        if (ActiveLayer is not { } l) return;
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
        _input?.Capture();
        _xfStart = LayerXform.From(l);
        _xfCenterX = cx; _xfCenterY = cy;
        _xfStartAngle = Math.Atan2(sy - cy, sx - cx);
        _xfStartDist = Math.Max(1e-3, Dist(sx, sy, cx, cy));
        _xfStartSx = l.ScaleX; _xfStartSy = l.ScaleY;
        var (ddx, ddy) = MapToDoc(sx, sy);
        _xfMoveDocX = ddx; _xfMoveDocY = ddy;
        _xfOrigOffX = l.OffsetX; _xfOrigOffY = l.OffsetY;
    }

    private void TransformDrag(double sx, double sy)
    {
        if (ActiveLayer is not { } l) return;
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
                var (dx, dy) = MapToDoc(sx, sy);
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

    private void StartPan(double sx, double sy)
    {
        _lastPanX = (int)sx;
        _lastPanY = (int)sy;
        _panningMouse = true;
        _input?.Capture();
    }

    private void DoFill(double dx, double dy)
    {
        if (ActiveLayer is not { } layer || layer.LockPixels) return;   // locked pixels: no fill
        var beforeState = RasterState.Capture(layer);                   // pre-expand state (whole-raster undo)
        if (_doc is { } d) layer.ExpandToCover(d.Width, d.Height);      // fillable across the canvas
        int ox = layer.OffsetX, oy = layer.OffsetY;
        var clip = _doc?.Selection is { } s ? ((int, int, int, int)?)(s.X, s.Y, s.W, s.H) : null;
        int changed = FillTool.Flood(layer.Pixels, layer.Width, layer.Height, (int)dx - ox, (int)dy - oy,
            Brush.R, Brush.G, Brush.B, 255, 32, clip, _doc?.SelectionMask, _doc?.Width ?? 0, ox, oy);
        layer.TrimToContent();   // auto-crop to content (also undoes the expand when nothing changed)
        if (changed == 0) { layer.Dirty = true; return; }
        layer.Dirty = true;
        CommandProduced?.Invoke(new RasterStateCommand(layer, beforeState, RasterState.Capture(layer), () => layer.Dirty = true));
    }

    private static Dictionary<(int, int), byte[]> SnapshotAllTiles(byte[] px, int w, int h)
    {
        var snap = new Dictionary<(int, int), byte[]>();
        for (int ty = 0; ty < RasterTiles.TilesY(h); ty++)
        for (int tx = 0; tx < RasterTiles.TilesX(w); tx++)
            snap[(tx, ty)] = RasterTiles.GetTile(px, w, h, tx, ty);
        return snap;
    }

    /// <summary>Eyedropper sample radius: 0 = point, 1 = 3×3 avg, 2 = 5×5 avg.</summary>
    public int EyedropperRadius { get; set; }
    /// <summary>Eyedropper samples the merged composite instead of the active layer.</summary>
    public bool EyedropperAllLayers { get; set; }

    private void SampleColor(double dx, double dy)
    {
        byte[]? src; int sw, sh, ox = 0, oy = 0;
        if (EyedropperAllLayers && ReadComposite() is { } comp && _doc is not null)
        { src = comp; sw = _doc.Width; sh = _doc.Height; }
        else if (ActiveLayer is { } layer)
        { src = layer.Pixels; sw = layer.Width; sh = layer.Height; ox = layer.OffsetX; oy = layer.OffsetY; }
        else return;

        // sample in buffer space (doc cursor minus the layer's origin)
        int cx = (int)Math.Clamp(dx - ox, 0, sw - 1), cy = (int)Math.Clamp(dy - oy, 0, sh - 1);
        int rad = Math.Clamp(EyedropperRadius, 0, 4);
        long rr = 0, gg = 0, bb = 0; int n = 0;
        for (int yy = cy - rad; yy <= cy + rad; yy++)
        for (int xx = cx - rad; xx <= cx + rad; xx++)
        {
            if (xx < 0 || yy < 0 || xx >= sw || yy >= sh) continue;
            int i = (yy * sw + xx) * 4;
            rr += src[i]; gg += src[i + 1]; bb += src[i + 2]; n++;
        }
        if (n == 0) return;
        byte r = (byte)(rr / n), g = (byte)(gg / n), b = (byte)(bb / n);
        Brush.R = r; Brush.G = g; Brush.B = b;
        ColorPicked?.Invoke(r, g, b);
    }

    /// <summary>Map a surface-pixel point to document pixels via the inverse viewport transform.</summary>
    private (double x, double y) MapToDoc(double sx, double sy)
    {
        var vp = ComputeViewport();
        double scale = vp.Scale > 0 ? vp.Scale : 1;
        return ((sx - vp.Ox) / scale, (sy - vp.Oy) / scale);
    }
}
