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

    // --- adjustment-layer params (only meaningful when Model is AdjustmentLayer) ---
    public bool IsAdjustment => Model is AdjustmentLayer;
    public bool IsFilter => Model is FilterLayer;
    public bool IsShape => Model is ShapeLayer;
    public bool IsText => Model is TextLayer;
    /// <summary>Any non-pixel effect node (has params editable in the toolbox).</summary>
    public bool IsEffect => Model is AdjustmentLayer or FilterLayer;
    public bool IsBrightnessContrast => Model is AdjustmentLayer { Kind: AdjustmentKind.BrightnessContrast };
    public bool IsLevels => Model is AdjustmentLayer { Kind: AdjustmentKind.Levels };
    public bool IsHsl => Model is AdjustmentLayer { Kind: AdjustmentKind.Hsl };

    /// <summary>Gaussian blur radius (only when Model is a FilterLayer).</summary>
    public double BlurRadius
    {
        get => (Model as FilterLayer)?.Radius ?? 8;
        set
        {
            if (Model is not FilterLayer f) return;
            f.Radius = (float)value;
            f.Dirty = true;
            OnPropertyChanged();
        }
    }

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

    // HSL (hue -180..180 deg, sat 0..200, light -100..100)
    public double HueDeg { get => (Model as AdjustmentLayer)?.HueShift * 360 ?? 0; set => SetAdj(a => a.HueShift = (float)(value / 360.0)); }
    public double SatPct { get => (Model as AdjustmentLayer)?.Saturation * 100 ?? 100; set => SetAdj(a => a.Saturation = (float)(value / 100.0)); }
    public double LightPct { get => (Model as AdjustmentLayer)?.Lightness * 100 ?? 0; set => SetAdj(a => a.Lightness = (float)(value / 100.0)); }

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
