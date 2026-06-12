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
    /// <summary>Stroke stabilizer strength 0..1 (0 = off): smooths paint input toward the cursor.</summary>
    public float Stabilizer { get; set; }
    /// <summary>Gradient-tool geometry (linear/radial/conical/reflected/diamond), set from the options bar.</summary>
    public GradientShape GradientShape { get; set; } = GradientShape.Linear;
    private double _smoothX, _smoothY;             // stabilizer state (per stroke)
    private float _lastPressure = 1f;              // stylus pressure at the previous paint point
    private bool _hudAdjust;                       // Ctrl+Alt brush size/hardness HUD
    private double _hudStartSx, _hudStartSy;
    private float _hudStartRadius, _hudStartHardness;
    private double _lastDocX, _lastDocY;
    private IStrokeSession? _session;
    private bool _gpuStrokeDirty;              // a GPU dab landed since the last composite
    private PixelLayer? _strokeLayer;          // active pixel-paint layer (null for mask paint)
    private RasterState _strokeBefore;         // pre-stroke raster snapshot (whole-raster undo + auto-crop)
    private bool _panningMouse;
    private double _lastPanX, _lastPanY;   // keep fractional surface coords (no per-move truncation drift)
    private bool _moving;
    private double _moveStartX, _moveStartY;
    private int _moveOrigX, _moveOrigY;
    private bool _gradienting;
    private double _gradStartDocX, _gradStartDocY;       // gradient start (doc px)
    private double _gradStartSx, _gradStartSy, _gradEndSx, _gradEndSy;   // line ends (surface px, overlay)
    private bool _cropping;
    private bool _zoomScrub, _zoomScrubMoved, _zoomScrubAlt;     // scrubby zoom gesture
    private double _zoomScrubLastX, _zoomScrubAnchorX, _zoomScrubAnchorY;
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
    private readonly List<(double X, double Y)> _polyPts = new();   // polygonal lasso vertices
    // GIMP-style selection grips
    private bool _selResizing, _selMoving;
    private bool _hL, _hR, _hT, _hB;            // which edges follow the cursor
    private double _selL0, _selR0, _selT0, _selB0;
    private double _selMoveStartX, _selMoveStartY;
    private bool _maskMoving;                    // moving a non-rect (mask) selection
    private byte[]? _maskMoveOrig;
    private double _maskMoveStartX, _maskMoveStartY;
    private bool _patching;                       // patch tool: dragging the selection to a source
    private double _patchStartX, _patchStartY;
    private byte[]? _patchSrc;                     // immutable pixel snapshot at gesture start (stable preview)
    private RasterState _patchBefore;             // pre-gesture raster state (whole-raster undo)
    private SelRect? _patchRect;                   // selection region captured at gesture start (heal target)
    private byte[]? _patchMask;
    private CanvasMods _lastMods;                  // modifiers from the most recent pointer event
    private bool _liquifying;                      // liquify displacement stroke
    private PixelLayer? _liquifyLayer;
    private RasterState _liquifyBefore;

    /// <summary>Liquify brush mode (push/bloat/pucker/twirl). Set from the options bar.</summary>
    public LiquifyMode LiquifyMode { get; set; } = LiquifyMode.Push;
    /// <summary>Liquify strength 0..1 (options bar).</summary>
    public float LiquifyStrength { get; set; } = 0.5f;

    // transform gizmo state
    private const float RotHandleDist = 28f;
    private bool _transforming;
    private int _xfMode;                 // 1 = move, 2 = rotate, 3 = scale (handle-driven), 5 = perspective
    private int _xfHandle;               // scale handle: 0..3 = corner TL,TR,BR,BL; 10..13 = edge top,right,bottom,left
    private int _xfBoxW, _xfBoxH;        // layer local box size at gesture start
    private LayerXform _xfStart;
    private double _xfCenterX, _xfCenterY, _xfStartAngle;
    private double _xfMoveDocX, _xfMoveDocY;
    private int _xfOrigOffX, _xfOrigOffY;

    /// <summary>The active PIXEL layer for paint (brush/fill/eyedropper). Null if a non-pixel layer is selected.</summary>
    private PixelLayer? _activeLayer;
    public PixelLayer? ActiveLayer
    {
        get => _activeLayer;
        set
        {
            if (ReferenceEquals(_activeLayer, value)) return;
            _activeLayer = value;
            _cloneSet = false;   // clone/heal source belongs to the previous layer
        }
    }

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
            if (_activeTool == ToolKind.Pen && value != ToolKind.Pen) { PenUp(); CommitPen(); }   // finalize any handle-drag, then commit the path
            if (_activeTool == ToolKind.MeshWarp && value != ToolKind.MeshWarp) CancelMeshWarp();
            AbortGesture();   // a held-button tool-cycle (keys → window, mouse → native HWND) must not run the old gesture under the new tool
            _activeTool = value;
            if (value == ToolKind.MeshWarp) BeginMeshWarp();
            if (value != ToolKind.Type) CommitTextEdit();   // leaving Type ends editing
            ToolChanged?.Invoke(value);
        }
    }

    /// <summary>
    /// Tear down ANY in-progress pointer gesture WITHOUT committing it: discards the partial
    /// edit, clears every gesture flag + dangling layer ref, releases pointer capture. Called on
    /// doc/tab swap and mid-gesture tool switch so a stale gesture can't write into a detached
    /// layer or NRE on the next pointer event. Pen/Mesh have their own commit/cancel (run first).
    /// </summary>
    private void AbortGesture()
    {
        if (_hudAdjust) _input?.RestoreCursor();
        _painting = false; _hudAdjust = false;
        _compositor?.CancelBrushStroke();   // a GPU stroke must not outlive its gesture
        _session = null; _strokeLayer = null;
        _panningMouse = false;
        _moving = false;
        _gradienting = false;
        _shaping = false;
        _cropping = false; _cropRect = null;
        _zoomScrub = false;
        _selecting = false;
        _lassoing = false; _lassoPts.Clear();
        _polyPts.Clear();                   // discard a pending polygonal-lasso
        _selResizing = false; _selMoving = false;
        _maskMoving = false; _maskMoveOrig = null;
        _patching = false; _patchSrc = null; _patchRect = null; _patchMask = null;
        _liquifying = false; _liquifyLayer = null;
        _transforming = false;
        _smartX.Clear(); _smartY.Clear();   // hide snap alignment lines
        _nodeDragging = false; _nodeIdx = -1; _nodePath = null; _nodeBefore = null;
        _meshDragIdx = -1;
        _cloneSet = false;                  // stale clone source must not bleed across docs/tools
        Brush.Clone = false; Brush.Heal = false;
        _input?.ReleaseCapture();
    }

    /// <summary>Raised when the active tool changes (so the toolbar can sync highlight).</summary>
    public event Action<ToolKind>? ToolChanged;

    /// <summary>When true the brush edits the layer's mask (black hides) instead of its pixels.</summary>
    public bool PaintMask { get; set; }

    public BrushTool Brush { get; } = new();

    /// <summary>Draw-time defaults for the Shape tools (fill/stroke/dash/per-kind), set from the options bar.</summary>
    public ShapeStyle Shape { get; } = new();

    /// <summary>The gradient the Gradient tool paints (edited in the Gradients panel).</summary>
    public GradientDef Gradient { get; } =
        new(new GradientStop(0f, 0, 0, 0, 255), new GradientStop(1f, 255, 255, 255, 255));

    /// <summary>Raised (R,G,B) when the eyedropper (Alt+click) samples a color.</summary>
    public Action<byte, byte, byte>? ColorPicked { get; set; }

    /// <summary>Background colour (foreground = <see cref="Brush"/> R/G/B). For swap + gradient bg.</summary>
    public byte BgR { get; set; } = 255;
    public byte BgG { get; set; } = 255;
    public byte BgB { get; set; } = 255;

    /// <summary>Swap foreground/background colours (X). Raises ColorPicked with the new foreground.</summary>
    public void SwapColors()
    {
        (Brush.R, BgR) = (BgR, Brush.R);
        (Brush.G, BgG) = (BgG, Brush.G);
        (Brush.B, BgB) = (BgB, Brush.B);
        ColorPicked?.Invoke(Brush.R, Brush.G, Brush.B);
    }

    /// <summary>Reset to default black foreground / white background (D).</summary>
    public void ResetColors()
    {
        Brush.R = 0; Brush.G = 0; Brush.B = 0;
        BgR = BgG = BgB = 255;
        ColorPicked?.Invoke(Brush.R, Brush.G, Brush.B);
    }

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
    public float TypeBoxWidth { get; set; }     // 0 = point text; >0 = area text wrap width
    public float TypeTracking { get; set; }     // letter spacing px

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
        return HitTextIn(_doc.Layers, dx, dy, 0, 0);
    }

    // recursive: text layers nested in groups (e.g. PSD imports) must be hittable too;
    // parent offsets accumulate so grouped/offset content still maps correctly
    private TextLayer? HitTextIn(System.Collections.Generic.List<Layer> layers, double dx, double dy, int ox, int oy)
    {
        for (int i = layers.Count - 1; i >= 0; i--)   // top-down
        {
            var l = layers[i];
            if (!l.Visible) continue;
            if (l.HasChildren && HitTextIn(l.Children, dx, dy, ox + l.OffsetX, oy + l.OffsetY) is { } inner)
                return inner;
            if (l is TextLayer t)
            {
                var (bx, by, bw, bh) = t.ContentBounds(_doc!.Width, _doc.Height);
                if (dx >= bx + ox + t.OffsetX && dx < bx + ox + t.OffsetX + bw &&
                    dy >= by + oy + t.OffsetY && dy < by + oy + t.OffsetY + bh) return t;
            }
        }
        return null;
    }

    // --- ICanvasInputSink: OS-agnostic pointer handlers (surface pixels) ----------

    /// <summary>
    /// True when the eyedropper is sampling: the dedicated tool, or Alt held over a paint tool
    /// (matches the <see cref="SampleColor"/> entry conditions in <see cref="OnLeftDown"/>).
    /// Drives the loupe overlay + suppresses the brush ring/dab while sampling.
    /// </summary>
    private bool EyedropperSampling =>
        ActiveTool == ToolKind.Eyedropper
        // Alt-click samples colour — but ONLY when Alt is the sole modifier. Ctrl+Alt is the
        // brush size/hardness HUD (and Shift+Alt etc. aren't sampling), so don't show the loupe then.
        || (_lastMods == CanvasMods.Alt && ActiveLayer is not null
            && ActiveTool is ToolKind.Brush or ToolKind.Pencil or ToolKind.Eraser or ToolKind.Fill);

    void ICanvasInputSink.PointerDown(CanvasButton button, double sx, double sy, CanvasMods mods)
    {
        _lastMouseX = sx; _lastMouseY = sy; _lastMods = mods;
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
        _lastMouseX = sx; _lastMouseY = sy; _lastMods = mods;
        ZoomAt(delta > 0 ? 1.1 : 1.0 / 1.1, sx, sy);
    }

    void ICanvasInputSink.PointerMove(double sx, double sy, CanvasMods mods)
    {
        _lastMouseX = sx; _lastMouseY = sy; _lastMods = mods;
        if (CursorDocMoved is not null) { var (cdx, cdy) = MapToDoc(sx, sy); CursorDocMoved(cdx, cdy); }

        // AI hover-select: highlight the object under the cursor (no drag/paint for this tool).
        // Don't intercept while middle-drag panning so the canvas can still move.
        if (ActiveTool == Sable.Tools.ToolKind.SmartSelect && HasSmartObjects && _guideAxis == 0 && !_panningMouse)
        {
            var (mdx, mdy) = MapToDoc(sx, sy);
            UpdateSmartHover(mdx, mdy, mods);
            return;
        }

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
            // stroke stabilizer: exponential pull toward the cursor smooths hand jitter
            if (Stabilizer > 0f)
            {
                double k = 1.0 - Math.Min(0.95, Stabilizer * 0.9);
                _smoothX += (dx - _smoothX) * k;
                _smoothY += (dy - _smoothY) * k;
                dx = _smoothX; dy = _smoothY;
            }
            float p = _input?.Pressure ?? 1f;
            _session?.StrokeTo(_lastDocX, _lastDocY, dx, dy, _lastPressure, p);
            _lastPressure = p;
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
        else if (_maskMoving && _doc is not null && _maskMoveOrig is not null)
        {
            var (dx, dy) = MapToDoc(sx, sy);
            var shifted = Selections.Shift(_maskMoveOrig, _doc.Width, _doc.Height,
                (int)Math.Round(dx - _maskMoveStartX), (int)Math.Round(dy - _maskMoveStartY));
            _doc.SetSelectionMaskLive(shifted);
        }
        else if (_zoomScrub)
        {
            double dxz = sx - _zoomScrubLastX;
            if (Math.Abs(dxz) > 0.01)
            {
                if (Math.Abs(sx - _zoomScrubAnchorX) > 3) _zoomScrubMoved = true;
                ZoomAt(Math.Pow(1.01, dxz), _zoomScrubAnchorX, _zoomScrubAnchorY);
                _zoomScrubLastX = sx;
            }
        }
        else if (_gradienting)
        {
            _gradEndSx = sx; _gradEndSy = sy;   // track for the live line overlay
        }
        else if (_cropping && _doc is not null)
        {
            var (dx, dy) = MapToDoc(sx, sy);
            // aspect-constrained crop: snap the dragged corner to the chosen ratio (w/h)
            if (CropAspect > 0)
            {
                double w = Math.Abs(dx - _cropStartDocX);
                double h = w / CropAspect;
                dx = _cropStartDocX + (dx >= _cropStartDocX ? w : -w);
                dy = _cropStartDocY + (dy >= _cropStartDocY ? h : -h);
            }
            _cropRect = SelRect.FromCorners(_cropStartDocX, _cropStartDocY, dx, dy, _doc.Width, _doc.Height);
        }
        else if (_shaping)
        {
            _shapeEndSx = sx; _shapeEndSy = sy;   // track for the live outline overlay
        }
        else if (_penDragging)
        {
            PenMove(sx, sy);
        }
        else if (_nodeDragging)
        {
            NodeMove(sx, sy);
        }
        else if (_meshDragIdx >= 0)
        {
            MeshDrag(sx, sy);
        }
        else if (_liquifying && _liquifyLayer is { } lq)
        {
            var (dx, dy) = MapToDoc(sx, sy);
            double ddx = dx - _lastDocX, ddy = dy - _lastDocY;
            // map doc → layer-buffer space (layer may be offset)
            double lcx = dx - lq.OffsetX, lcy = dy - lq.OffsetY;
            LiquifyTool.Stamp(lq.Pixels, lq.Width, lq.Height, lcx, lcy, ddx, ddy,
                LiquifyMode, LiquifyStrength, Brush.Radius, Brush.Hardness);
            lq.Dirty = true;
            _doc?.MarkStructureChanged();
            _lastDocX = dx; _lastDocY = dy;
        }
        else if (_patching && _doc is { } pdoc && ActiveLayer is not null)
        {
            var (pdx, pdy) = MapToDoc(sx, sy);   // live heal preview following the drag
            int poffX = (int)Math.Round(pdx - _patchStartX), poffY = (int)Math.Round(pdy - _patchStartY);
            ApplyPatchPreview(poffX, poffY);
            // move the marching ants to the SOURCE region so the user can aim at a clean area
            if (_patchMask is { } pmk)
                pdoc.SetSelectionMaskLive(Selections.Shift(pmk, pdoc.Width, pdoc.Height, poffX, poffY));
            else if (_patchRect is { } prc)
                pdoc.Selection = SelRect.FromCorners(prc.X + poffX, prc.Y + poffY, prc.Right + poffX, prc.Bottom + poffY, pdoc.Width, pdoc.Height);
        }
        else if (_panningMouse)
        {
            PanBy(sx - _lastPanX, sy - _lastPanY);
            _lastPanX = sx; _lastPanY = sy;
        }
    }

    // selection combine: Shift=add, Alt=subtract, Shift+Alt=intersect, none=replace.
    private SelMode _selMode = SelMode.Replace;
    private byte[]? _baseSelMask;   // existing selection snapshot at gesture start (for combine)

    private static SelMode SelModeFrom(CanvasMods mods)
        => (mods.HasFlag(CanvasMods.Shift), mods.HasFlag(CanvasMods.Alt)) switch
        {
            (true, true) => SelMode.Intersect,
            (true, false) => SelMode.Add,
            (false, true) => SelMode.Subtract,
            _ => SelMode.Replace
        };

    /// <summary>Read the combine mode from modifiers and snapshot the current selection.</summary>
    private void CaptureSelMode(CanvasMods mods)
    {
        _selMode = SelModeFrom(mods);
        _baseSelMask = _selMode == SelMode.Replace ? null : _doc?.SnapshotSelectionMask();
    }

    private byte[]? _polyBase;   // selection before a polygonal-lasso started (combine mode read on the closing click)

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

    /// <summary>GPU brush engine master switch (plan §2): pixel-paint strokes stamp dabs in
    /// compute and read back once at stroke end. Mask/quick-mask painting stays CPU.</summary>
    public bool GpuBrush { get; set; } = true;

    private IStrokeSession? CreateSession()
    {
        // quick-mask mode: the brush edits the selection (white = add, eraser/black = remove)
        if (QuickMask)
        {
            if (_qmask is null || _doc is null) return null;
            Brush.BeginStroke();
            Brush.Mode = BrushMode.Paint;
            Brush.Clone = false; Brush.Erase = false; Brush.Pencil = false; Brush.LockAlpha = false;
            Brush.OriginX = 0; Brush.OriginY = 0;
            Brush.Clip = null; Brush.ClipMask = null;
            bool remove = ActiveTool == ToolKind.Eraser;
            Brush.R = Brush.G = Brush.B = (byte)(remove ? 0 : 255);   // black removes, white adds (src-over on R)
            return new StrokeSession(_qmask, _doc.Width, _doc.Height, Brush, _ => SyncQuickMask());
        }

        if (ActiveLayer is not { } layer) return null;
        Brush.Clone = false; Brush.Heal = false;   // reset; clone/heal configured per-stroke below
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
                _ => { layer.MaskDirty = true; layer.Dirty = true; }, layer.OffsetX, layer.OffsetY,
                () => layer.Mask);
        }
        Brush.Erase = ActiveTool == ToolKind.Eraser;
        Brush.Pencil = ActiveTool == ToolKind.Pencil;   // hard aliased edge
        Brush.LockAlpha = layer.LockAlpha;   // preserve existing alpha (transparency lock)
        return new StrokeSession(layer.Pixels, layer.Width, layer.Height, Brush,
            tiles => layer.MarkTilesDirty(tiles), layer.OffsetX, layer.OffsetY,
            () => layer.Pixels);
    }

    /// <summary>Upgrade a pixel-paint gesture to the GPU stroke pipeline. Clone/heal must be
    /// configured on the Brush BEFORE this (the snapshot bind happens at begin). Returns the
    /// CPU session unchanged when the GPU engine is off/unavailable or for mask targets.</summary>
    private IStrokeSession? UpgradeToGpuSession(IStrokeSession? cpu)
    {
        if (!GpuBrush || cpu is null || _compositor is null || _doc is null) return cpu;
        if (_strokeLayer is not { } layer) return cpu;   // mask/quick-mask paint stays CPU
        try
        {
            return new GpuBrushSession(_compositor, layer, Brush, _doc, () => _gpuStrokeDirty = true);
        }
        catch
        {
            return cpu;   // GPU stroke setup failed → CPU fallback, painting must never break
        }
    }

    // guide drag state: 0 = none, 1 = vertical guide (GuidesX), 2 = horizontal guide (GuidesY)
    private int _guideAxis;
    private int _guideIdx;

    /// <summary>Snapping master toggle (View ▸ Snapping). PLAN §2.5.</summary>
    public bool SnapEnabled { get; set; } = true;
    /// <summary>Snap pull distance in SCREEN px (converted to doc units by the live scale).</summary>
    public double SnapTolerance { get; set; } = 6.0;
    public bool SnapToGrid { get; set; } = true;     // grid lines
    public bool SnapToGuides { get; set; } = true;   // user guides
    public bool SnapToCanvas { get; set; } = true;   // document/page edges + centre
    public bool SnapToObjects { get; set; } = true;  // other layers' bounding boxes + mid points
    public bool SnapVisibleOnly { get; set; } = true;// ignore hidden layers as snap targets

    private double SnapDocTolerance => SnapTolerance / Math.Max(0.0001, EffectiveScale);

    private double SnapAxis(double v, bool xAxis)
    {
        if (!SnapEnabled || _doc is not { } d) return v;
        double th = SnapDocTolerance;
        double best = v, bestD = th;
        void Try(double c) { double dd = Math.Abs(v - c); if (dd < bestD) { bestD = dd; best = c; } }
        if (SnapToCanvas) { Try(0); Try(xAxis ? d.Width : d.Height); }                    // document edges
        if (SnapToGuides) foreach (var g in xAxis ? d.GuidesX : d.GuidesY) Try(g);        // guides
        if (SnapToGrid && GridSpacing > 0) Try(Math.Round(v / GridSpacing) * GridSpacing);// grid (independent of grid visibility)
        return best;
    }

    private (double x, double y) Snap(double dx, double dy) => (SnapAxis(dx, true), SnapAxis(dy, false));

    /// <summary>Snap a transform scale handle's document position to the canvas borders + centre + guides,
    /// recording the matched alignment line (so the magenta guide shows). Returns the snapped coord.</summary>
    private double SnapScaleHandle(double v, bool xAxis, List<float> lines)
    {
        if (!SnapEnabled || _doc is not { } d) return v;
        double th = SnapDocTolerance;
        double best = v, bestD = th, line = 0; bool found = false;
        void Try(double c) { double dd = Math.Abs(v - c); if (dd < bestD) { bestD = dd; best = c; found = true; line = c; } }
        double ext = xAxis ? d.Width : d.Height;
        if (SnapToCanvas) { Try(0); Try(ext); Try(ext / 2.0); }        // canvas borders + centre
        if (SnapToGuides) foreach (var g in xAxis ? d.GuidesX : d.GuidesY) Try(g);   // guides
        if (SnapToGrid && GridSpacing > 0) Try(Math.Round(v / GridSpacing) * GridSpacing);
        if (found) lines.Add((float)line);
        return best;
    }

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
        double th = SnapDocTolerance;
        var cb = moving.ContentBounds(d.Width, d.Height);
        double mw = cb.w, mh = cb.h;

        var vcand = new List<double>();
        var hcand = new List<double>();
        if (SnapToCanvas) { vcand.Add(0); vcand.Add(d.Width / 2.0); vcand.Add(d.Width); hcand.Add(0); hcand.Add(d.Height / 2.0); hcand.Add(d.Height); }
        if (SnapToGuides) { foreach (var g in d.GuidesX) vcand.Add(g); foreach (var g in d.GuidesY) hcand.Add(g); }
        if (SnapToObjects) CollectLayerLines(d.Layers, moving, vcand, hcand);
        if (SnapToGrid && GridSpacing > 0)
        {
            // grid candidates near the moving layer's current edges (left/centre/right, top/centre/bottom)
            double sp = GridSpacing;
            double lx = rawX + cb.x, ty = rawY + cb.y;
            foreach (var e in stackalloc[] { lx, lx + mw / 2.0, lx + mw }) vcand.Add(Math.Round(e / sp) * sp);
            foreach (var e in stackalloc[] { ty, ty + mh / 2.0, ty + mh }) hcand.Add(Math.Round(e / sp) * sp);
        }

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
            if (ReferenceEquals(l, moving)) continue;
            if (SnapVisibleOnly && !l.Visible) continue;
            if (l is Sable.Engine.Layers.GroupLayer g) { CollectLayerLines(g.Children, moving, vcand, hcand); continue; }
            if (l is not (Sable.Engine.Layers.PixelLayer or Sable.Engine.Layers.ShapeLayer or Sable.Engine.Layers.TextLayer or Sable.Engine.Layers.PathLayer)) continue;
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

        // AI hover-select: click commits the hovered object to the selection
        if (ActiveTool == ToolKind.SmartSelect)
        {
            if (HasSmartObjects) SmartSelectClick(dx, dy, mods);
            return;
        }

        bool brushy = ActiveTool is ToolKind.Brush or ToolKind.Pencil or ToolKind.Eraser
            or ToolKind.CloneStamp or ToolKind.Heal or ToolKind.SpotHeal or ToolKind.Dodge or ToolKind.Burn or ToolKind.Sponge
            or ToolKind.BlurBrush or ToolKind.SharpenBrush or ToolKind.Smudge or ToolKind.Liquify;   // Liquify uses Brush.Radius/Hardness too

        // Affinity HUD: Ctrl+Alt + drag adjusts brush size/hardness (intercept before painting)
        if (mods.HasFlag(CanvasMods.Ctrl) && alt && brushy)
        {
            _hudAdjust = true; _input?.Capture();
            _input?.HideCursor();   // Affinity: hide the OS cursor; restore at start pos on release
            _hudStartSx = sx; _hudStartSy = sy;
            _hudStartRadius = Brush.Radius; _hudStartHardness = Brush.Hardness;
            return;
        }

        // Alt-click = quick eyedropper for paint + retouch brushes (NOT clone/heal — they use Alt for the source point).
        bool paintTool = ActiveTool is ToolKind.Brush or ToolKind.Pencil or ToolKind.Eraser or ToolKind.Fill
            or ToolKind.Dodge or ToolKind.Burn or ToolKind.Sponge or ToolKind.BlurBrush or ToolKind.SharpenBrush or ToolKind.Smudge;
        if (alt && ActiveLayer is not null && paintTool) { SampleColor(dx, dy); return; }
        // clone stamp / healing brush: Alt+click sets the source point
        if (ActiveTool is ToolKind.CloneStamp or ToolKind.Heal && alt)
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
            case ToolKind.ShapeRoundedRect:
            case ToolKind.ShapeEllipse:
            case ToolKind.ShapeLine:
            case ToolKind.ShapePolygon:
            case ToolKind.ShapeStar:
            case ToolKind.ShapeArrow:
                if (_doc is not null)
                {
                    _shaping = true; _input?.Capture();
                    _shapeStartDocX = dx; _shapeStartDocY = dy;
                    _shapeStartSx = sx; _shapeStartSy = sy;
                    _shapeEndSx = sx; _shapeEndSy = sy;
                }
                break;
            case ToolKind.Patch:
                if (_doc is not null && ActiveLayer is { LockPixels: false } patchLayer
                    && (_doc.SelectionMask is not null || _doc.Selection is not null))
                {
                    _patching = true; _input?.Capture();
                    _patchStartX = dx; _patchStartY = dy;
                    _patchSrc = (byte[])patchLayer.Pixels.Clone();   // immutable source for a stable live preview
                    _patchBefore = RasterState.Capture(patchLayer);
                    _patchRect = _doc.Selection;                     // the hole to heal (fixed for the gesture)
                    _patchMask = _doc.SelectionMask is { } pm0 ? (byte[])pm0.Clone() : null;
                }
                break;
            case ToolKind.MeshWarp:
                MeshDown(dx, dy);
                break;
            case ToolKind.Liquify:
                if (ActiveLayer is { LockPixels: false } liq)
                {
                    _liquifying = true; _input?.Capture();
                    _liquifyLayer = liq;
                    if (_doc is { } ld) liq.ExpandToCover(ld.Width, ld.Height);
                    _liquifyBefore = RasterState.Capture(liq);
                    _lastDocX = dx; _lastDocY = dy;
                }
                break;
            case ToolKind.Pen:
                PenDown(dx, dy, mods);
                break;
            case ToolKind.Node:
                NodeDown(dx, dy, mods);
                break;
            case ToolKind.Zoom:
                // scrubby zoom: drag right = in, left = out, anchored at the press point;
                // a plain click (no drag) steps once like before (Alt = out)
                _zoomScrub = true; _zoomScrubMoved = false; _zoomScrubAlt = alt;
                _zoomScrubLastX = sx; _zoomScrubAnchorX = sx; _zoomScrubAnchorY = sy;
                _input?.Capture();
                break;
            case ToolKind.Hand:
                StartPan(sx, sy);
                break;
            case ToolKind.Transform:
                BeginMoveTool(sx, sy, dx, dy, mods);   // Move + Transform are one tool now
                break;
            case ToolKind.EllipseMarquee:
                if (_doc is not null)
                {
                    CaptureSelMode(mods);
                    _selecting = true; _input?.Capture();
                    _selStartX = dx; _selStartY = dy;
                    _doc.SelectionMask = null;
                    // zero the rect so a plain click (no drag → no move event) deselects on release
                    if (_selMode == SelMode.Replace) _doc.Selection = new SelRect((int)dx, (int)dy, 0, 0);
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

            case ToolKind.PolyLasso:
                if (_doc is not null)
                {
                    if (_polyPts.Count == 0) _polyBase = _doc.SnapshotSelectionMask();   // remember the pre-poly selection
                    double cth = 8.0 / Math.Max(0.0001, EffectiveScale);
                    if (_polyPts.Count >= 3 && Math.Abs(dx - _polyPts[0].X) <= cth && Math.Abs(dy - _polyPts[0].Y) <= cth)
                    {
                        // combine mode comes from the CLOSING click's modifiers (natural habit)
                        _selMode = SelModeFrom(mods);
                        _baseSelMask = _selMode == SelMode.Replace ? null : _polyBase;
                        CommitPolyLasso();   // clicked back on the first vertex → close
                        break;
                    }
                    _polyPts.Add((dx, dy));
                    if (_polyPts.Count >= 2)
                        _doc.SetMaskSelection(Selections.Polygon(_doc.Width, _doc.Height, _polyPts));   // live preview
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

            case ToolKind.ColorRange:
                if (_doc is not null && ReadComposite() is { } comp)
                {
                    CaptureSelMode(mods);
                    int ix = Math.Clamp((int)dx, 0, _doc.Width - 1), iy = Math.Clamp((int)dy, 0, _doc.Height - 1);
                    int j = (iy * _doc.Width + ix) * 4;
                    var m = Selections.ColorRange(comp, _doc.Width, _doc.Height, comp[j], comp[j + 1], comp[j + 2], 32);
                    ApplyMask(m);
                }
                break;

            case ToolKind.Marquee:
                if (_doc is not null)
                {
                    // move a non-rect (mask) selection by dragging its interior — sample the mask
                    // itself, not just the bbox, so clicks in a transparent hole deselect instead
                    if (_doc.SelectionMask is not null && _doc.Selection is { } mb &&
                        dx >= mb.X && dx < mb.Right && dy >= mb.Y && dy < mb.Bottom &&
                        _doc.SelectionMask[(int)dy * _doc.Width + (int)dx] > 0)
                    {
                        _maskMoving = true; _input?.Capture();
                        _maskMoveOrig = (byte[])_doc.SelectionMask.Clone();
                        _maskMoveStartX = dx; _maskMoveStartY = dy;
                        break;
                    }
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
                        // zero the rect so a plain click (no drag → no move event) deselects on release
                        if (_selMode == SelMode.Replace) _doc.Selection = new SelRect((int)dx, (int)dy, 0, 0);
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
                            Align = (TextAlign)TypeAlign, LineSpacing = TypeLineSpacing,
                            BoxWidth = TypeBoxWidth, Tracking = TypeTracking
                        };
                        LayerProduced?.Invoke(t);
                        BeginTextEdit(t);
                    }
                }
                break;
            case ToolKind.Move:
                BeginMoveTool(sx, sy, dx, dy, mods);   // unified: move (drag interior) + scale/rotate (grab a handle)
                break;
            default: // Brush / Eraser / CloneStamp / Heal / SpotHeal
                if (ActiveTool is ToolKind.CloneStamp or ToolKind.Heal && !_cloneSet) break;   // need a source first (Alt+click)
                _session = CreateSession();
                if (_session is not null)
                {
                    bool cloning = ActiveTool is ToolKind.CloneStamp or ToolKind.Heal or ToolKind.SpotHeal;
                    if (cloning && ActiveLayer is { } cl)
                    {
                        Brush.Clone = true;
                        Brush.Heal = ActiveTool is ToolKind.Heal or ToolKind.SpotHeal;
                        Brush.CloneSrc = (byte[])cl.Pixels.Clone();   // snapshot avoids feedback during the stroke
                        Brush.CloneSrcW = cl.Width; Brush.CloneSrcH = cl.Height;
                        if (ActiveTool == ToolKind.SpotHeal)
                        {
                            // auto-source: pull texture from a nearby region (one brush-diameter to the left)
                            int off = Math.Max(4, (int)(Brush.Radius * 2));
                            Brush.CloneOffX = off; Brush.CloneOffY = 0;
                        }
                        else { Brush.CloneOffX = (int)Math.Round(dx - _cloneSrcX); Brush.CloneOffY = (int)Math.Round(dy - _cloneSrcY); }
                    }
                    _session = UpgradeToGpuSession(_session);   // after clone config (snapshot binds at begin)
                    _input?.Capture();
                    _painting = true;
                    _lastDocX = dx; _lastDocY = dy;
                    _smoothX = dx; _smoothY = dy;
                    _lastPressure = _input?.Pressure ?? 1f;
                    _session!.StrokeTo(dx, dy, dx, dy, _lastPressure, _lastPressure);
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
            Brush.Clone = false; Brush.Heal = false;   // clone/heal is per-stroke; clear after
            if (_strokeLayer is { } pl)
            {
                // pixel paint: auto-crop the layer to its painted content, then record the whole
                // gesture (grow + paint + trim) as one undoable raster-state swap.
                (_session as GpuBrushSession)?.Complete();   // GPU stroke: read pixels back first
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
            _smartX.Clear(); _smartY.Clear();   // hide snap alignment lines
            _input?.ReleaseCapture();
            if (_doc is not null)
                CommandProduced?.Invoke(new TransformLayerCommand(_doc, tl, _xfStart, LayerXform.From(tl)));
        }
        else if (_zoomScrub)
        {
            _zoomScrub = false;
            _input?.ReleaseCapture();
            if (!_zoomScrubMoved)   // plain click = single zoom step (Alt = out)
                ZoomAt(_zoomScrubAlt ? 1.0 / 1.1 : 1.1, _zoomScrubAnchorX, _zoomScrubAnchorY);
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
                Gradient, clip, _doc?.SelectionMask, _doc?.Width ?? 0, GradientShape);
            if (changed > 0)
            {
                var after = SnapshotAllTiles(target, w, h);
                gl.MarkTilesDirty(after.Keys);
                CommandProduced?.Invoke(new PaintRasterCommand(() => gl.Pixels, w, h, before, after, t => gl.MarkTilesDirty(t)));
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
        else if (_maskMoving)
        {
            _maskMoving = false; _input?.ReleaseCapture();
            if (_doc?.SelectionMask is { } fm) _doc.SetMaskSelection(fm);   // normalise bounds (clears if fully off-canvas)
            _maskMoveOrig = null;
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
                ToolKind.ShapeRoundedRect => ShapeKind.RoundedRect,
                ToolKind.ShapeEllipse => ShapeKind.Ellipse,
                ToolKind.ShapeLine => ShapeKind.Line,
                ToolKind.ShapePolygon => ShapeKind.Polygon,
                ToolKind.ShapeStar => ShapeKind.Star,
                ToolKind.ShapeArrow => ShapeKind.Arrow,
                _ => ShapeKind.Rectangle
            };
            float sx0 = (float)_shapeStartDocX, sy0 = (float)_shapeStartDocY;
            float sw = (float)(ex - sx0), sh = (float)(ey - sy0);
            if (Math.Abs(sw) >= 2 || Math.Abs(sh) >= 2)   // ignore a tiny accidental drag
            {
                // each shape is its own PARAMETRIC layer (editable fill + tight bounds; Move grabs it).
                // Fill colour = foreground; stroke/dash/per-kind params come from the options bar (ShapeStyle).
                bool lineish = kind is ShapeKind.Line or ShapeKind.Arrow;
                var shape = new ShapeLayer(kind, sx0, sy0, sw, sh, Brush.R, Brush.G, Brush.B)
                {
                    Filled = Shape.Filled && !lineish,
                    Stroked = Shape.StrokeOn || lineish,
                    // stroke colour defaults to the foreground (not black) — per-layer override via the Shape panel
                    StrokeR = Brush.R, StrokeG = Brush.G, StrokeB = Brush.B,
                    StrokeWidth = Shape.StrokeWidth,
                    DashOn = Shape.DashOn, DashLen = Shape.DashLen, GapLen = Shape.GapLen,
                    CornerRadius = Shape.CornerRadius, Sides = Shape.Sides, InnerRatio = Shape.InnerRatio,
                };
                LayerProduced?.Invoke(shape);
            }
        }
        else if (_penDragging)
        {
            PenUp();
        }
        else if (_nodeDragging)
        {
            NodeUp();
        }
        else if (_patching)
        {
            _patching = false; _input?.ReleaseCapture();
            if (ActiveLayer is { } pl && _patchSrc is not null)
            {
                var (ux, uy) = MapToDoc(_lastMouseX, _lastMouseY);
                int offX = (int)Math.Round(ux - _patchStartX), offY = (int)Math.Round(uy - _patchStartY);
                ApplyPatchPreview(offX, offY);   // commit the final heal state
                if (offX != 0 || offY != 0)
                {
                    var after = RasterState.Capture(pl);
                    CommandProduced?.Invoke(new RasterStateCommand(pl, _patchBefore, after, () => pl.Dirty = true));
                }
            }
            // restore the marching ants to the original (now-healed) hole
            if (_doc is { } rdoc)
            {
                if (_patchMask is { } pmk) rdoc.SetSelectionMaskLive(pmk);
                else if (_patchRect is { } prc) rdoc.Selection = prc;
            }
            _patchSrc = null; _patchMask = null; _patchRect = null;
        }
        else if (_meshDragIdx >= 0)
        {
            MeshUp();
        }
        else if (_liquifying && _liquifyLayer is { } lq)
        {
            _liquifying = false; _input?.ReleaseCapture();
            var after = RasterState.Capture(lq);
            CommandProduced?.Invoke(new RasterStateCommand(lq, _liquifyBefore, after, () => lq.Dirty = true));
            _liquifyLayer = null;
        }
        else if (_panningMouse)
        {
            _panningMouse = false;
            _input?.ReleaseCapture();
        }
        else _input?.ReleaseCapture();   // safety net: no gesture branch fired (or a leaked capture) → release
    }

    /// <summary>
    /// Patch live-preview + commit: heal the current selection from the immutable gesture-start
    /// pixel snapshot (<see cref="_patchSrc"/>) using the source region offset by (offX,offY),
    /// shifting the source tone to match the selection's own tone. The selection bbox is reset
    /// from the snapshot each call, so repeated drag frames (and repeat patches) never compound
    /// or smear — every offset is recomputed from scratch. Caller emits the undo command on release.
    /// </summary>
    private void ApplyPatchPreview(int offX, int offY)
    {
        if (_doc is not { } d || ActiveLayer is not { } l || _patchSrc is not { } src) return;
        var mask = _patchMask; var rect = _patchRect;   // captured at gesture start (immutable region)
        if (mask is null && rect is null) return;

        int lw = l.Width, lh = l.Height, ox = l.OffsetX, oy = l.OffsetY;
        var rectTuple = rect is { } rr ? ((int, int, int, int)?)(rr.X, rr.Y, rr.W, rr.H) : null;
        PatchTool.Apply(l.Pixels, src, lw, lh, ox, oy, rectTuple, mask, d.Width, offX, offY);

        // bbox (buffer space) touched → mark its tiles dirty + recomposite for live feedback
        int bx0 = (rect is { } r1 ? r1.X : 0), by0 = (rect is { } r2 ? r2.Y : 0);
        int bx1 = (rect is { } r3 ? r3.Right : d.Width), by1 = (rect is { } r4 ? r4.Bottom : d.Height);
        int cx0 = Math.Max(bx0, ox), cy0 = Math.Max(by0, oy);
        int cx1 = Math.Min(bx1, ox + lw), cy1 = Math.Min(by1, oy + lh);
        if (cx1 > cx0 && cy1 > cy0)
        {
            var tiles = new List<(int, int)>();
            int tx0 = (cx0 - ox) / RasterTiles.TileSize, tx1 = (cx1 - 1 - ox) / RasterTiles.TileSize;
            int ty0 = (cy0 - oy) / RasterTiles.TileSize, ty1 = (cy1 - 1 - oy) / RasterTiles.TileSize;
            int maxTx = RasterTiles.TilesX(lw), maxTy = RasterTiles.TilesY(lh);
            for (int ty = ty0; ty <= ty1; ty++)
            for (int tx = tx0; tx <= tx1; tx++)
                if (tx >= 0 && ty >= 0 && tx < maxTx && ty < maxTy) tiles.Add((tx, ty));
            l.MarkTilesDirty(tiles);
        }
        l.Dirty = true;
        _doc.MarkStructureChanged();
    }

    /// <summary>When true the crop discards pixels outside the rect (legacy destructive crop);
    /// default keeps them — layers retain independent bounds, so cropping is reversible.</summary>
    public bool CropDeletePixels { get; set; }

    /// <summary>Crop aspect-ratio constraint as w/h (0 = free), set from the options bar.</summary>
    public double CropAspect { get; set; }

    /// <summary>Commit the pending crop rectangle (Enter), resizing the document. Undoable.</summary>
    public void CommitCrop()
    {
        if (_doc is null || _cropRect is not { } r || r.W < 1 || r.H < 1) return;
        CommandProduced?.Invoke(CropDeletePixels
            ? new CropCommand(_doc, r.X, r.Y, r.W, r.H)
            : new CropKeepCommand(_doc, r.X, r.Y, r.W, r.H));
        _cropRect = null;
        ResetView();
    }

    /// <summary>Discard a pending crop rectangle (Esc).</summary>
    public void CancelCrop() => _cropRect = null;

    /// <summary>Clear any active selection.</summary>
    public void Deselect() { _doc?.ClearSelection(); _lastSelShape = null; }

    /// <summary>True while a polygonal-lasso selection is being clicked out.</summary>
    public bool PolyLassoActive => _polyPts.Count > 0;

    /// <summary>Close the in-progress polygonal lasso (Enter / click on first vertex). Combines + feathers.</summary>
    public void CommitPolyLasso()
    {
        if (_doc is not null && _polyPts.Count >= 3)
            ApplyMask(Selections.Polygon(_doc.Width, _doc.Height, _polyPts));
        _polyPts.Clear();
    }

    /// <summary>Cancel the in-progress polygonal lasso (Esc), discarding the preview.</summary>
    public void CancelPolyLasso()
    {
        bool had = _polyPts.Count > 0;
        _polyPts.Clear();
        if (had) _doc?.ClearSelection();
    }

    /// <summary>Erase the selected region of the active layer (undoable). No-op without a selection.</summary>
    public void DeleteSelection()
    {
        if (_doc is not { } doc || doc.Selection is not { } sel || ActiveLayer is not { } layer || sel.W <= 0 || sel.H <= 0) return;
        var target = layer.Pixels;
        int w = layer.Width, h = layer.Height;
        var before = SnapshotAllTiles(target, w, h);
        var mask = doc.SelectionMask;
        int mw = doc.Width, ox = layer.OffsetX, oy = layer.OffsetY;
        // iterate the selection in DOC space (clamped to the doc); map each doc pixel into the layer's
        // own buffer via the offset, skip pixels outside the layer's bounds.
        int xs = Math.Max(0, sel.X), xe = Math.Min(doc.Width, sel.Right);
        int ys = Math.Max(0, sel.Y), ye = Math.Min(doc.Height, sel.Bottom);
        for (int dy = ys; dy < ye; dy++)
        for (int dx = xs; dx < xe; dx++)
        {
            int bx = dx - ox, by = dy - oy;
            if ((uint)bx >= (uint)w || (uint)by >= (uint)h) continue;   // outside this layer
            int cov = mask is null ? 255 : mask[dy * mw + dx];
            if (cov == 0) continue;
            int i = (by * w + bx) * 4;
            if (cov >= 255) { target[i] = target[i + 1] = target[i + 2] = target[i + 3] = 0; }
            else target[i + 3] = (byte)(target[i + 3] * (255 - cov) / 255);   // feathered: erase alpha ∝ coverage
        }
        var after = SnapshotAllTiles(target, w, h);
        layer.MarkTilesDirty(after.Keys);
        CommandProduced?.Invoke(new PaintRasterCommand(() => layer.Pixels, w, h, before, after, t => layer.MarkTilesDirty(t)));
    }

    /// <summary>Fill the selection (or the whole layer when none) with a solid colour, src-over,
    /// honouring feathered selection coverage. Undoable (Edit ▸ Fill with FG/BG).</summary>
    public void FillSelection(byte r, byte g, byte b)
    {
        if (_doc is not { } doc || ActiveLayer is not { LockPixels: false } layer) return;
        layer.ExpandToCover(doc.Width, doc.Height);
        var target = layer.Pixels;
        int w = layer.Width, h = layer.Height;
        var before = SnapshotAllTiles(target, w, h);
        var mask = doc.SelectionMask;
        var sel = doc.Selection ?? new SelRect(0, 0, doc.Width, doc.Height);
        int mw = doc.Width, ox = layer.OffsetX, oy = layer.OffsetY;
        int xs = Math.Max(0, sel.X), xe = Math.Min(doc.Width, sel.Right);
        int ys = Math.Max(0, sel.Y), ye = Math.Min(doc.Height, sel.Bottom);
        for (int dy = ys; dy < ye; dy++)
        for (int dx = xs; dx < xe; dx++)
        {
            int bx = dx - ox, by = dy - oy;
            if ((uint)bx >= (uint)w || (uint)by >= (uint)h) continue;
            int cov = mask is null ? 255 : mask[dy * mw + dx];
            if (cov == 0) continue;
            int i = (by * w + bx) * 4;
            float sa = cov / 255f;
            float da = target[i + 3] / 255f;
            float outA = sa + da * (1f - sa);
            if (outA <= 0f) continue;
            target[i]     = (byte)((r / 255f * sa + target[i] / 255f * da * (1f - sa)) / outA * 255f + 0.5f);
            target[i + 1] = (byte)((g / 255f * sa + target[i + 1] / 255f * da * (1f - sa)) / outA * 255f + 0.5f);
            target[i + 2] = (byte)((b / 255f * sa + target[i + 2] / 255f * da * (1f - sa)) / outA * 255f + 0.5f);
            target[i + 3] = (byte)(outA * 255f + 0.5f);
        }
        var after = SnapshotAllTiles(target, w, h);
        layer.MarkTilesDirty(after.Keys);
        CommandProduced?.Invoke(new PaintRasterCommand(() => layer.Pixels, w, h, before, after, t => layer.MarkTilesDirty(t)));
    }

    /// <summary>Copy the selected region of the active layer (or the whole layer if no selection) → RGBA8 region.</summary>
    public (byte[] px, int w, int h)? CopyRegion()
    {
        if (_doc is null || ActiveLayer is not { } layer) return null;
        if (_doc.Selection is { } sel && sel.W > 0 && sel.H > 0)
        {
            // region = selection clamped to the DOCUMENT; the layer's pixels are read via its offset,
            // pixels outside the layer's bounds copy out transparent.
            var mask = _doc.SelectionMask; int mw = _doc.Width;
            int x0 = Math.Max(0, sel.X), y0 = Math.Max(0, sel.Y);
            int x1 = Math.Min(_doc.Width, sel.Right), y1 = Math.Min(_doc.Height, sel.Bottom);
            int w = x1 - x0, h = y1 - y0;
            if (w <= 0 || h <= 0) return null;
            int lw = layer.Width, lh = layer.Height, ox = layer.OffsetX, oy = layer.OffsetY;
            var src = layer.Pixels; var outp = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int dx = x0 + x, dy = y0 + y;
                int di = (y * w + x) * 4;
                int bx = dx - ox, by = dy - oy;
                if ((uint)bx >= (uint)lw || (uint)by >= (uint)lh) continue;   // outside layer → transparent
                int si = (by * lw + bx) * 4;
                byte cov = mask is not null ? mask[dy * mw + dx] : (byte)255;
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
        var c = l.Perspective && l.PerspCorners is { Length: 8 } pc
            ? pc
            : AffineMath.Corners(l.Width, l.Height, l.OffsetX, l.OffsetY, l.ScaleX, l.ScaleY, l.Rotation, l.ShearX, l.ShearY);
        var vp = ComputeViewport();
        var r = new float[8];
        for (int i = 0; i < 4; i++)
        {
            r[2 * i] = vp.Ox + c[2 * i] * vp.Scale;
            r[2 * i + 1] = vp.Oy + c[2 * i + 1] * vp.Scale;
        }
        return r;
    }

    private int _xfCornerIdx = -1;   // perspective corner being dragged (0..3)

    /// <summary>Unified Move/Transform begin: pixel layer → full gizmo (move interior / scale handles /
    /// rotate / Alt-corner perspective); non-pixel layer → move only.</summary>
    private void BeginMoveTool(double sx, double sy, double dx, double dy, CanvasMods mods)
    {
        if (ActiveLayer is not null)
            BeginTransform(sx, sy, mods.HasFlag(CanvasMods.Alt));
        else if (SelLayer is { LockPosition: false } ml)
        {
            // non-pixel (shape/text/path/group): move only (compositor pivots their transforms about doc-centre)
            _moving = true; _input?.Capture();
            _moveStartX = dx; _moveStartY = dy;
            _moveOrigX = ml.OffsetX; _moveOrigY = ml.OffsetY;
        }
    }

    private void BeginTransform(double sx, double sy, bool alt = false)
    {
        if (ActiveLayer is not { } l) return;
        var cs = CornersSurface(l);

        // Alt + corner → perspective / free-distort: drag that corner independently (Ctrl is now centre-scale)
        if (alt)
        {
            int ci = NearestCornerIndex(cs, sx, sy);
            if (ci >= 0)
            {
                _transforming = true; _input?.Capture();
                _xfStart = LayerXform.From(l);
                if (!l.Perspective || l.PerspCorners is not { Length: 8 })
                {
                    l.PerspCorners = AffineMath.Corners(l.Width, l.Height, l.OffsetX, l.OffsetY, l.ScaleX, l.ScaleY, l.Rotation, l.ShearX, l.ShearY);
                    l.Perspective = true;
                }
                _xfMode = 5; _xfCornerIdx = ci;
                return;
            }
        }

        double cx = (cs[0] + cs[2] + cs[4] + cs[6]) * 0.25;
        double cy = (cs[1] + cs[3] + cs[5] + cs[7]) * 0.25;
        double tmx = (cs[0] + cs[2]) * 0.5, tmy = (cs[1] + cs[3]) * 0.5;   // top mid
        double dl = Math.Sqrt((tmx - cx) * (tmx - cx) + (tmy - cy) * (tmy - cy));
        double rpx = tmx, rpy = tmy;
        if (dl > 1e-3) { rpx = tmx + (tmx - cx) / dl * RotHandleDist; rpy = tmy + (tmy - cy) / dl * RotHandleDist; }

        // edge midpoints (top,right,bottom,left)
        double tx = (cs[0] + cs[2]) * 0.5, ty = (cs[1] + cs[3]) * 0.5;
        double rx = (cs[2] + cs[4]) * 0.5, ry = (cs[3] + cs[5]) * 0.5;
        double bx = (cs[4] + cs[6]) * 0.5, by = (cs[5] + cs[7]) * 0.5;
        double lxm = (cs[6] + cs[0]) * 0.5, lym = (cs[7] + cs[1]) * 0.5;

        _xfMode = 0; _xfHandle = -1;
        if (Dist(sx, sy, rpx, rpy) <= 8) _xfMode = 2;                                      // rotate handle
        else if (NearestCornerDist(cs, sx, sy) <= 8) { _xfMode = 3; _xfHandle = NearestCornerIndex(cs, sx, sy); }
        else if (Dist(sx, sy, tx, ty) <= 7) { _xfMode = 3; _xfHandle = 10; }               // top edge
        else if (Dist(sx, sy, rx, ry) <= 7) { _xfMode = 3; _xfHandle = 11; }               // right edge
        else if (Dist(sx, sy, bx, by) <= 7) { _xfMode = 3; _xfHandle = 12; }               // bottom edge
        else if (Dist(sx, sy, lxm, lym) <= 7) { _xfMode = 3; _xfHandle = 13; }             // left edge
        else if (PointInQuad(cs, sx, sy)) _xfMode = 1;                                     // inside → move
        else _xfMode = 1;                                                                  // anywhere else → move the layer (PS Move-tool feel)

        if (_xfMode == 0) return;
        _transforming = true;
        _input?.Capture();
        _xfStart = LayerXform.From(l);
        _xfBoxW = l.Width; _xfBoxH = l.Height;
        _xfCenterX = cx; _xfCenterY = cy;
        _xfStartAngle = Math.Atan2(sy - cy, sx - cx);
        var (ddx, ddy) = MapToDoc(sx, sy);
        _xfMoveDocX = ddx; _xfMoveDocY = ddy;
        _xfOrigOffX = l.OffsetX; _xfOrigOffY = l.OffsetY;
    }

    // local (pre-transform) box coords of a scale handle (corner 0..3 / edge 10..13); fallback = centre
    private (double x, double y) HandleLocal(int handle) => handle switch
    {
        0 => (0, 0), 1 => (_xfBoxW, 0), 2 => (_xfBoxW, _xfBoxH), 3 => (0, _xfBoxH),
        10 => (_xfBoxW * 0.5, 0), 11 => (_xfBoxW, _xfBoxH * 0.5),
        12 => (_xfBoxW * 0.5, _xfBoxH), 13 => (0, _xfBoxH * 0.5),
        _ => (_xfBoxW * 0.5, _xfBoxH * 0.5)
    };
    private static int OppositeHandle(int handle) => handle switch
    {
        0 => 2, 1 => 3, 2 => 0, 3 => 1,
        10 => 12, 11 => 13, 12 => 10, 13 => 11, _ => handle
    };

    // forward-map a layer-local point to doc space using the given transform (full affine incl shear)
    private (double x, double y) LocalToDoc(double lx, double ly, LayerXform xf)
    {
        var (x, y) = AffineMath.LayerToDoc(_xfBoxW, _xfBoxH, xf.OffsetX, xf.OffsetY,
            xf.ScaleX, xf.ScaleY, xf.Rotation, (float)lx, (float)ly, xf.ShearX, xf.ShearY);
        return (x, y);
    }

    private void TransformDrag(double sx, double sy)
    {
        if (ActiveLayer is not { } l) return;
        switch (_xfMode)
        {
            case 5: // perspective: drag one free corner
            {
                if (l.PerspCorners is { Length: 8 } pc && _xfCornerIdx >= 0)
                {
                    var (dx, dy) = MapToDoc(sx, sy);
                    pc[_xfCornerIdx * 2] = (float)dx; pc[_xfCornerIdx * 2 + 1] = (float)dy;
                }
                break;
            }
            case 2: // rotate about centre; Shift snaps to 15°
            {
                double ang = Math.Atan2(sy - _xfCenterY, sx - _xfCenterX);
                double deg = _xfStart.Rotation + (ang - _xfStartAngle) * 180.0 / Math.PI;
                if (_lastMods.HasFlag(CanvasMods.Shift)) deg = Math.Round(deg / 15.0) * 15.0;
                l.Rotation = (float)deg;
                break;
            }
            case 3: // scale via a corner/edge handle (new modifier model)
            {
                ScaleByHandle(l, sx, sy);
                break;
            }
            default: // move (interior); Shift constrains to an axis; snaps to other layers + the canvas
            {
                var (dx, dy) = MapToDoc(sx, sy);
                double mx = dx - _xfMoveDocX, my = dy - _xfMoveDocY;
                if (_lastMods.HasFlag(CanvasMods.Shift)) { if (Math.Abs(mx) >= Math.Abs(my)) my = 0; else mx = 0; }
                double rawX = _xfOrigOffX + mx, rawY = _xfOrigOffY + my;
                var (snX, snY) = SmartSnap(l, rawX, rawY);   // edges/centres of layers + document
                l.OffsetX = (int)Math.Round(_smartX.Count > 0 ? snX : SnapAxis(snX, true));
                l.OffsetY = (int)Math.Round(_smartY.Count > 0 ? snY : SnapAxis(snY, false));
                break;
            }
        }
        _doc?.MarkStructureChanged();
    }

    /// <summary>
    /// Handle-driven scale, anchoring the opposite handle (or the centre with Ctrl):
    ///  • none → uniform (aspect-locked); • Shift → non-uniform (dragged axis/axes only);
    ///  • Ctrl → uniform from centre; • Ctrl+Shift → non-uniform from centre.
    /// Scaling pivots about the layer centre, so after solving the new scale the offset is
    /// recomputed to keep the anchor point fixed in document space.
    /// </summary>
    private void ScaleByHandle(Layer l, double sx, double sy)
    {
        bool shift = _lastMods.HasFlag(CanvasMods.Shift);
        bool ctrl = _lastMods.HasFlag(CanvasMods.Ctrl);

        var (hx, hy) = HandleLocal(_xfHandle);
        var (ax, ay) = ctrl ? (_xfBoxW * 0.5, _xfBoxH * 0.5) : HandleLocal(OppositeHandle(_xfHandle));
        var (aDocX, aDocY) = LocalToDoc(ax, ay, _xfStart);     // fixed anchor (start position)
        var (curX, curY) = MapToDoc(sx, sy);

        // snap the dragged handle to the canvas borders/centre + guides (records magenta alignment lines).
        // Corners snap both axes; edge handles snap only the axis they move along.
        _smartX.Clear(); _smartY.Clear();
        bool snapX = _xfHandle is 0 or 1 or 2 or 3 or 11 or 13;
        bool snapY = _xfHandle is 0 or 1 or 2 or 3 or 10 or 12;
        if (snapX) curX = SnapScaleHandle(curX, true, _smartX);
        if (snapY) curY = SnapScaleHandle(curY, false, _smartY);

        // un-rotate the cursor offset → the target for S·v (v = local handle offset from the anchor)
        double rad = -_xfStart.Rotation * Math.PI / 180.0;
        double cr = Math.Cos(rad), sr = Math.Sin(rad);
        double dX = curX - aDocX, dY = curY - aDocY;
        double wX = cr * dX - sr * dY, wY = sr * dX + cr * dY;

        double vX = hx - ax, vY = hy - ay;
        double sx0 = _xfStart.ScaleX, sy0 = _xfStart.ScaleY;
        double d0X = sx0 * vX, d0Y = sy0 * vY;       // start handle offset in scale space

        double nsx = sx0, nsy = sy0;
        if (shift)   // non-uniform: each axis independently (an axis with v≈0 keeps its scale)
        {
            if (Math.Abs(vX) > 1e-6) nsx = wX / vX;
            if (Math.Abs(vY) > 1e-6) nsy = wY / vY;
        }
        else         // uniform: project the cursor onto the start handle vector → one factor
        {
            double denom = d0X * d0X + d0Y * d0Y;
            double factor = denom > 1e-9 ? (wX * d0X + wY * d0Y) / denom : 1.0;
            nsx = sx0 * factor; nsy = sy0 * factor;
        }
        if (Math.Abs(nsx) < 1e-3) nsx = nsx < 0 ? -1e-3 : 1e-3;   // avoid a degenerate collapse
        if (Math.Abs(nsy) < 1e-3) nsy = nsy < 0 ? -1e-3 : 1e-3;
        l.ScaleX = (float)nsx; l.ScaleY = (float)nsy;

        // keep the anchor fixed: centreDoc = aDoc - R·S'·(aLocal - centre); offset = centreDoc - box/2
        double clX = ax - _xfBoxW * 0.5, clY = ay - _xfBoxH * 0.5;
        double rad2 = _xfStart.Rotation * Math.PI / 180.0;
        double cr2 = Math.Cos(rad2), sr2 = Math.Sin(rad2);
        double pX = nsx * clX, pY = nsy * clY;
        double rX = cr2 * pX - sr2 * pY, rY = sr2 * pX + cr2 * pY;
        l.OffsetX = (int)Math.Round((aDocX - rX) - _xfBoxW * 0.5);
        l.OffsetY = (int)Math.Round((aDocY - rY) - _xfBoxH * 0.5);
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
    private static int NearestCornerIndex(float[] cs, double x, double y, double tol = 10)
    {
        int best = -1; double m = tol;
        for (int i = 0; i < 4; i++) { double d = Dist(x, y, cs[2 * i], cs[2 * i + 1]); if (d <= m) { m = d; best = i; } }
        return best;
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
        _lastPanX = sx;
        _lastPanY = sy;
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
        if (SampleColorValue(dx, dy) is not { } c) return;
        Brush.R = c.r; Brush.G = c.g; Brush.B = c.b;
        ColorPicked?.Invoke(c.r, c.g, c.b);
    }

    /// <summary>Compute the colour the eyedropper would pick at (dx,dy) doc px — no side effects.
    /// Honours <see cref="EyedropperRadius"/> averaging + <see cref="EyedropperAllLayers"/>.</summary>
    private (byte r, byte g, byte b)? SampleColorValue(double dx, double dy)
    {
        byte[]? src; int sw, sh, ox = 0, oy = 0;
        if (EyedropperAllLayers && ReadComposite() is { } comp && _doc is not null)
        { src = comp; sw = _doc.Width; sh = _doc.Height; }
        else if (ActiveLayer is { } layer)
        { src = layer.Pixels; sw = layer.Width; sh = layer.Height; ox = layer.OffsetX; oy = layer.OffsetY; }
        else return null;

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
        if (n == 0) return null;
        return ((byte)(rr / n), (byte)(gg / n), (byte)(bb / n));
    }

    /// <summary>Map a surface-pixel point to document pixels via the inverse viewport transform.</summary>
    private (double x, double y) MapToDoc(double sx, double sy)
    {
        var vp = ComputeViewport();
        double scale = vp.Scale > 0 ? vp.Scale : 1;
        return ((sx - vp.Ox) / scale, (sy - vp.Oy) / scale);
    }
}
