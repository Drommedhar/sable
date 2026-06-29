using Sable.Core;
using Sable.Engine.Layers;
using Sable.Format;
using Xunit;

namespace Sable.Tests;

/// <summary>
/// PSD import fixture tests — one per canonical fixture in <c>PsdFixtures</c>, asserting the
/// structure + warnings the compatibility matrix (<c>docs/compat/psd_matrix.md</c>) promises.
/// These lock the importer's behaviour against the documented status labels.
/// </summary>
public class PsdFixtureTests
{
    // §4/§5 raster stack
    [Fact]
    public void BasicRasterStack_TwoLayersBlendOpacityOffset()
    {
        var doc = PsdReader.Load(PsdFixtures.BasicRasterStack(), "basic", out var warnings);
        Assert.Equal(2, doc.Layers.Count);
        var top = Assert.IsType<PixelLayer>(doc.Layers[1]);
        Assert.Equal(BlendMode.Screen, top.BlendMode);
        Assert.Equal(128 / 255f, top.Opacity, 3);
        Assert.Equal(1, top.OffsetX);
        Assert.Empty(warnings);
    }

    // §6 nested pass-through group
    [Fact]
    public void NestedGroupPassThrough_GroupWithChildAndPassThrough()
    {
        var doc = PsdReader.Load(PsdFixtures.NestedGroupPassThrough(), "grp", out _);
        Assert.Equal(2, doc.Layers.Count);
        var g = Assert.IsType<GroupLayer>(doc.Layers[0]);
        Assert.Equal("My Group", g.Name);
        Assert.True(g.PassThrough);
        Assert.Single(g.Children);
        Assert.Equal("Above", doc.Layers[1].Name);
    }

    // §6 clipping chain
    [Fact]
    public void ClippingChain_ThreeClippedLayersAllClipToBelow()
    {
        var doc = PsdReader.Load(PsdFixtures.ClippingChain(), "clip", out _);
        Assert.Equal(4, doc.Layers.Count);
        Assert.False(doc.Layers[0].ClipToBelow);   // base
        Assert.True(doc.Layers[1].ClipToBelow);
        Assert.True(doc.Layers[2].ClipToBelow);
        Assert.True(doc.Layers[3].ClipToBelow);
    }

    // §7 raster mask
    [Fact]
    public void LayerMask_MaskPlaneMappedToLayerAlignedR()
    {
        var doc = PsdReader.Load(PsdFixtures.LayerMask(), "mask", out _);
        var l = Assert.IsType<PixelLayer>(Assert.Single(doc.Layers));
        Assert.NotNull(l.Mask);
        Assert.Equal(255, l.Mask![0]);
        Assert.Equal(0, l.Mask![1 * 4]);
    }

    // §7 vector mask rasterised + warning
    [Fact]
    public void VectorMaskRasterised_CoverageIntoMaskAndWarning()
    {
        var doc = PsdReader.Load(PsdFixtures.VectorMaskRasterised(), "vm", out var warnings);
        var l = Assert.IsType<PixelLayer>(Assert.Single(doc.Layers));
        Assert.NotNull(l.Mask);
        Assert.True(l.Mask![(4 * 8 + 1) * 4] > 200, "inside path = revealed");
        Assert.True(l.Mask![(4 * 8 + 6) * 4] < 50, "outside path = hidden");
        Assert.Contains(warnings, w => w.Contains("vector mask rasterised"));
    }

    // §10 SoCo + vmsk → editable PathLayer (single closed contour bridge)
    [Fact]
    public void SolidFillShape_BridgesToEditablePathLayer()
    {
        var doc = PsdReader.Load(PsdFixtures.SolidFillShape(), "shape", out var warnings);
        var l = Assert.IsType<PathLayer>(Assert.Single(doc.Layers));
        Assert.True(l.Closed);
        Assert.True(l.Filled);
        Assert.Equal(10, l.FillR);
        Assert.Equal(220, l.FillG);
        Assert.Equal(30, l.FillB);
        Assert.True(l.Nodes.Count >= 4, "rectangle vector mask → ≥4 path nodes");
        Assert.Contains(warnings, w => w.Contains("editable shape"));
    }

    // §10 SoCo + multi-contour vmsk → editable PathLayer with ExtraContours (holes)
    [Fact]
    public void SolidFillMultiContour_BridgesToPathLayerWithExtraContours()
    {
        var doc = PsdReader.Load(PsdFixtures.SolidFillMultiContour(), "multi", out var warnings);
        var l = Assert.IsType<PathLayer>(Assert.Single(doc.Layers));
        Assert.NotEmpty(l.Nodes);
        Assert.Single(l.ExtraContours);   // second contour → hole
        Assert.Contains(warnings, w => w.Contains("multi-contour"));
    }

    // §9 text → editable TextLayer
    [Fact]
    public void TextPoint_EditableTextLayerWithStyle()
    {
        var doc = PsdReader.Load(PsdFixtures.TextPoint(), "txt", out var warnings);
        var t = Assert.IsType<TextLayer>(Assert.Single(doc.Layers));
        Assert.Equal("Hello", t.Text);
        Assert.Equal(32f, t.FontSize, 1);
        Assert.Equal(255, t.R);
        Assert.Equal(TextAlign.Center, t.Align);
        Assert.True(t.Underline);
        Assert.Contains(warnings, w => w.Contains("editable text"));
    }

    // §9 multi-style text → editable TextLayer + multi-style warning
    [Fact]
    public void TextMultiStyle_FlattensToFirstStyleAndWarns()
    {
        var doc = PsdReader.Load(PsdFixtures.TextMultiStyle(), "ms", out var warnings);
        var t = Assert.IsType<TextLayer>(Assert.Single(doc.Layers));
        Assert.Equal("Two Styles", t.Text);
        Assert.Equal(24f, t.FontSize, 1);   // first run's size, not the second's 32
        Assert.Contains(warnings, w => w.Contains("style runs"));
    }

    // §12 lfx2 drop shadow + colour overlay
    [Fact]
    public void DropShadowAndOverlay_MapToLayerEffects()
    {
        var doc = PsdReader.Load(PsdFixtures.DropShadowAndOverlay(), "fx", out _);
        var l = Assert.IsType<PixelLayer>(Assert.Single(doc.Layers));
        Assert.Equal(2, l.Effects.Count);
        var sh = l.Effects.First(e => e.Kind == LayerEffectKind.DropShadow);
        Assert.Equal(BlendMode.Multiply, sh.BlendMode);
        Assert.Equal(0.5f, sh.Opacity, 2);
        Assert.Equal(7f, sh.Radius, 1);
        Assert.Equal(10f, sh.OffsetY, 1);   // angle 90° → straight down
        var ov = l.Effects.First(e => e.Kind == LayerEffectKind.ColorOverlay);
        Assert.Equal(1f, ov.G, 2);
    }

    // §2 16-bit → 8-bit + warning
    [Fact]
    public void SixteenBitFlattened_ConvertsHighByteAndWarns()
    {
        var doc = PsdReader.Load(PsdFixtures.SixteenBitFlattened(), "deep", out var warnings);
        var l = Assert.IsType<PixelLayer>(Assert.Single(doc.Layers));
        Assert.Equal(0xAB, l.Pixels[0]);
        Assert.Contains(warnings, w => w.Contains("16-bit"));
    }

    // §2 CMYK rejected
    [Fact]
    public void UnsupportedModeCmyk_RejectedWithClearError()
        => Assert.Throws<InvalidDataException>(() =>
            PsdReader.Load(PsdFixtures.UnsupportedModeCmyk(), "cmyk", out _));

    // §13 Smart Object rasterised + warning
    [Fact]
    public void SmartObjectRasterised_WarningEmitted()
    {
        var doc = PsdReader.Load(PsdFixtures.SmartObjectRasterised(), "so", out var warnings);
        Assert.Single(doc.Layers);   // the layer still imports (rasterised), not skipped
        Assert.Contains(warnings, w => w.Contains("smart object rasterised"));
    }

    // §11 adjustment layer skipped + warning
    [Fact]
    public void AdjustmentSkipped_WarningEmittedAndLayerDropped()
    {
        var doc = PsdReader.Load(PsdFixtures.AdjustmentSkipped(), "adj", out var warnings);
        Assert.Single(doc.Layers);   // only the background survives; the adjustment is skipped
        Assert.Contains(warnings, w => w.Contains("skipped"));
    }

    // §11 Brightness/Contrast → editable AdjustmentLayer
    [Fact]
    public void AdjustmentBrightnessContrast_MapsToEditableAdjustmentLayer()
    {
        var doc = PsdReader.Load(PsdFixtures.AdjustmentBrightnessContrast(), "bc", out var warnings);
        Assert.Equal(2, doc.Layers.Count);
        var adj = Assert.IsType<AdjustmentLayer>(doc.Layers[1]);
        Assert.Equal(AdjustmentKind.BrightnessContrast, adj.Kind);
        Assert.Equal(0.3f, adj.Brightness, 3);          // 30/100
        Assert.Equal(1.5f, adj.Contrast, 3);            // 1 + 50/100
        Assert.Equal(200 / 255f, adj.Opacity, 3);       // PSD opacity preserved
        Assert.True(adj.ClipToBelow);                    // clipping preserved
        Assert.Contains(warnings, w => w.Contains("editable"));
    }

    // §11 Curves → editable Curves layer with the composite-channel curve
    [Fact]
    public void AdjustmentCurves_MapsToEditableCurvesLayer()
    {
        var doc = PsdReader.Load(PsdFixtures.AdjustmentCurves(), "crv", out _);
        Assert.Equal(2, doc.Layers.Count);
        var adj = Assert.IsType<AdjustmentLayer>(doc.Layers[1]);
        Assert.Equal(AdjustmentKind.Curves, adj.Kind);
        // composite channel (0) should have 3 points; others stay identity (2 points)
        Assert.Equal(3, adj.Curves[0].Count);
        Assert.Equal(2, adj.Curves[1].Count);
        Assert.Equal(0f, adj.Curves[0][0].Item1, 3);          // (0,0)
        Assert.Equal(64f / 255f, adj.Curves[0][1].Item2, 3);  // (128,64)
    }

    // §11 Invert → editable Invert layer (no params)
    [Fact]
    public void AdjustmentInvert_MapsToEditableInvertLayer()
    {
        var doc = PsdReader.Load(PsdFixtures.AdjustmentInvert(), "inv", out _);
        Assert.Equal(2, doc.Layers.Count);
        var adj = Assert.IsType<AdjustmentLayer>(doc.Layers[1]);
        Assert.Equal(AdjustmentKind.Invert, adj.Kind);
    }

    // §11 Photo Filter → editable White Balance (approximate mapping)
    [Fact]
    public void AdjustmentPhotoFilter_MapsToWhiteBalance()
    {
        var doc = PsdReader.Load(PsdFixtures.AdjustmentPhotoFilter(), "pf", out var warnings);
        Assert.Equal(2, doc.Layers.Count);
        var adj = Assert.IsType<AdjustmentLayer>(doc.Layers[1]);
        Assert.Equal(AdjustmentKind.WhiteBalance, adj.Kind);
        Assert.True(adj.Temperature > 0f);   // warm filter (R>B) → +temp
        Assert.Contains(warnings, w => w.Contains("editable"));
    }

    // §12 gradient overlay with 3 stops → 2-colour flatten warning
    [Fact]
    public void GradientOverlayMultiStop_FlattenWarningEmitted()
    {
        var doc = PsdReader.Load(PsdFixtures.GradientOverlayMultiStop(), "gf", out var warnings);
        var fx = Assert.Single(doc.Layers).Effects;
        Assert.NotEmpty(fx);
        Assert.Contains(warnings, w => w.Contains("gradient overlay has 3 stops"));
    }

    // §12 bevel/emboss with contour → contour-not-imported warning
    [Fact]
    public void BevelWithContour_ContourWarningEmitted()
    {
        var doc = PsdReader.Load(PsdFixtures.BevelWithContour(), "bv", out var warnings);
        var fx = Assert.Single(doc.Layers).Effects;
        Assert.NotEmpty(fx);
        Assert.Contains(warnings, w => w.Contains("bevel/emboss contour curve not imported"));
    }

    // §9 vertical text + small-caps + baseline shift → warnings
    [Fact]
    public void TextVertical_EmitsVerticalAndStyleWarnings()
    {
        var doc = PsdReader.Load(PsdFixtures.TextVertical(), "vt", out var warnings);
        Assert.Contains(warnings, w => w.Contains("vertical text not imported"));
        Assert.Contains(warnings, w => w.Contains("small-caps not imported"));
        Assert.Contains(warnings, w => w.Contains("baseline shift not imported"));
    }

    // ---- additional fixture tests (matrix gap fill) ----

    // §2 grayscale 8-bit → RGB gray
    [Fact]
    public void Grayscale8Bit_ReplicatesChannelToRgb()
    {
        var doc = PsdReader.Load(PsdFixtures.Grayscale8Bit(), "gray", out _);
        var l = Assert.IsType<PixelLayer>(Assert.Single(doc.Layers));
        Assert.Equal(128, l.Pixels[0]);
        Assert.Equal(128, l.Pixels[1]);
        Assert.Equal(128, l.Pixels[2]);
    }

    // §2 grayscale 16-bit → 8-bit + warning
    [Fact]
    public void Grayscale16Bit_ConvertsAndWarns()
    {
        var doc = PsdReader.Load(PsdFixtures.Grayscale16Bit(), "g16", out var warnings);
        var l = Assert.IsType<PixelLayer>(Assert.Single(doc.Layers));
        Assert.Equal(0xCD, l.Pixels[0]);
        Assert.Contains(warnings, w => w.Contains("16-bit"));
    }

    // §3 ZIP compression
    [Fact]
    public void ZipCompression_DecodesCorrectly()
    {
        var doc = PsdReader.Load(PsdFixtures.ZipCompression(), "zip", out _);
        var l = Assert.IsType<PixelLayer>(Assert.Single(doc.Layers));
        Assert.Equal(100, l.Pixels[0]);
        Assert.Equal(150, l.Pixels[1]);
        Assert.Equal(200, l.Pixels[2]);
    }

    // §3 ZIP with prediction
    [Fact]
    public void ZipPredictionCompression_DecodesCorrectly()
    {
        var doc = PsdReader.Load(PsdFixtures.ZipPredictionCompression(), "zip3", out _);
        var l = Assert.IsType<PixelLayer>(Assert.Single(doc.Layers));
        Assert.Equal(100, l.Pixels[0]);
        Assert.Equal(150, l.Pixels[1]);
        Assert.Equal(200, l.Pixels[2]);
    }

    // §4 fill opacity (iOpa)
    [Fact]
    public void FillOpacity_PreservedFromIOpa()
    {
        var doc = PsdReader.Load(PsdFixtures.FillOpacity(), "fop", out _);
        Assert.Equal(2, doc.Layers.Count);
        Assert.Equal(128 / 255f, doc.Layers[1].FillOpacity, 3);
    }

    // §4 luni unicode name
    [Fact]
    public void UnicodeLayerName_OverridesPascalName()
    {
        var doc = PsdReader.Load(PsdFixtures.UnicodeLayerName(), "luni", out _);
        Assert.Equal("Layer\u00e9", doc.Layers[0].Name);
    }

    // §6 nested groups
    [Fact]
    public void NestedGroups_GroupInsideGroup()
    {
        var doc = PsdReader.Load(PsdFixtures.NestedGroups(), "ng", out _);
        var outer = Assert.IsType<GroupLayer>(doc.Layers[0]);
        Assert.Equal("Outer Group", outer.Name);
        var inner = Assert.IsType<GroupLayer>(outer.Children[0]);
        Assert.Equal("Inner Group", inner.Name);
        Assert.Single(inner.Children);
        Assert.Equal("Inner Child", inner.Children[0].Name);
    }

    // §6 unbalanced group markers → flattened warning
    [Fact]
    public void UnbalancedGroups_FlattenedWithWarning()
    {
        var doc = PsdReader.Load(PsdFixtures.UnbalancedGroups(), "ub", out var warnings);
        Assert.Contains(warnings, w => w.Contains("Unbalanced group markers"));
    }

    // §7 mask default colour 0
    [Fact]
    public void MaskDefaultBlack_FillsBeforeBlit()
    {
        var doc = PsdReader.Load(PsdFixtures.MaskDefaultBlack(), "mdb", out _);
        var l = Assert.IsType<PixelLayer>(Assert.Single(doc.Layers));
        Assert.NotNull(l.Mask);
        // default 0 = all hidden where mask plane is 0
        Assert.Equal(0, l.Mask![0]);
    }

    // §7 disabled mask → dropped warning
    [Fact]
    public void DisabledMask_DroppedWithWarning()
    {
        var doc = PsdReader.Load(PsdFixtures.DisabledMask(), "dm", out var warnings);
        var l = Assert.IsType<PixelLayer>(Assert.Single(doc.Layers));
        Assert.Null(l.Mask);   // disabled → dropped
        Assert.Contains(warnings, w => w.Contains("disabled layer mask dropped"));
    }

    // §9 area text + faux bold/italic
    [Fact]
    public void TextArea_EditableWithFauxStyles()
    {
        var doc = PsdReader.Load(PsdFixtures.TextArea(), "ta", out _);
        var t = Assert.IsType<TextLayer>(Assert.Single(doc.Layers));
        Assert.Equal("Area Text", t.Text);
        Assert.True(t.Bold);
        Assert.True(t.Italic);
    }

    // §12 multiple effects (inner shadow + outer glow + inner glow + stroke)
    [Fact]
    public void MultipleEffects_AllFourKindsMapped()
    {
        var doc = PsdReader.Load(PsdFixtures.MultipleEffects(), "mfx", out _);
        var fx = Assert.Single(doc.Layers).Effects;
        Assert.Equal(4, fx.Count);
        Assert.Contains(fx, e => e.Kind == LayerEffectKind.InnerShadow);
        Assert.Contains(fx, e => e.Kind == LayerEffectKind.OuterGlow);
        Assert.Contains(fx, e => e.Kind == LayerEffectKind.InnerGlow);
        Assert.Contains(fx, e => e.Kind == LayerEffectKind.Stroke);
    }

    // §12 legacy lrFX → legacy-not-imported warning
    [Fact]
    public void LegacyLrFx_LegacyWarningEmitted()
    {
        var doc = PsdReader.Load(PsdFixtures.LegacyLrFx(), "lrfx", out var warnings);
        Assert.Contains(warnings, w => w.Contains("legacy layer effects not imported"));
    }

    // §2 32-bit rejected
    [Fact]
    public void ThirtyTwoBit_RejectedWithClearError()
        => Assert.Throws<InvalidDataException>(() =>
            PsdReader.Load(PsdFixtures.ThirtyTwoBitRejected(), "32", out _));

    // §1 PSB rejected
    [Fact]
    public void PsbRejected_VersionTwoThrows()
        => Assert.Throws<InvalidDataException>(() =>
            PsdReader.Load(PsdFixtures.PsbRejected(), "psb", out _));

    // §8 clipped group — group clipped to below
    [Fact]
    public void ClippedGroup_GroupClipToBelowPreserved()
    {
        var doc = PsdReader.Load(PsdFixtures.ClippedGroup(), "cg", out _);
        Assert.Equal(2, doc.Layers.Count);
        var g = Assert.IsType<GroupLayer>(doc.Layers[1]);   // Base is bottom, group is top
        Assert.Equal("Clipped Group", g.Name);
        Assert.True(g.ClipToBelow);
        Assert.Single(g.Children);
    }

    // §9 baked rotation matrix → Rotation preserved
    [Fact]
    public void TextRotated_RotationPreservedFromMatrix()
    {
        var doc = PsdReader.Load(PsdFixtures.TextRotated(), "rot", out _);
        var t = Assert.IsType<TextLayer>(Assert.Single(doc.Layers));
        Assert.Equal("Rotated", t.Text);
        Assert.True(Math.Abs(t.Rotation - 45.0) < 1.0, $"rotation ~45°, got {t.Rotation}");
    }

    // §9 text warp → warp-not-imported warning
    [Fact]
    public void TextWarp_WarpWarningEmitted()
    {
        var doc = PsdReader.Load(PsdFixtures.TextWarp(), "warp", out var warnings);
        Assert.Contains(warnings, w => w.Contains("text warp not imported"));
    }

    // §7 real vector mask composite — doesn't crash (silently skipped)
    [Fact]
    public void RealVectorMaskComposite_DoesNotCrash()
    {
        var doc = PsdReader.Load(PsdFixtures.RealVectorMaskComposite(), "rvm", out _);
        Assert.Single(doc.Layers);
    }
}
