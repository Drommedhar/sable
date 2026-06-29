using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Sable.Core;
using Sable.Engine;
using Sable.Engine.Layers;

namespace Sable.Format;

/// <summary>
/// Native <c>.sable</c> document container (PLAN §4): a zip holding
/// <c>document.json</c> (size + layer graph/params) and <c>layers/{i}.raw</c>
/// (per pixel-layer RGBA8 bytes, deflate-compressed by the zip). Re-editable layer
/// data survives save/load. History/thumbnails/ICC come later.
/// </summary>
public static class SableFile
{
    private const string ManifestEntry = "document.json";
    private const string PreviewEntry = "preview.png";

    /// <summary>Read the embedded composite preview PNG without loading the document; null if absent.</summary>
    public static byte[]? TryReadPreviewPng(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            if (zip.GetEntry(PreviewEntry) is not { } e) return null;
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    private sealed class DocDto
    {
        public int Version { get; set; } = 1;
        public int Width { get; set; }
        public int Height { get; set; }
        public int Depth { get; set; } = 8;   // bits per channel (8/16/32); 0/absent → 8 (legacy)
        public List<LayerDto> Layers { get; set; } = new();
        public List<float> GuidesX { get; set; } = new();
        public List<float> GuidesY { get; set; } = new();
        public string? SavedSelection { get; set; }   // zip entry name of the stored selection mask
        public string? IccProfile { get; set; }       // zip entry name of the embedded ICC profile
        public string? IccName { get; set; }           // human-readable profile description
    }

    private sealed class LayerDto
    {
        public string Name { get; set; } = "Layer";
        public string Type { get; set; } = "pixel";
        public int BlendMode { get; set; }
        public float Opacity { get; set; } = 1f;
        public float FillOpacity { get; set; } = 1f;
        public float BifLo0 { get; set; }
        public float BifLo1 { get; set; }
        public float BifHi0 { get; set; } = 1f;
        public float BifHi1 { get; set; } = 1f;
        public bool Visible { get; set; } = true;
        public bool Clip { get; set; }
        public bool LockPosition { get; set; }
        public bool LockPixels { get; set; }
        public bool LockAlpha { get; set; }
        public int ColorTag { get; set; }
        public int OffsetX { get; set; }
        public int OffsetY { get; set; }
        public float ScaleX { get; set; } = 1f;
        public float ScaleY { get; set; } = 1f;
        public float Rotation { get; set; }
        public float ShearX { get; set; }
        public float ShearY { get; set; }
        public bool Perspective { get; set; }
        public float[]? PerspCorners { get; set; }
        public int LayerW { get; set; }   // pixel-layer buffer size (0 = legacy → document size)
        public int LayerH { get; set; }
        public string? Pixels { get; set; }   // zip entry name, if a pixel layer
        public int AdjustmentKind { get; set; }
        public float Brightness { get; set; }
        public float Contrast { get; set; } = 1f;
        public float InBlack { get; set; }
        public float InWhite { get; set; } = 1f;
        public float Gamma { get; set; } = 1f;
        public float OutBlack { get; set; }
        public float OutWhite { get; set; } = 1f;
        public float HueShift { get; set; }
        public float Saturation { get; set; } = 1f;
        public float Lightness { get; set; }
        public float Exposure { get; set; }
        public float Vibrance { get; set; }
        public float Threshold { get; set; } = 0.5f;
        public float Posterize { get; set; } = 6f;
        public float BwR { get; set; } = 0.3f;
        public float BwG { get; set; } = 0.59f;
        public float BwB { get; set; } = 0.11f;
        public float Temperature { get; set; }
        public float Tint { get; set; }
        public float Shadows { get; set; }
        public float Highlights { get; set; }
        public float[]? ColorBalance { get; set; }
        public float[]? ChannelMix { get; set; }
        public float[][]? Curves { get; set; }   // [channel][x0,y0,x1,y1,...]
        public float[]? GradientMapStops { get; set; }   // flat [pos,r,g,b, ...] (rgb 0..255)
        public bool PassThrough { get; set; }            // group: composite children onto the backdrop
        public int FilterKind { get; set; }
        public float Radius { get; set; } = 8f;
        public float FilterAmount { get; set; } = 1f;
        public float FilterAngle { get; set; }
        public int ShapeKind { get; set; }
        public float ShX { get; set; }
        public float ShY { get; set; }
        public float ShW { get; set; }
        public float ShH { get; set; }
        public byte ShR { get; set; }
        public byte ShG { get; set; }
        public byte ShB { get; set; }
        public byte ShA { get; set; } = 255;
        public float ShStroke { get; set; } = 4f;
        public bool ShFilled { get; set; } = true;
        public bool ShStroked { get; set; }
        public byte ShSR { get; set; }
        public byte ShSG { get; set; }
        public byte ShSB { get; set; }
        public byte ShSA { get; set; } = 255;
        public bool ShDash { get; set; }
        public float ShDashLen { get; set; } = 12f;
        public float ShGap { get; set; } = 8f;
        public float ShCorner { get; set; } = 12f;
        public int ShSides { get; set; } = 5;
        public float ShInner { get; set; } = 0.5f;
        public int ShCap { get; set; } = 1;    // 0=butt,1=round,2=square
        public int ShJoin { get; set; } = 1;   // 0=miter,1=round,2=bevel
        public string? Text { get; set; }
        public float TxSize { get; set; } = 48f;
        public float TxX { get; set; }
        public float TxY { get; set; }
        public byte TxR { get; set; }
        public byte TxG { get; set; }
        public byte TxB { get; set; }
        public string? TxFont { get; set; }
        public bool TxBold { get; set; }
        public bool TxItalic { get; set; }
        public bool TxUnderline { get; set; }
        public bool TxStrike { get; set; }
        public int TxAlign { get; set; }
        public float TxLineSpacing { get; set; } = 1f;
        public float TxBoxWidth { get; set; }
        public float TxTracking { get; set; }
        public float[]? TxPath { get; set; }   // on-path polyline as [x,y,x,y,...]
        // vector path: nodes flattened as [Ax,Ay,InX,InY,OutX,OutY,Smooth] × n
        public float[]? PathNodes { get; set; }
        public bool PathClosed { get; set; }
        public List<PathContourDto> PathExtras { get; set; } = new();
        public bool PathFilled { get; set; } = true;
        public byte PfR { get; set; }
        public byte PfG { get; set; }
        public byte PfB { get; set; }
        public byte PfA { get; set; } = 255;
        public bool PathStroked { get; set; }
        public int PsCap { get; set; } = 1;
        public int PsJoin { get; set; } = 1;
        public byte PsR { get; set; }
        public byte PsG { get; set; }
        public byte PsB { get; set; }
        public byte PsA { get; set; } = 255;
        public float PsWidth { get; set; } = 2f;
        public string? Mask { get; set; }   // zip entry name, if the layer has a mask
        public int MaskW { get; set; }   // mask buffer size (0 = legacy → document size)
        public int MaskH { get; set; }
        public List<EffectDto> Effects { get; set; } = new();
        public List<LayerDto> Children { get; set; } = new();   // for groups
    }

    private sealed class PathContourDto
    {
        public float[]? Nodes { get; set; }   // [Ax,Ay,InX,InY,OutX,OutY,Smooth] × n
        public bool Closed { get; set; }
    }

    private sealed class EffectDto
    {
        public int Kind { get; set; }
        public bool Enabled { get; set; } = true;
        public float R { get; set; }
        public float G { get; set; }
        public float B { get; set; }
        public float Opacity { get; set; } = 1f;
        public int BlendMode { get; set; }
        public float Radius { get; set; } = 6f;
        public float OffsetX { get; set; }
        public float OffsetY { get; set; }
        public float Size { get; set; } = 3f;
        public int StrokePos { get; set; }
        public float R2 { get; set; } = 1f;
        public float G2 { get; set; } = 1f;
        public float B2 { get; set; } = 1f;
        public float Angle { get; set; }
        public float Depth { get; set; } = 1f;
    }

    public static void Save(Document doc, string path) => Save(doc, path, null);

    /// <summary>Save with an optional pre-encoded PNG composite preview (stored as preview.png in the
    /// container — used by the welcome screen / future shell thumbnails without loading the layers).</summary>
    public static void Save(Document doc, string path, byte[]? previewPng)
    {
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        int next = 0;
        var dto = new DocDto { Width = doc.Width, Height = doc.Height, Depth = (int)doc.Depth };
        dto.GuidesX.AddRange(doc.GuidesX);
        dto.GuidesY.AddRange(doc.GuidesY);
        if (doc.SavedSelection is { } sel) { dto.SavedSelection = "selection.raw"; WriteEntry(zip, dto.SavedSelection, sel); }
        if (doc.IccProfile is { Length: > 0 } icc) { dto.IccProfile = "color.icc"; dto.IccName = doc.IccProfileName; WriteEntry(zip, dto.IccProfile, icc); }
        if (previewPng is { Length: > 0 })
        {
            var pe = zip.CreateEntry(PreviewEntry, CompressionLevel.NoCompression);   // PNG is already compressed
            using var ps = pe.Open();
            ps.Write(previewPng, 0, previewPng.Length);
        }
        foreach (var layer in doc.Layers)
            dto.Layers.Add(SaveLayer(zip, layer, doc.Width, doc.Height, doc.Depth, ref next));

        var manifest = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
        using var ms = manifest.Open();
        JsonSerializer.Serialize(ms, dto, new JsonSerializerOptions { WriteIndented = true });
    }

    private static LayerDto SaveLayer(ZipArchive zip, Layer layer, int docW, int docH, Sable.Core.BitDepth depth, ref int next)
    {
        int id = next++;
        var ld = new LayerDto
        {
            Name = layer.Name,
            BlendMode = (int)layer.BlendMode,
            Opacity = layer.Opacity,
            FillOpacity = layer.FillOpacity,
            BifLo0 = layer.BlendIfLo0, BifLo1 = layer.BlendIfLo1,
            BifHi0 = layer.BlendIfHi0, BifHi1 = layer.BlendIfHi1,
            Visible = layer.Visible,
            Clip = layer.ClipToBelow,
            LockPosition = layer.LockPosition,
            LockPixels = layer.LockPixels,
            LockAlpha = layer.LockAlpha,
            ColorTag = layer.ColorTag,
            OffsetX = layer.OffsetX,
            OffsetY = layer.OffsetY,
            ShearX = layer.ShearX,
            ShearY = layer.ShearY,
            Perspective = layer.Perspective,
            PerspCorners = layer.PerspCorners is { } pc ? (float[])pc.Clone() : null,
            ScaleX = layer.ScaleX,
            ScaleY = layer.ScaleY,
            Rotation = layer.Rotation
        };
        foreach (var fx in layer.Effects)
            ld.Effects.Add(new EffectDto
            {
                Kind = (int)fx.Kind, Enabled = fx.Enabled, R = fx.R, G = fx.G, B = fx.B,
                Opacity = fx.Opacity, BlendMode = (int)fx.BlendMode, Radius = fx.Radius,
                OffsetX = fx.OffsetX, OffsetY = fx.OffsetY, Size = fx.Size, StrokePos = (int)fx.StrokePos,
                R2 = fx.R2, G2 = fx.G2, B2 = fx.B2, Angle = fx.Angle, Depth = fx.Depth
            });
        switch (layer)
        {
            case PixelLayer px:
                ld.Type = "pixel";
                ld.LayerW = px.Width; ld.LayerH = px.Height;
                ld.Pixels = $"layers/{id}.raw";
                // TODO Phase E: store at doc.Depth (8→byte, 16→ushort, 32→float). Interim: 8-bit raw.
                WriteEntry(zip, ld.Pixels, PackPixels(px.Pixels, depth));   // raw stored at the document depth
                break;
            case AdjustmentLayer adj:
                ld.Type = "adjustment";
                ld.AdjustmentKind = (int)adj.Kind;
                ld.Brightness = adj.Brightness; ld.Contrast = adj.Contrast;
                ld.InBlack = adj.InBlack; ld.InWhite = adj.InWhite; ld.Gamma = adj.Gamma;
                ld.OutBlack = adj.OutBlack; ld.OutWhite = adj.OutWhite;
                ld.HueShift = adj.HueShift; ld.Saturation = adj.Saturation; ld.Lightness = adj.Lightness;
                ld.Exposure = adj.Exposure; ld.Vibrance = adj.Vibrance; ld.Threshold = adj.Threshold; ld.Posterize = adj.Posterize;
                ld.BwR = adj.BwR; ld.BwG = adj.BwG; ld.BwB = adj.BwB; ld.Temperature = adj.Temperature; ld.Tint = adj.Tint;
                ld.Shadows = adj.Shadows; ld.Highlights = adj.Highlights;
                ld.ColorBalance = (float[])adj.ColorBalance.Clone(); ld.ChannelMix = (float[])adj.ChannelMix.Clone();
                ld.Curves = adj.Curves.Select(ch => ch.SelectMany(p => new[] { p.x, p.y }).ToArray()).ToArray();
                ld.GradientMapStops = adj.GradientStops
                    .SelectMany(s => new[] { s.Pos, (float)s.R, (float)s.G, (float)s.B }).ToArray();
                break;
            case FilterLayer flt:
                ld.Type = "filter";
                ld.FilterKind = (int)flt.Kind;
                ld.Radius = flt.Radius;
                ld.FilterAmount = flt.Amount;
                ld.FilterAngle = flt.Angle;
                break;
            case ShapeLayer sh:
                ld.Type = "shape";
                ld.ShapeKind = (int)sh.Kind;
                ld.ShX = sh.X; ld.ShY = sh.Y; ld.ShW = sh.W; ld.ShH = sh.H;
                ld.ShR = sh.R; ld.ShG = sh.G; ld.ShB = sh.B; ld.ShA = sh.A;
                ld.ShStroke = sh.StrokeWidth;
                ld.ShFilled = sh.Filled; ld.ShStroked = sh.Stroked;
                ld.ShSR = sh.StrokeR; ld.ShSG = sh.StrokeG; ld.ShSB = sh.StrokeB; ld.ShSA = sh.StrokeA;
                ld.ShDash = sh.DashOn; ld.ShDashLen = sh.DashLen; ld.ShGap = sh.GapLen;
                ld.ShCorner = sh.CornerRadius; ld.ShSides = sh.Sides; ld.ShInner = sh.InnerRatio;
                ld.ShCap = (int)sh.Cap; ld.ShJoin = (int)sh.Join;
                break;
            case TextLayer txt:
                ld.Type = "text";
                ld.Text = txt.Text; ld.TxSize = txt.FontSize;
                ld.TxX = txt.X; ld.TxY = txt.Y;
                ld.TxR = txt.R; ld.TxG = txt.G; ld.TxB = txt.B;
                ld.TxFont = txt.FontFamily; ld.TxBold = txt.Bold; ld.TxItalic = txt.Italic;
                ld.TxUnderline = txt.Underline; ld.TxStrike = txt.Strikethrough;
                ld.TxAlign = (int)txt.Align; ld.TxLineSpacing = txt.LineSpacing;
                ld.TxBoxWidth = txt.BoxWidth; ld.TxTracking = txt.Tracking;
                if (txt.PathPoints.Count > 0)
                {
                    var tp = new float[txt.PathPoints.Count * 2];
                    for (int i = 0; i < txt.PathPoints.Count; i++) { tp[i * 2] = txt.PathPoints[i].X; tp[i * 2 + 1] = txt.PathPoints[i].Y; }
                    ld.TxPath = tp;
                }
                break;
            case PathLayer pth:
                ld.Type = "path";
                ld.PathNodes = NodesToFloats(pth.Nodes); ld.PathClosed = pth.Closed;
                foreach (var (en, ec) in pth.ExtraContours)
                    ld.PathExtras.Add(new PathContourDto { Nodes = NodesToFloats(en), Closed = ec });
                ld.PathFilled = pth.Filled; ld.PfR = pth.FillR; ld.PfG = pth.FillG; ld.PfB = pth.FillB; ld.PfA = pth.FillA;
                ld.PathStroked = pth.Stroked; ld.PsR = pth.StrokeR; ld.PsG = pth.StrokeG; ld.PsB = pth.StrokeB; ld.PsA = pth.StrokeA;
                ld.PsWidth = pth.StrokeWidth; ld.PsCap = (int)pth.Cap; ld.PsJoin = (int)pth.Join;
                break;
            case GroupLayer g:
                ld.Type = "group";
                ld.PassThrough = g.PassThrough;
                break;
        }
        // children: a group's contained layers OR a content layer's nested effect layers
        // (live filters / adjustments). Saved uniformly for every layer type.
        foreach (var c in layer.Children) ld.Children.Add(SaveLayer(zip, c, docW, docH, depth, ref next));
        if (layer.Mask is { } mask)
        {
            ld.Mask = $"masks/{id}.raw";
            // masks are layer-aligned: a pixel layer's mask matches its (dynamic) buffer size;
            // other layer types use document-sized masks.
            (ld.MaskW, ld.MaskH) = layer is PixelLayer mpx ? (mpx.Width, mpx.Height) : (docW, docH);
            WriteEntry(zip, ld.Mask, mask);
        }
        return ld;
    }

    public static Document Load(string path)
    {
        using var fs = File.OpenRead(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        var manifest = zip.GetEntry(ManifestEntry)
            ?? throw new InvalidDataException("Not a .sable file: missing document.json");
        DocDto dto;
        using (var ms = manifest.Open())
            dto = JsonSerializer.Deserialize<DocDto>(ms)
                  ?? throw new InvalidDataException("Corrupt .sable manifest");

        ValidateDim(dto.Width, dto.Height, "document");
        var doc = new Document(dto.Width, dto.Height);
        doc.Depth = dto.Depth switch { 16 => Sable.Core.BitDepth.Sixteen, 32 => Sable.Core.BitDepth.ThirtyTwo, _ => Sable.Core.BitDepth.Eight };
        doc.GuidesX.AddRange(dto.GuidesX);
        doc.GuidesY.AddRange(dto.GuidesY);
        if (dto.SavedSelection is not null && zip.GetEntry(dto.SavedSelection) is { } se)
        {
            var buf = new byte[dto.Width * dto.Height];
            using var es = se.Open();
            ReadFully(es, buf);
            doc.SavedSelection = buf;
        }
        if (dto.IccProfile is not null && zip.GetEntry(dto.IccProfile) is { } ie)
        {
            var buf = new byte[ie.Length];
            using var es = ie.Open();
            ReadFully(es, buf);
            doc.IccProfile = buf;
            doc.IccProfileName = dto.IccName;
        }
        foreach (var ld in dto.Layers)
            if (BuildLayer(ld, zip, dto.Width, dto.Height, doc.Depth) is { } l) doc.Layers.Add(l);
        return doc;
    }

    private static Layer? BuildLayer(LayerDto ld, ZipArchive zip, int w, int h, Sable.Core.BitDepth depth)
    {
        Layer? created = ld.Type switch
        {
            "pixel" => LoadPixel(ld, zip, w, h, depth),
            "adjustment" => BuildAdjustment(ld),
            "filter" => new FilterLayer((FilterKind)ld.FilterKind) { Radius = ld.Radius, Amount = ld.FilterAmount, Angle = ld.FilterAngle },
            "shape" => new ShapeLayer((ShapeKind)ld.ShapeKind, ld.ShX, ld.ShY, ld.ShW, ld.ShH, ld.ShR, ld.ShG, ld.ShB)
            {
                A = ld.ShA, StrokeWidth = ld.ShStroke,
                Filled = ld.ShFilled, Stroked = ld.ShStroked,
                StrokeR = ld.ShSR, StrokeG = ld.ShSG, StrokeB = ld.ShSB, StrokeA = ld.ShSA,
                DashOn = ld.ShDash, DashLen = ld.ShDashLen, GapLen = ld.ShGap,
                CornerRadius = ld.ShCorner, Sides = ld.ShSides, InnerRatio = ld.ShInner,
                Cap = (LineCap)ld.ShCap, Join = (LineJoin)ld.ShJoin,
            },
            "text" => new TextLayer(ld.Text ?? "Text", ld.TxX, ld.TxY, ld.TxSize, ld.TxR, ld.TxG, ld.TxB)
            {
                FontFamily = ld.TxFont ?? "", Bold = ld.TxBold, Italic = ld.TxItalic,
                Underline = ld.TxUnderline, Strikethrough = ld.TxStrike,
                Align = (TextAlign)ld.TxAlign, LineSpacing = ld.TxLineSpacing,
                BoxWidth = ld.TxBoxWidth, Tracking = ld.TxTracking,
                PathPoints = BuildTextPath(ld.TxPath),
            },
            "path" => BuildPath(ld),
            "group" => LoadGroup(ld, zip, w, h),
            _ => null
        };
        if (created is null) return null;

        created.Name = ld.Name;
        created.BlendMode = (BlendMode)ld.BlendMode;
        created.Opacity = ld.Opacity;
        created.FillOpacity = ld.FillOpacity;
        created.BlendIfLo0 = ld.BifLo0; created.BlendIfLo1 = ld.BifLo1;
        created.BlendIfHi0 = ld.BifHi0; created.BlendIfHi1 = ld.BifHi1;
        created.Visible = ld.Visible;
        created.ClipToBelow = ld.Clip;
        created.LockPosition = ld.LockPosition;
        created.LockPixels = ld.LockPixels;
        created.LockAlpha = ld.LockAlpha;
        created.ColorTag = ld.ColorTag;
        created.OffsetX = ld.OffsetX;
        created.OffsetY = ld.OffsetY;
        created.ShearX = ld.ShearX;
        created.ShearY = ld.ShearY;
        created.Perspective = ld.Perspective;
        created.PerspCorners = ld.PerspCorners is { Length: 8 } ? ld.PerspCorners : null;
        created.ScaleX = ld.ScaleX;
        created.ScaleY = ld.ScaleY;
        created.Rotation = ld.Rotation;
        foreach (var fd in ld.Effects)
            created.Effects.Add(new LayerEffect
            {
                Kind = (LayerEffectKind)fd.Kind, Enabled = fd.Enabled, R = fd.R, G = fd.G, B = fd.B,
                Opacity = fd.Opacity, BlendMode = (BlendMode)fd.BlendMode, Radius = fd.Radius,
                OffsetX = fd.OffsetX, OffsetY = fd.OffsetY, Size = fd.Size, StrokePos = (StrokePosition)fd.StrokePos,
                R2 = fd.R2, G2 = fd.G2, B2 = fd.B2, Angle = fd.Angle, Depth = fd.Depth
            });
        if (ld.Mask is not null && zip.GetEntry(ld.Mask) is { } maskEntry)
        {
            // mask is layer-aligned; legacy files (MaskW==0) used document-sized masks.
            int mw = ld.MaskW > 0 ? ld.MaskW : w;
            int mh = ld.MaskH > 0 ? ld.MaskH : h;
            ValidateDim(mw, mh, "mask");
            var mask = new byte[mw * mh * 4];
            using var es = maskEntry.Open();
            ReadFully(es, mask);
            created.Mask = mask;
            created.MaskDirty = true;
            created.Dirty = true;
        }
        // children: group content OR nested effect layers — loaded uniformly for every type.
        foreach (var c in ld.Children)
            if (BuildLayer(c, zip, w, h, depth) is { } cl) created.Children.Add(cl);
        return created;
    }

    private static AdjustmentLayer BuildAdjustment(LayerDto ld)
    {
        var a = new AdjustmentLayer((AdjustmentKind)ld.AdjustmentKind)
        {
            Brightness = ld.Brightness, Contrast = ld.Contrast,
            InBlack = ld.InBlack, InWhite = ld.InWhite, Gamma = ld.Gamma,
            OutBlack = ld.OutBlack, OutWhite = ld.OutWhite,
            HueShift = ld.HueShift, Saturation = ld.Saturation, Lightness = ld.Lightness,
            Exposure = ld.Exposure, Vibrance = ld.Vibrance, Threshold = ld.Threshold, Posterize = ld.Posterize,
            BwR = ld.BwR, BwG = ld.BwG, BwB = ld.BwB, Temperature = ld.Temperature, Tint = ld.Tint,
            Shadows = ld.Shadows, Highlights = ld.Highlights
        };
        if (ld.ColorBalance is { Length: 9 }) ld.ColorBalance.CopyTo(a.ColorBalance, 0);
        if (ld.ChannelMix is { Length: 9 }) ld.ChannelMix.CopyTo(a.ChannelMix, 0);
        if (ld.Curves is { } cs)
        {
            for (int ch = 0; ch < a.Curves.Length && ch < cs.Length; ch++)
            {
                var flat = cs[ch];
                var pts = a.Curves[ch];
                pts.Clear();
                for (int i = 0; i + 1 < flat.Length; i += 2) pts.Add((flat[i], flat[i + 1]));
                if (pts.Count < 2) { pts.Clear(); pts.Add((0f, 0f)); pts.Add((1f, 1f)); }
            }
        }
        if (ld.GradientMapStops is { Length: >= 8 } gm)
        {
            a.GradientStops.Clear();
            for (int i = 0; i + 3 < gm.Length; i += 4)
                a.GradientStops.Add((gm[i], (byte)Math.Clamp(gm[i + 1], 0, 255),
                    (byte)Math.Clamp(gm[i + 2], 0, 255), (byte)Math.Clamp(gm[i + 3], 0, 255)));
        }
        return a;
    }

    private static PathLayer BuildPath(LayerDto ld)
    {
        var p = new PathLayer
        {
            Closed = ld.PathClosed,
            Filled = ld.PathFilled, FillR = ld.PfR, FillG = ld.PfG, FillB = ld.PfB, FillA = ld.PfA,
            Stroked = ld.PathStroked, StrokeR = ld.PsR, StrokeG = ld.PsG, StrokeB = ld.PsB, StrokeA = ld.PsA,
            StrokeWidth = ld.PsWidth, Cap = (LineCap)ld.PsCap, Join = (LineJoin)ld.PsJoin,
        };
        FloatsToNodes(ld.PathNodes, p.Nodes);
        foreach (var ex in ld.PathExtras)
        {
            var en = new List<PathNode>();
            FloatsToNodes(ex.Nodes, en);
            p.ExtraContours.Add((en, ex.Closed));
        }
        return p;
    }

    private static List<(float, float)> BuildTextPath(float[]? f)
    {
        var list = new List<(float, float)>();
        if (f is not null) for (int i = 0; i + 1 < f.Length; i += 2) list.Add((f[i], f[i + 1]));
        return list;
    }

    private static float[] NodesToFloats(List<PathNode> nodes)
    {
        var f = new float[nodes.Count * 7];
        for (int i = 0; i < nodes.Count; i++)
        {
            var nd = nodes[i]; int o = i * 7;
            f[o] = nd.Ax; f[o + 1] = nd.Ay; f[o + 2] = nd.InX; f[o + 3] = nd.InY;
            f[o + 4] = nd.OutX; f[o + 5] = nd.OutY; f[o + 6] = nd.Smooth ? 1f : 0f;
        }
        return f;
    }

    private static void FloatsToNodes(float[]? f, List<PathNode> into)
    {
        if (f is null) return;
        for (int i = 0; i + 6 < f.Length; i += 7)
            into.Add(new PathNode
            {
                Ax = f[i], Ay = f[i + 1], InX = f[i + 2], InY = f[i + 3],
                OutX = f[i + 4], OutY = f[i + 5], Smooth = f[i + 6] != 0f
            });
    }

    private static PixelLayer LoadPixel(LayerDto ld, ZipArchive zip, int w, int h, Sable.Core.BitDepth depth)
    {
        // LayerW/H == 0 → legacy file where every layer was document-sized
        int lw = ld.LayerW > 0 ? ld.LayerW : w;
        int lh = ld.LayerH > 0 ? ld.LayerH : h;
        ValidateDim(lw, lh, "layer");
        var px = new PixelLayer(lw, lh, ld.Name);
        if (ld.Pixels is not null && zip.GetEntry(ld.Pixels) is { } pe)
        {
            using var es = pe.Open();
            int bpc = (int)depth / 8;   // bytes per channel at the doc depth (8→1, 16→2, 32→4)
            var raw = new byte[lw * lh * 4 * bpc];
            ReadFully(es, raw);
            px.SetBuffer(lw, lh, UnpackPixels(raw, lw * lh * 4, depth));
        }
        return px;
    }

    /// <summary>Quantise RGBA32F pixels to the document depth for on-disk storage: 8-bit byte, 16-bit
    /// little-endian ushort, or 32-bit little-endian float. Keeps 8-bit <c>.sable</c> files the same
    /// size as before while preserving full precision for 16/32-bit documents (bit-depth pipeline).</summary>
    private static byte[] PackPixels(float[] px, Sable.Core.BitDepth depth)
    {
        switch (depth)
        {
            case Sable.Core.BitDepth.Sixteen:
            {
                var outp = new byte[px.Length * 2];
                for (int i = 0; i < px.Length; i++)
                {
                    ushort v = (ushort)Math.Clamp(px[i] * 65535f + 0.5f, 0f, 65535f);
                    outp[i * 2] = (byte)(v & 0xFF); outp[i * 2 + 1] = (byte)(v >> 8);
                }
                return outp;
            }
            case Sable.Core.BitDepth.ThirtyTwo:
                return MemoryMarshal.AsBytes(px.AsSpan()).ToArray();
            default:
                return PixelLayer.FloatToBytes(px);
        }
    }

    /// <summary>Inverse of <see cref="PackPixels"/>: decode <paramref name="n"/> channels from the raw
    /// depth-specific bytes back to RGBA32F (0..1). Legacy files (depth defaulting to 8) read as bytes.</summary>
    private static float[] UnpackPixels(byte[] raw, int n, Sable.Core.BitDepth depth)
    {
        switch (depth)
        {
            case Sable.Core.BitDepth.Sixteen:
            {
                var dst = new float[n];
                for (int i = 0; i < n; i++) dst[i] = (ushort)(raw[i * 2] | (raw[i * 2 + 1] << 8)) / 65535f;
                return dst;
            }
            case Sable.Core.BitDepth.ThirtyTwo:
                return MemoryMarshal.Cast<byte, float>(raw).ToArray();
            default:
            {
                var b = raw.Length == n ? raw : raw[..n];
                return PixelLayer.BytesToFloat(b);
            }
        }
    }

    // Children are loaded generically by BuildLayer (any layer can hold them), so this
    // just makes the typed group shell.
    private static GroupLayer LoadGroup(LayerDto ld, ZipArchive zip, int w, int h)
        => new(ld.Name) { PassThrough = ld.PassThrough };

    private static void WriteEntry(ZipArchive zip, string name, byte[] data)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = e.Open();
        s.Write(data, 0, data.Length);
    }

    private static void ReadFully(Stream s, byte[] buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = s.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
                throw new InvalidDataException(
                    $"Corrupt .sable: entry truncated ({offset}/{buffer.Length} bytes).");
            offset += read;
        }
    }

    private const int MaxDim = 32768;

    /// <summary>Reject negative/zero/oversized dimensions (corrupt or malicious manifest) before allocating.</summary>
    private static void ValidateDim(int w, int h, string what)
    {
        if (w < 1 || h < 1 || w > MaxDim || h > MaxDim || (long)w * h > (long)MaxDim * MaxDim)
            throw new InvalidDataException($"Corrupt .sable: invalid {what} dimensions {w}x{h}.");
    }
}
