using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using Sable.Core;
using Sable.Engine.Layers;

namespace Sable.UI.ViewModels;

/// <summary>
/// MVVM wrapper over an engine <see cref="Layer"/>. Setters mutate the layer and
/// flag it dirty; the GPU canvas polls <c>Document.AnyDirty</c> each frame and
/// recomposites — so edits here show live with no explicit redraw call.
/// </summary>
public sealed partial class LayerViewModel : ObservableObject
{
    public Layer Model { get; }

    /// <summary>Nesting depth in the layer tree (0 = top level), for indented display.</summary>
    public int Depth { get; }
    public Avalonia.Thickness Indent => new(Depth * 14, 0, 0, 0);
    public bool IsGroup => Model is GroupLayer;
    /// <summary>Has nested rows (group content OR nested effect layers) → shows a disclosure chevron.</summary>
    public bool HasChildren => Model.HasChildren;
    /// <summary>A raster layer — gets a live downscaled <see cref="Thumbnail"/>; others show a type icon.</summary>
    public bool IsPixel => Model is PixelLayer;
    /// <summary>Indented below a group — draws a nesting guide.</summary>
    public bool IsNested => Depth > 0;
    /// <summary>Group disclosure state (set by the document VM; only meaningful for groups).</summary>
    public bool IsExpanded { get; }

    public LayerViewModel(Layer model, int depth = 0, bool expanded = true)
    {
        Model = model;
        Depth = depth;
        IsExpanded = expanded;
        RefreshThumbnail();
    }

    // --- live row thumbnail (pixel layers only; effects/groups show a Path icon) ---
    private const int ThumbW = 30, ThumbH = 24;

    [ObservableProperty]
    private Bitmap? _thumbnail;

    /// <summary>Rebuild the row thumbnail from the layer's current pixels (Affinity-style live thumb).</summary>
    public void RefreshThumbnail()
    {
        Thumbnail = Model is PixelLayer pl ? BuildThumb(pl) : null;
    }

    /// <summary>Box-average downscale of the layer's RGBA8 pixels, aspect-fit over a checker, into a small Bgra8888 bitmap.</summary>
    private static WriteableBitmap BuildThumb(PixelLayer pl)
    {
        int sw = pl.Width, sh = pl.Height;
        var src = pl.Pixels;

        double s = Math.Min((double)ThumbW / sw, (double)ThumbH / sh);
        int dw = Math.Max(1, (int)(sw * s));
        int dh = Math.Max(1, (int)(sh * s));
        int offx = (ThumbW - dw) / 2, offy = (ThumbH - dh) / 2;

        var px = new byte[ThumbW * ThumbH * 4];   // BGRA, premultiplied (all opaque)
        for (int y = 0; y < ThumbH; y++)
            for (int x = 0; x < ThumbW; x++)
            {
                byte c = (((x / 4) + (y / 4)) & 1) == 0 ? (byte)96 : (byte)72;
                int o = (y * ThumbW + x) * 4;
                px[o] = c; px[o + 1] = c; px[o + 2] = c; px[o + 3] = 255;
            }

        for (int y = 0; y < dh; y++)
            for (int x = 0; x < dw; x++)
            {
                int sx0 = (int)((double)x / dw * sw), sx1 = (int)((double)(x + 1) / dw * sw);
                int sy0 = (int)((double)y / dh * sh), sy1 = (int)((double)(y + 1) / dh * sh);
                if (sx1 <= sx0) sx1 = sx0 + 1;
                if (sy1 <= sy0) sy1 = sy0 + 1;
                int stepx = Math.Max(1, (sx1 - sx0) / 4), stepy = Math.Max(1, (sy1 - sy0) / 4);

                long r = 0, g = 0, b = 0, a = 0; int n = 0;
                for (int yy = sy0; yy < sy1; yy += stepy)
                    for (int xx = sx0; xx < sx1; xx += stepx)
                    {
                        int so = (yy * sw + xx) * 4;
                        r += src[so]; g += src[so + 1]; b += src[so + 2]; a += src[so + 3]; n++;
                    }
                if (n == 0) n = 1;
                byte ar = (byte)(r / n), ag = (byte)(g / n), ab = (byte)(b / n), aa = (byte)(a / n);

                int o = ((offy + y) * ThumbW + (offx + x)) * 4;
                float af = aa / 255f;
                px[o]     = (byte)(ab * af + px[o]     * (1 - af));   // B
                px[o + 1] = (byte)(ag * af + px[o + 1] * (1 - af));   // G
                px[o + 2] = (byte)(ar * af + px[o + 2] * (1 - af));   // R
                px[o + 3] = 255;
            }

        var wb = new WriteableBitmap(new PixelSize(ThumbW, ThumbH), new Vector(96, 96),
                                     PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var buf = wb.Lock())
        {
            int stride = buf.RowBytes;
            if (stride == ThumbW * 4)
                Marshal.Copy(px, 0, buf.Address, px.Length);
            else
                for (int y = 0; y < ThumbH; y++)
                    Marshal.Copy(px, y * ThumbW * 4, buf.Address + y * stride, ThumbW * 4);
        }
        return wb;
    }

    public string Name
    {
        get => Model.Name;
        set { if (Model.Name != value) { Model.Name = value; OnPropertyChanged(); } }
    }

    // --- inline rename (double-click / F2 / context menu) ---
    /// <summary>True while the layer row's name is being edited inline (TextBox shown instead of TextBlock).</summary>
    [ObservableProperty]
    private bool _isEditing;

    private string _nameBackup = "";

    /// <summary>Enter inline-rename mode, remembering the current name so Esc can restore it.</summary>
    public void BeginRename() { _nameBackup = Name; IsEditing = true; }

    /// <summary>Commit the inline rename (the name is already updated live via the TextBox binding).</summary>
    public void CommitRename()
    {
        if (string.IsNullOrWhiteSpace(Name)) Name = _nameBackup;   // never allow a blank name
        IsEditing = false;
    }

    /// <summary>Cancel the inline rename and restore the name as it was before editing.</summary>
    public void CancelRename() { Name = _nameBackup; IsEditing = false; }

    public bool IsVisible
    {
        get => Model.Visible;
        set
        {
            if (Model.Visible == value) return;
            Model.Visible = value;
            Model.Dirty = true;
            OnPropertyChanged();
        }
    }

    /// <summary>Opacity as 0..100 for the slider.</summary>
    public double OpacityPercent
    {
        get => Model.Opacity * 100.0;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            var f = (float)(clamped / 100.0);
            if (Math.Abs(Model.Opacity - f) < 0.0001f) return;
            Model.Opacity = f;
            Model.Dirty = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OpacityLabel));
        }
    }

    public string OpacityLabel => $"Opacity {OpacityPercent:0}%";

    /// <summary>Fill opacity as 0..100 for the slider.</summary>
    public double FillOpacityPercent
    {
        get => Model.FillOpacity * 100.0;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            var f = (float)(clamped / 100.0);
            if (Math.Abs(Model.FillOpacity - f) < 0.0001f) return;
            Model.FillOpacity = f;
            Model.Dirty = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FillOpacityLabel));
        }
    }

    public string FillOpacityLabel => $"Fill {FillOpacityPercent:0}%";

    public BlendMode BlendMode
    {
        get => Model.BlendMode;
        set
        {
            if (Model.BlendMode == value) return;
            Model.BlendMode = value;
            Model.Dirty = true;
            OnPropertyChanged();
        }
    }

    /// <summary>True when the layer carries a raster mask (for the footer Mask toggle + row indicator).</summary>
    public bool HasMask => Model.HasMask;

    /// <summary>Notify the UI a mask was added/removed (footer Mask button).</summary>
    public void RaiseMaskChanged() => OnPropertyChanged(nameof(HasMask));

    // ---- layer effects (drop shadow / outer glow / stroke / colour overlay) ----
    private LayerEffect? Fx(LayerEffectKind k) => Model.Effects.FirstOrDefault(e => e.Kind == k);

    private void ToggleFx(LayerEffectKind k, bool on)
    {
        var e = Fx(k);
        if (on && e is null) Model.Effects.Add(LayerEffect.Create(k));
        else if (!on && e is not null) Model.Effects.Remove(e);
        else return;
        Model.Dirty = true;
        OnPropertyChanged(string.Empty);   // refresh enable flags + the kind's param rows
    }

    // editing a param creates the effect if absent (Affinity-style: tweak = enable), so the
    // dialog controls stay live instead of greyed-out.
    private LayerEffect EnsureFx(LayerEffectKind k)
    {
        var e = Fx(k);
        if (e is null)
        {
            e = LayerEffect.Create(k);
            Model.Effects.Add(e);
            OnPropertyChanged(k switch
            {
                LayerEffectKind.DropShadow => nameof(HasDropShadow),
                LayerEffectKind.OuterGlow => nameof(HasOuterGlow),
                LayerEffectKind.Stroke => nameof(HasStroke),
                LayerEffectKind.InnerShadow => nameof(HasInnerShadow),
                LayerEffectKind.InnerGlow => nameof(HasInnerGlow),
                LayerEffectKind.GradientOverlay => nameof(HasGradientOverlay),
                _ => nameof(HasColorOverlay),
            });
        }
        return e;
    }

    private void SetFx(LayerEffectKind k, Action<LayerEffect> set, string name)
    {
        set(EnsureFx(k)); Model.Dirty = true; OnPropertyChanged(name);
    }

    private static string FxHex(LayerEffect? e)
        => e is null ? "000000" : $"{(int)Math.Round(e.R * 255):X2}{(int)Math.Round(e.G * 255):X2}{(int)Math.Round(e.B * 255):X2}";

    private void SetFxHex(LayerEffectKind k, string s, string name)
    {
        s = s.TrimStart('#');
        if (s.Length != 6 || !int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out int rgb)) return;
        SetFx(k, e => { e.R = ((rgb >> 16) & 0xff) / 255f; e.G = ((rgb >> 8) & 0xff) / 255f; e.B = (rgb & 0xff) / 255f; }, name);
    }

    public bool HasDropShadow { get => Fx(LayerEffectKind.DropShadow) is not null; set => ToggleFx(LayerEffectKind.DropShadow, value); }
    public string DsColorHex { get => FxHex(Fx(LayerEffectKind.DropShadow)); set => SetFxHex(LayerEffectKind.DropShadow, value, nameof(DsColorHex)); }
    public double DsOpacityPct { get => (Fx(LayerEffectKind.DropShadow)?.Opacity ?? 0.6f) * 100; set => SetFx(LayerEffectKind.DropShadow, e => e.Opacity = (float)(value / 100.0), nameof(DsOpacityPct)); }
    public double DsRadius { get => Fx(LayerEffectKind.DropShadow)?.Radius ?? 6; set => SetFx(LayerEffectKind.DropShadow, e => e.Radius = (float)value, nameof(DsRadius)); }
    public double DsOffsetX { get => Fx(LayerEffectKind.DropShadow)?.OffsetX ?? 4; set => SetFx(LayerEffectKind.DropShadow, e => e.OffsetX = (float)value, nameof(DsOffsetX)); }
    public double DsOffsetY { get => Fx(LayerEffectKind.DropShadow)?.OffsetY ?? 4; set => SetFx(LayerEffectKind.DropShadow, e => e.OffsetY = (float)value, nameof(DsOffsetY)); }

    public bool HasOuterGlow { get => Fx(LayerEffectKind.OuterGlow) is not null; set => ToggleFx(LayerEffectKind.OuterGlow, value); }
    public string GlowColorHex { get => FxHex(Fx(LayerEffectKind.OuterGlow)); set => SetFxHex(LayerEffectKind.OuterGlow, value, nameof(GlowColorHex)); }
    public double GlowOpacityPct { get => (Fx(LayerEffectKind.OuterGlow)?.Opacity ?? 0.7f) * 100; set => SetFx(LayerEffectKind.OuterGlow, e => e.Opacity = (float)(value / 100.0), nameof(GlowOpacityPct)); }
    public double GlowRadius { get => Fx(LayerEffectKind.OuterGlow)?.Radius ?? 8; set => SetFx(LayerEffectKind.OuterGlow, e => e.Radius = (float)value, nameof(GlowRadius)); }

    public bool HasStroke { get => Fx(LayerEffectKind.Stroke) is not null; set => ToggleFx(LayerEffectKind.Stroke, value); }
    public string StrokeColorHex { get => FxHex(Fx(LayerEffectKind.Stroke)); set => SetFxHex(LayerEffectKind.Stroke, value, nameof(StrokeColorHex)); }
    public double StrokeOpacityPct { get => (Fx(LayerEffectKind.Stroke)?.Opacity ?? 1f) * 100; set => SetFx(LayerEffectKind.Stroke, e => e.Opacity = (float)(value / 100.0), nameof(StrokeOpacityPct)); }
    public double StrokeSizeVal { get => Fx(LayerEffectKind.Stroke)?.Size ?? 3; set => SetFx(LayerEffectKind.Stroke, e => e.Size = (float)value, nameof(StrokeSizeVal)); }
    public int StrokePosIndex { get => (int)(Fx(LayerEffectKind.Stroke)?.StrokePos ?? StrokePosition.Outside); set => SetFx(LayerEffectKind.Stroke, e => e.StrokePos = (StrokePosition)value, nameof(StrokePosIndex)); }

    public bool HasColorOverlay { get => Fx(LayerEffectKind.ColorOverlay) is not null; set => ToggleFx(LayerEffectKind.ColorOverlay, value); }
    public string OverlayColorHex { get => FxHex(Fx(LayerEffectKind.ColorOverlay)); set => SetFxHex(LayerEffectKind.ColorOverlay, value, nameof(OverlayColorHex)); }
    public double OverlayOpacityPct { get => (Fx(LayerEffectKind.ColorOverlay)?.Opacity ?? 1f) * 100; set => SetFx(LayerEffectKind.ColorOverlay, e => e.Opacity = (float)(value / 100.0), nameof(OverlayOpacityPct)); }

    public bool HasInnerShadow { get => Fx(LayerEffectKind.InnerShadow) is not null; set => ToggleFx(LayerEffectKind.InnerShadow, value); }
    public string InShColorHex { get => FxHex(Fx(LayerEffectKind.InnerShadow)); set => SetFxHex(LayerEffectKind.InnerShadow, value, nameof(InShColorHex)); }
    public double InShOpacityPct { get => (Fx(LayerEffectKind.InnerShadow)?.Opacity ?? 0.6f) * 100; set => SetFx(LayerEffectKind.InnerShadow, e => e.Opacity = (float)(value / 100.0), nameof(InShOpacityPct)); }
    public double InShRadius { get => Fx(LayerEffectKind.InnerShadow)?.Radius ?? 6; set => SetFx(LayerEffectKind.InnerShadow, e => e.Radius = (float)value, nameof(InShRadius)); }
    public double InShOffsetX { get => Fx(LayerEffectKind.InnerShadow)?.OffsetX ?? 4; set => SetFx(LayerEffectKind.InnerShadow, e => e.OffsetX = (float)value, nameof(InShOffsetX)); }
    public double InShOffsetY { get => Fx(LayerEffectKind.InnerShadow)?.OffsetY ?? 4; set => SetFx(LayerEffectKind.InnerShadow, e => e.OffsetY = (float)value, nameof(InShOffsetY)); }

    public bool HasInnerGlow { get => Fx(LayerEffectKind.InnerGlow) is not null; set => ToggleFx(LayerEffectKind.InnerGlow, value); }
    public string InGlColorHex { get => FxHex(Fx(LayerEffectKind.InnerGlow)); set => SetFxHex(LayerEffectKind.InnerGlow, value, nameof(InGlColorHex)); }
    public double InGlOpacityPct { get => (Fx(LayerEffectKind.InnerGlow)?.Opacity ?? 0.7f) * 100; set => SetFx(LayerEffectKind.InnerGlow, e => e.Opacity = (float)(value / 100.0), nameof(InGlOpacityPct)); }
    public double InGlRadius { get => Fx(LayerEffectKind.InnerGlow)?.Radius ?? 6; set => SetFx(LayerEffectKind.InnerGlow, e => e.Radius = (float)value, nameof(InGlRadius)); }

    public bool HasGradientOverlay { get => Fx(LayerEffectKind.GradientOverlay) is not null; set => ToggleFx(LayerEffectKind.GradientOverlay, value); }
    public string GradColor1Hex { get => FxHex(Fx(LayerEffectKind.GradientOverlay)); set => SetFxHex(LayerEffectKind.GradientOverlay, value, nameof(GradColor1Hex)); }
    public string GradColor2Hex
    {
        get { var e = Fx(LayerEffectKind.GradientOverlay); return e is null ? "FFFFFF" : $"{(int)Math.Round(e.R2 * 255):X2}{(int)Math.Round(e.G2 * 255):X2}{(int)Math.Round(e.B2 * 255):X2}"; }
        set
        {
            var s = value.TrimStart('#');
            if (s.Length != 6 || !int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out int rgb)) return;
            SetFx(LayerEffectKind.GradientOverlay, e => { e.R2 = ((rgb >> 16) & 0xff) / 255f; e.G2 = ((rgb >> 8) & 0xff) / 255f; e.B2 = (rgb & 0xff) / 255f; }, nameof(GradColor2Hex));
        }
    }
    public double GradOpacityPct { get => (Fx(LayerEffectKind.GradientOverlay)?.Opacity ?? 1f) * 100; set => SetFx(LayerEffectKind.GradientOverlay, e => e.Opacity = (float)(value / 100.0), nameof(GradOpacityPct)); }
    public double GradAngle { get => Fx(LayerEffectKind.GradientOverlay)?.Angle ?? 90; set => SetFx(LayerEffectKind.GradientOverlay, e => e.Angle = (float)value, nameof(GradAngle)); }

    public bool HasBevel { get => Fx(LayerEffectKind.Bevel) is not null; set => ToggleFx(LayerEffectKind.Bevel, value); }
    public string BevHighlightHex { get => FxHex(Fx(LayerEffectKind.Bevel)); set => SetFxHex(LayerEffectKind.Bevel, value, nameof(BevHighlightHex)); }
    public string BevShadowHex
    {
        get { var e = Fx(LayerEffectKind.Bevel); return e is null ? "000000" : $"{(int)Math.Round(e.R2 * 255):X2}{(int)Math.Round(e.G2 * 255):X2}{(int)Math.Round(e.B2 * 255):X2}"; }
        set
        {
            var s = value.TrimStart('#');
            if (s.Length != 6 || !int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out int rgb)) return;
            SetFx(LayerEffectKind.Bevel, e => { e.R2 = ((rgb >> 16) & 0xff) / 255f; e.G2 = ((rgb >> 8) & 0xff) / 255f; e.B2 = (rgb & 0xff) / 255f; }, nameof(BevShadowHex));
        }
    }
    public double BevOpacityPct { get => (Fx(LayerEffectKind.Bevel)?.Opacity ?? 0.75f) * 100; set => SetFx(LayerEffectKind.Bevel, e => e.Opacity = (float)(value / 100.0), nameof(BevOpacityPct)); }
    public double BevSize { get => Fx(LayerEffectKind.Bevel)?.Size ?? 4; set => SetFx(LayerEffectKind.Bevel, e => e.Size = (float)value, nameof(BevSize)); }
    public double BevAngle { get => Fx(LayerEffectKind.Bevel)?.Angle ?? 135; set => SetFx(LayerEffectKind.Bevel, e => e.Angle = (float)value, nameof(BevAngle)); }
    public double BevDepth { get => Fx(LayerEffectKind.Bevel)?.Depth ?? 1; set => SetFx(LayerEffectKind.Bevel, e => e.Depth = (float)value, nameof(BevDepth)); }

    /// <summary>Reorder the effect of the given kind within its render group (front/behind). dir = -1 up, +1 down.</summary>
    public void MoveEffect(LayerEffectKind k, int dir)
    {
        var list = Model.Effects;
        int i = list.FindIndex(e => e.Kind == k);
        if (i < 0) return;
        bool behind = k is LayerEffectKind.DropShadow or LayerEffectKind.OuterGlow;
        int j = i + dir;
        while (j >= 0 && j < list.Count)
        {
            bool jb = list[j].Kind is LayerEffectKind.DropShadow or LayerEffectKind.OuterGlow;
            if (jb == behind) break;       // nearest sibling in the same render group
            j += dir;
        }
        if (j < 0 || j >= list.Count) return;
        (list[i], list[j]) = (list[j], list[i]);
        Model.Dirty = true;
    }

    // per-effect blend mode (Affinity blend dropdown)
    public Sable.Core.BlendMode DsBlend { get => Fx(LayerEffectKind.DropShadow)?.BlendMode ?? Sable.Core.BlendMode.Multiply; set => SetFx(LayerEffectKind.DropShadow, e => e.BlendMode = value, nameof(DsBlend)); }
    public Sable.Core.BlendMode GlowBlend { get => Fx(LayerEffectKind.OuterGlow)?.BlendMode ?? Sable.Core.BlendMode.Screen; set => SetFx(LayerEffectKind.OuterGlow, e => e.BlendMode = value, nameof(GlowBlend)); }
    public Sable.Core.BlendMode StrokeBlend { get => Fx(LayerEffectKind.Stroke)?.BlendMode ?? Sable.Core.BlendMode.Normal; set => SetFx(LayerEffectKind.Stroke, e => e.BlendMode = value, nameof(StrokeBlend)); }
    public Sable.Core.BlendMode OverlayBlend { get => Fx(LayerEffectKind.ColorOverlay)?.BlendMode ?? Sable.Core.BlendMode.Normal; set => SetFx(LayerEffectKind.ColorOverlay, e => e.BlendMode = value, nameof(OverlayBlend)); }
    public Sable.Core.BlendMode InShBlend { get => Fx(LayerEffectKind.InnerShadow)?.BlendMode ?? Sable.Core.BlendMode.Multiply; set => SetFx(LayerEffectKind.InnerShadow, e => e.BlendMode = value, nameof(InShBlend)); }
    public Sable.Core.BlendMode InGlBlend { get => Fx(LayerEffectKind.InnerGlow)?.BlendMode ?? Sable.Core.BlendMode.Screen; set => SetFx(LayerEffectKind.InnerGlow, e => e.BlendMode = value, nameof(InGlBlend)); }
    public Sable.Core.BlendMode GradBlend { get => Fx(LayerEffectKind.GradientOverlay)?.BlendMode ?? Sable.Core.BlendMode.Normal; set => SetFx(LayerEffectKind.GradientOverlay, e => e.BlendMode = value, nameof(GradBlend)); }
    public Sable.Core.BlendMode BevBlend { get => Fx(LayerEffectKind.Bevel)?.BlendMode ?? Sable.Core.BlendMode.Normal; set => SetFx(LayerEffectKind.Bevel, e => e.BlendMode = value, nameof(BevBlend)); }

    public bool ClipToBelow
    {
        get => Model.ClipToBelow;
        set
        {
            if (Model.ClipToBelow == value) return;
            Model.ClipToBelow = value;
            Model.Dirty = true;
            OnPropertyChanged();
        }
    }

    // --- locks (behaviour only; no recomposite) ---
    public bool LockPosition { get => Model.LockPosition; set { if (Model.LockPosition != value) { Model.LockPosition = value; OnPropertyChanged(); } } }
    public bool LockPixels { get => Model.LockPixels; set { if (Model.LockPixels != value) { Model.LockPixels = value; OnPropertyChanged(); } } }
    public bool LockAlpha { get => Model.LockAlpha; set { if (Model.LockAlpha != value) { Model.LockAlpha = value; OnPropertyChanged(); } } }

    // --- colour tag (Affinity row strip) ---
    private static readonly Avalonia.Media.Color[] TagColors =
    {
        Avalonia.Media.Colors.Transparent,
        Avalonia.Media.Color.FromRgb(0xD0, 0x4A, 0x4A), Avalonia.Media.Color.FromRgb(0xD8, 0x8A, 0x3A),
        Avalonia.Media.Color.FromRgb(0xD8, 0xC0, 0x3A), Avalonia.Media.Color.FromRgb(0x5A, 0xB0, 0x5A),
        Avalonia.Media.Color.FromRgb(0x4A, 0x80, 0xD0), Avalonia.Media.Color.FromRgb(0x9A, 0x5A, 0xC0),
        Avalonia.Media.Color.FromRgb(0x88, 0x88, 0x88),
    };

    public int ColorTag
    {
        get => Model.ColorTag;
        set
        {
            int v = Math.Clamp(value, 0, TagColors.Length - 1);
            if (Model.ColorTag == v) return;
            Model.ColorTag = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TagBrush));
            OnPropertyChanged(nameof(HasTag));
        }
    }
    public bool HasTag => Model.ColorTag > 0;
    public Avalonia.Media.IBrush TagBrush => TagBrushFor(Model.ColorTag);

    /// <summary>Brush for a colour-tag index (shared by the row strip + the panel swatches).</summary>
    public static Avalonia.Media.IBrush TagBrushFor(int tag)
    {
        int i = Math.Clamp(tag, 0, TagColors.Length - 1);
        return i == 0
            ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0x3A, 0x3A, 0x3A))   // "none" = dark swatch
            : new Avalonia.Media.SolidColorBrush(TagColors[i]);
    }

    // --- adjustment-layer params (only meaningful when Model is AdjustmentLayer) ---
    public bool IsAdjustment => Model is AdjustmentLayer;
    public bool IsFilter => Model is FilterLayer;
    public bool IsShape => Model is ShapeLayer;
    public bool IsText => Model is TextLayer;
    public bool IsPath => Model is PathLayer;
    /// <summary>Any non-pixel effect node (has params editable in the toolbox).</summary>
    public bool IsEffect => Model is AdjustmentLayer or FilterLayer;
    public bool IsBrightnessContrast => Model is AdjustmentLayer { Kind: AdjustmentKind.BrightnessContrast };
    public bool IsLevels => Model is AdjustmentLayer { Kind: AdjustmentKind.Levels };
    public bool IsHsl => Model is AdjustmentLayer { Kind: AdjustmentKind.Hsl };
    public bool IsCurves => Model is AdjustmentLayer { Kind: AdjustmentKind.Curves };
    public bool IsExposure => Model is AdjustmentLayer { Kind: AdjustmentKind.Exposure };
    public bool IsVibrance => Model is AdjustmentLayer { Kind: AdjustmentKind.Vibrance };
    public bool IsThreshold => Model is AdjustmentLayer { Kind: AdjustmentKind.Threshold };
    public bool IsPosterize => Model is AdjustmentLayer { Kind: AdjustmentKind.Posterize };
    public bool IsInvert => Model is AdjustmentLayer { Kind: AdjustmentKind.Invert };
    public bool IsBlackWhite => Model is AdjustmentLayer { Kind: AdjustmentKind.BlackWhite };
    public bool IsWhiteBalance => Model is AdjustmentLayer { Kind: AdjustmentKind.WhiteBalance };
    public bool IsColorBalance => Model is AdjustmentLayer { Kind: AdjustmentKind.ColorBalance };
    public bool IsChannelMixer => Model is AdjustmentLayer { Kind: AdjustmentKind.ChannelMixer };
    public bool IsShadowsHighlights => Model is AdjustmentLayer { Kind: AdjustmentKind.ShadowsHighlights };
    /// <summary>The adjustment model when this is a Curves layer (for the curve editor), else null.</summary>
    public AdjustmentLayer? CurvesAdjustment => Model is AdjustmentLayer { Kind: AdjustmentKind.Curves } a ? a : null;

    /// <summary>Blur radius / spread (FilterLayer).</summary>
    public double BlurRadius
    {
        get => (Model as FilterLayer)?.Radius ?? 8;
        set { if (Model is FilterLayer f) { f.Radius = (float)value; f.Dirty = true; OnPropertyChanged(); } }
    }
    public double FilterAmount
    {
        get => (Model as FilterLayer)?.Amount ?? 1;
        set { if (Model is FilterLayer f) { f.Amount = (float)value; f.Dirty = true; OnPropertyChanged(); } }
    }
    public double FilterAngle
    {
        get => (Model as FilterLayer)?.Angle ?? 0;
        set { if (Model is FilterLayer f) { f.Angle = (float)value; f.Dirty = true; OnPropertyChanged(); } }
    }
    private FilterKind FilterKindOf => (Model as FilterLayer)?.Kind ?? FilterKind.GaussianBlur;
    public bool FilterUsesRadius => Model is FilterLayer { Kind: FilterKind.GaussianBlur or FilterKind.BoxBlur or FilterKind.MotionBlur or FilterKind.UnsharpMask or FilterKind.HighPass or FilterKind.Clarity };
    public bool FilterUsesAmount => Model is FilterLayer { Kind: FilterKind.Sharpen or FilterKind.UnsharpMask or FilterKind.Clarity or FilterKind.ZoomBlur or FilterKind.AddNoise or FilterKind.Denoise };
    public bool FilterUsesAngle => Model is FilterLayer { Kind: FilterKind.MotionBlur };

    private void SetAdj(Action<AdjustmentLayer> set, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (Model is not AdjustmentLayer a) return;
        set(a);
        a.Dirty = true;
        OnPropertyChanged(name);
    }

    // Levels (sliders in 0..100 / gamma 10..300)
    public double InBlackPct { get => (Model as AdjustmentLayer)?.InBlack * 100 ?? 0; set => SetAdj(a => a.InBlack = (float)(value / 100.0)); }
    public double InWhitePct { get => (Model as AdjustmentLayer)?.InWhite * 100 ?? 100; set => SetAdj(a => a.InWhite = (float)(value / 100.0)); }
    public double GammaPct { get => (Model as AdjustmentLayer)?.Gamma * 100 ?? 100; set => SetAdj(a => a.Gamma = (float)(value / 100.0)); }
    public double OutBlackPct { get => (Model as AdjustmentLayer)?.OutBlack * 100 ?? 0; set => SetAdj(a => a.OutBlack = (float)(value / 100.0)); }
    public double OutWhitePct { get => (Model as AdjustmentLayer)?.OutWhite * 100 ?? 100; set => SetAdj(a => a.OutWhite = (float)(value / 100.0)); }

    // Single-param adjustments (display units)
    public double ExposureStops { get => (Model as AdjustmentLayer)?.Exposure ?? 0; set => SetAdj(a => a.Exposure = (float)value); }
    public double VibrancePct { get => (Model as AdjustmentLayer)?.Vibrance * 100 ?? 0; set => SetAdj(a => a.Vibrance = (float)(value / 100.0)); }
    public double ThresholdPct { get => (Model as AdjustmentLayer)?.Threshold * 100 ?? 50; set => SetAdj(a => a.Threshold = (float)(value / 100.0)); }
    public double PosterizeLevels { get => (Model as AdjustmentLayer)?.Posterize ?? 6; set => SetAdj(a => a.Posterize = (float)value); }

    // Black & White (weights ×100)
    public double BwRPct { get => (Model as AdjustmentLayer)?.BwR * 100 ?? 30; set => SetAdj(a => a.BwR = (float)(value / 100.0)); }
    public double BwGPct { get => (Model as AdjustmentLayer)?.BwG * 100 ?? 59; set => SetAdj(a => a.BwG = (float)(value / 100.0)); }
    public double BwBPct { get => (Model as AdjustmentLayer)?.BwB * 100 ?? 11; set => SetAdj(a => a.BwB = (float)(value / 100.0)); }

    // White Balance (-100..100)
    public double TemperaturePct { get => (Model as AdjustmentLayer)?.Temperature * 100 ?? 0; set => SetAdj(a => a.Temperature = (float)(value / 100.0)); }
    public double TintPct { get => (Model as AdjustmentLayer)?.Tint * 100 ?? 0; set => SetAdj(a => a.Tint = (float)(value / 100.0)); }

    // Shadows / Highlights (-100..100)
    public double ShadowsPct { get => (Model as AdjustmentLayer)?.Shadows * 100 ?? 0; set => SetAdj(a => a.Shadows = (float)(value / 100.0)); }
    public double HighlightsPct { get => (Model as AdjustmentLayer)?.Highlights * 100 ?? 0; set => SetAdj(a => a.Highlights = (float)(value / 100.0)); }

    // Colour Balance — 9 shifts as -100..100 (display)
    private double Cb(int i) => (Model as AdjustmentLayer)?.ColorBalance[i] * 100 ?? 0;
    private void SetCb(int i, double v, string name) { if (Model is AdjustmentLayer a) { a.ColorBalance[i] = (float)(v / 100.0); a.Dirty = true; OnPropertyChanged(name); } }
    public double CbShadowR { get => Cb(0); set => SetCb(0, value, nameof(CbShadowR)); }
    public double CbShadowG { get => Cb(1); set => SetCb(1, value, nameof(CbShadowG)); }
    public double CbShadowB { get => Cb(2); set => SetCb(2, value, nameof(CbShadowB)); }
    public double CbMidR { get => Cb(3); set => SetCb(3, value, nameof(CbMidR)); }
    public double CbMidG { get => Cb(4); set => SetCb(4, value, nameof(CbMidG)); }
    public double CbMidB { get => Cb(5); set => SetCb(5, value, nameof(CbMidB)); }
    public double CbHighR { get => Cb(6); set => SetCb(6, value, nameof(CbHighR)); }
    public double CbHighG { get => Cb(7); set => SetCb(7, value, nameof(CbHighG)); }
    public double CbHighB { get => Cb(8); set => SetCb(8, value, nameof(CbHighB)); }

    // Channel Mixer — 3x3 as -200..200 (display)
    private double Cm(int i) => (Model as AdjustmentLayer)?.ChannelMix[i] * 100 ?? 0;
    private void SetCm(int i, double v, string name) { if (Model is AdjustmentLayer a) { a.ChannelMix[i] = (float)(v / 100.0); a.Dirty = true; OnPropertyChanged(name); } }
    public double CmRR { get => Cm(0); set => SetCm(0, value, nameof(CmRR)); }
    public double CmRG { get => Cm(1); set => SetCm(1, value, nameof(CmRG)); }
    public double CmRB { get => Cm(2); set => SetCm(2, value, nameof(CmRB)); }
    public double CmGR { get => Cm(3); set => SetCm(3, value, nameof(CmGR)); }
    public double CmGG { get => Cm(4); set => SetCm(4, value, nameof(CmGG)); }
    public double CmGB { get => Cm(5); set => SetCm(5, value, nameof(CmGB)); }
    public double CmBR { get => Cm(6); set => SetCm(6, value, nameof(CmBR)); }
    public double CmBG { get => Cm(7); set => SetCm(7, value, nameof(CmBG)); }
    public double CmBB { get => Cm(8); set => SetCm(8, value, nameof(CmBB)); }

    /// <summary>Reset all this adjustment's params to defaults (header Reset button).</summary>
    public void ResetAdjustment()
    {
        if (Model is not AdjustmentLayer a) return;
        a.Brightness = 0; a.Contrast = 1f;
        a.InBlack = 0; a.InWhite = 1f; a.Gamma = 1f; a.OutBlack = 0; a.OutWhite = 1f;
        a.HueShift = 0; a.Saturation = 1f; a.Lightness = 0;
        a.Exposure = 0; a.Vibrance = 0; a.Threshold = 0.5f; a.Posterize = 6f;
        a.BwR = 0.3f; a.BwG = 0.59f; a.BwB = 0.11f; a.Temperature = 0; a.Tint = 0;
        a.Shadows = 0; a.Highlights = 0;
        Array.Clear(a.ColorBalance);
        float[] identityBc = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
        identityBc.CopyTo(a.ChannelMix, 0);
        for (int ch = 0; ch < a.Curves.Length; ch++)
        { a.Curves[ch].Clear(); a.Curves[ch].Add((0f, 0f)); a.Curves[ch].Add((1f, 1f)); }
        a.Dirty = true;
        // refresh every bound slider/box
        OnPropertyChanged(nameof(Brightness)); OnPropertyChanged(nameof(Contrast));
        OnPropertyChanged(nameof(InBlackPct)); OnPropertyChanged(nameof(InWhitePct)); OnPropertyChanged(nameof(GammaPct));
        OnPropertyChanged(nameof(OutBlackPct)); OnPropertyChanged(nameof(OutWhitePct));
        OnPropertyChanged(nameof(HueDeg)); OnPropertyChanged(nameof(SatPct)); OnPropertyChanged(nameof(LightPct));
        OnPropertyChanged(nameof(ExposureStops)); OnPropertyChanged(nameof(VibrancePct));
        OnPropertyChanged(nameof(ThresholdPct)); OnPropertyChanged(nameof(PosterizeLevels));
        OnPropertyChanged(nameof(BwRPct)); OnPropertyChanged(nameof(BwGPct)); OnPropertyChanged(nameof(BwBPct));
        OnPropertyChanged(nameof(TemperaturePct)); OnPropertyChanged(nameof(TintPct));
        OnPropertyChanged(string.Empty);   // refresh the 9+9 colour-balance / channel-mixer sliders
    }

    // HSL (hue -180..180 deg, sat 0..200, light -100..100)
    public double HueDeg { get => (Model as AdjustmentLayer)?.HueShift * 360 ?? 0; set => SetAdj(a => a.HueShift = (float)(value / 360.0)); }
    public double SatPct { get => (Model as AdjustmentLayer)?.Saturation * 100 ?? 100; set => SetAdj(a => a.Saturation = (float)(value / 100.0)); }
    public double LightPct { get => (Model as AdjustmentLayer)?.Lightness * 100 ?? 0; set => SetAdj(a => a.Lightness = (float)(value / 100.0)); }

    // --- transform params (any layer) — numeric Transform panel ---
    private void SetXform(Action<Layer> set, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        set(Model); Model.Dirty = true; OnPropertyChanged(name);
    }
    public double XfX { get => Model.OffsetX; set => SetXform(l => l.OffsetX = (int)Math.Round(value)); }
    public double XfY { get => Model.OffsetY; set => SetXform(l => l.OffsetY = (int)Math.Round(value)); }
    public double XfScaleX { get => Model.ScaleX * 100; set => SetXform(l => l.ScaleX = (float)(value / 100.0)); }
    public double XfScaleY { get => Model.ScaleY * 100; set => SetXform(l => l.ScaleY = (float)(value / 100.0)); }
    public double XfRotation { get => Model.Rotation; set => SetXform(l => l.Rotation = (float)value); }
    public double XfShearX { get => Model.ShearX * 100; set => SetXform(l => l.ShearX = (float)(value / 100.0)); }
    public double XfShearY { get => Model.ShearY * 100; set => SetXform(l => l.ShearY = (float)(value / 100.0)); }

    /// <summary>Refresh all transform-panel bindings (after a gizmo drag / flip / reset).</summary>
    public void RefreshTransform()
    {
        OnPropertyChanged(nameof(XfX)); OnPropertyChanged(nameof(XfY));
        OnPropertyChanged(nameof(XfScaleX)); OnPropertyChanged(nameof(XfScaleY));
        OnPropertyChanged(nameof(XfRotation)); OnPropertyChanged(nameof(XfShearX)); OnPropertyChanged(nameof(XfShearY));
    }

    // --- shape-layer params (only meaningful when Model is ShapeLayer) — Shape properties panel ---
    public bool ShapeUsesSides => Model is ShapeLayer { Kind: ShapeKind.Polygon or ShapeKind.Star };
    public bool ShapeUsesInner => Model is ShapeLayer { Kind: ShapeKind.Star };
    public bool ShapeUsesCorner => Model is ShapeLayer { Kind: ShapeKind.RoundedRect };
    public bool ShapeIsLine => Model is ShapeLayer { Kind: ShapeKind.Line or ShapeKind.Arrow };

    private void SetShape(Action<ShapeLayer> set, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (Model is not ShapeLayer s) return;
        set(s); s.Dirty = true; OnPropertyChanged(name);
    }

    public bool ShapeFilled
    {
        get => Model is ShapeLayer s && s.Filled;
        set => SetShape(s => s.Filled = value);
    }
    public bool ShapeStroked
    {
        get => Model is ShapeLayer s && s.Stroked;
        set => SetShape(s => s.Stroked = value);
    }
    public string ShapeFillHex
    {
        get => Model is ShapeLayer s ? $"{s.R:X2}{s.G:X2}{s.B:X2}" : "000000";
        set { if (TryHex(value, out var r, out var g, out var b)) SetShape(s => { s.R = r; s.G = g; s.B = b; }); }
    }
    public string ShapeStrokeHex
    {
        get => Model is ShapeLayer s ? $"{s.StrokeR:X2}{s.StrokeG:X2}{s.StrokeB:X2}" : "000000";
        set { if (TryHex(value, out var r, out var g, out var b)) SetShape(s => { s.StrokeR = r; s.StrokeG = g; s.StrokeB = b; }); }
    }
    public double ShapeStrokeWidth
    {
        get => Model is ShapeLayer s ? s.StrokeWidth : 0;
        set => SetShape(s => s.StrokeWidth = (float)value);
    }
    public bool ShapeDashOn
    {
        get => Model is ShapeLayer s && s.DashOn;
        set => SetShape(s => s.DashOn = value);
    }
    public double ShapeDashLen
    {
        get => Model is ShapeLayer s ? s.DashLen : 0;
        set => SetShape(s => s.DashLen = (float)value);
    }
    public double ShapeGapLen
    {
        get => Model is ShapeLayer s ? s.GapLen : 0;
        set => SetShape(s => s.GapLen = (float)value);
    }
    public double ShapeSides
    {
        get => Model is ShapeLayer s ? s.Sides : 5;
        set => SetShape(s => s.Sides = Math.Clamp((int)Math.Round(value), 3, 60));
    }
    public double ShapeInnerPercent
    {
        get => Model is ShapeLayer s ? s.InnerRatio * 100 : 50;
        set => SetShape(s => s.InnerRatio = (float)Math.Clamp(value / 100.0, 0.05, 0.95));
    }
    public double ShapeCornerRadius
    {
        get => Model is ShapeLayer s ? s.CornerRadius : 0;
        set => SetShape(s => s.CornerRadius = (float)Math.Max(0, value));
    }
    /// <summary>Stroke cap as a combo index (0=butt,1=round,2=square).</summary>
    public int ShapeCap
    {
        get => Model is ShapeLayer s ? (int)s.Cap : 1;
        set => SetShape(s => s.Cap = (LineCap)Math.Clamp(value, 0, 2));
    }
    /// <summary>Stroke join as a combo index (0=miter,1=round,2=bevel).</summary>
    public int ShapeJoin
    {
        get => Model is ShapeLayer s ? (int)s.Join : 1;
        set => SetShape(s => s.Join = (LineJoin)Math.Clamp(value, 0, 2));
    }

    private static bool TryHex(string hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        hex = hex.TrimStart('#');
        if (hex.Length != 6) return false;
        return byte.TryParse(hex.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out r)
            && byte.TryParse(hex.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out g)
            && byte.TryParse(hex.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out b);
    }

    /// <summary>Brightness as -100..100 for the slider.</summary>
    public double Brightness
    {
        get => Model is AdjustmentLayer a ? a.Brightness * 100.0 : 0;
        set
        {
            if (Model is not AdjustmentLayer a) return;
            a.Brightness = (float)(value / 100.0);
            a.Dirty = true;
            OnPropertyChanged();
        }
    }

    /// <summary>Contrast as 0..200 for the slider (100 = no change).</summary>
    public double Contrast
    {
        get => Model is AdjustmentLayer a ? a.Contrast * 100.0 : 100;
        set
        {
            if (Model is not AdjustmentLayer a) return;
            a.Contrast = (float)(value / 100.0);
            a.Dirty = true;
            OnPropertyChanged();
        }
    }
}
