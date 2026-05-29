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

    public LayerViewModel(Layer model, int depth = 0)
    {
        Model = model;
        Depth = depth;
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
