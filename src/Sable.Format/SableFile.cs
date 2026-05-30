using System.IO.Compression;
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

    private sealed class DocDto
    {
        public int Version { get; set; } = 1;
        public int Width { get; set; }
        public int Height { get; set; }
        public List<LayerDto> Layers { get; set; } = new();
        public List<float> GuidesX { get; set; } = new();
        public List<float> GuidesY { get; set; } = new();
    }

    private sealed class LayerDto
    {
        public string Name { get; set; } = "Layer";
        public string Type { get; set; } = "pixel";
        public int BlendMode { get; set; }
        public float Opacity { get; set; } = 1f;
        public float FillOpacity { get; set; } = 1f;
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
        public string? Mask { get; set; }   // zip entry name, if the layer has a mask
        public List<EffectDto> Effects { get; set; } = new();
        public List<LayerDto> Children { get; set; } = new();   // for groups
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

    public static void Save(Document doc, string path)
    {
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        int next = 0;
        var dto = new DocDto { Width = doc.Width, Height = doc.Height };
        dto.GuidesX.AddRange(doc.GuidesX);
        dto.GuidesY.AddRange(doc.GuidesY);
        foreach (var layer in doc.Layers)
            dto.Layers.Add(SaveLayer(zip, layer, ref next));

        var manifest = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
        using var ms = manifest.Open();
        JsonSerializer.Serialize(ms, dto, new JsonSerializerOptions { WriteIndented = true });
    }

    private static LayerDto SaveLayer(ZipArchive zip, Layer layer, ref int next)
    {
        int id = next++;
        var ld = new LayerDto
        {
            Name = layer.Name,
            BlendMode = (int)layer.BlendMode,
            Opacity = layer.Opacity,
            FillOpacity = layer.FillOpacity,
            Visible = layer.Visible,
            Clip = layer.ClipToBelow,
            LockPosition = layer.LockPosition,
            LockPixels = layer.LockPixels,
            LockAlpha = layer.LockAlpha,
            ColorTag = layer.ColorTag,
            OffsetX = layer.OffsetX,
            OffsetY = layer.OffsetY,
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
                WriteEntry(zip, ld.Pixels, px.Pixels);
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
                break;
            case TextLayer txt:
                ld.Type = "text";
                ld.Text = txt.Text; ld.TxSize = txt.FontSize;
                ld.TxX = txt.X; ld.TxY = txt.Y;
                ld.TxR = txt.R; ld.TxG = txt.G; ld.TxB = txt.B;
                ld.TxFont = txt.FontFamily; ld.TxBold = txt.Bold; ld.TxItalic = txt.Italic;
                ld.TxUnderline = txt.Underline; ld.TxStrike = txt.Strikethrough;
                ld.TxAlign = (int)txt.Align; ld.TxLineSpacing = txt.LineSpacing;
                break;
            case GroupLayer g:
                ld.Type = "group";
                foreach (var c in g.Children) ld.Children.Add(SaveLayer(zip, c, ref next));
                break;
        }
        if (layer.Mask is { } mask)
        {
            ld.Mask = $"masks/{id}.raw";
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

        var doc = new Document(dto.Width, dto.Height);
        doc.GuidesX.AddRange(dto.GuidesX);
        doc.GuidesY.AddRange(dto.GuidesY);
        foreach (var ld in dto.Layers)
            if (BuildLayer(ld, zip, dto.Width, dto.Height) is { } l) doc.Layers.Add(l);
        return doc;
    }

    private static Layer? BuildLayer(LayerDto ld, ZipArchive zip, int w, int h)
    {
        Layer? created = ld.Type switch
        {
            "pixel" => LoadPixel(ld, zip, w, h),
            "adjustment" => BuildAdjustment(ld),
            "filter" => new FilterLayer((FilterKind)ld.FilterKind) { Radius = ld.Radius, Amount = ld.FilterAmount, Angle = ld.FilterAngle },
            "shape" => new ShapeLayer((ShapeKind)ld.ShapeKind, ld.ShX, ld.ShY, ld.ShW, ld.ShH, ld.ShR, ld.ShG, ld.ShB)
            {
                A = ld.ShA, StrokeWidth = ld.ShStroke
            },
            "text" => new TextLayer(ld.Text ?? "Text", ld.TxX, ld.TxY, ld.TxSize, ld.TxR, ld.TxG, ld.TxB)
            {
                FontFamily = ld.TxFont ?? "", Bold = ld.TxBold, Italic = ld.TxItalic,
                Underline = ld.TxUnderline, Strikethrough = ld.TxStrike,
                Align = (TextAlign)ld.TxAlign, LineSpacing = ld.TxLineSpacing
            },
            "group" => LoadGroup(ld, zip, w, h),
            _ => null
        };
        if (created is null) return null;

        created.Name = ld.Name;
        created.BlendMode = (BlendMode)ld.BlendMode;
        created.Opacity = ld.Opacity;
        created.FillOpacity = ld.FillOpacity;
        created.Visible = ld.Visible;
        created.ClipToBelow = ld.Clip;
        created.LockPosition = ld.LockPosition;
        created.LockPixels = ld.LockPixels;
        created.LockAlpha = ld.LockAlpha;
        created.ColorTag = ld.ColorTag;
        created.OffsetX = ld.OffsetX;
        created.OffsetY = ld.OffsetY;
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
            created.AddWhiteMask(w, h);
            using var es = maskEntry.Open();
            ReadFully(es, created.Mask!);
        }
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
        return a;
    }

    private static PixelLayer LoadPixel(LayerDto ld, ZipArchive zip, int w, int h)
    {
        // LayerW/H == 0 → legacy file where every layer was document-sized
        int lw = ld.LayerW > 0 ? ld.LayerW : w;
        int lh = ld.LayerH > 0 ? ld.LayerH : h;
        var px = new PixelLayer(lw, lh, ld.Name);
        if (ld.Pixels is not null && zip.GetEntry(ld.Pixels) is { } pe)
        {
            using var es = pe.Open();
            ReadFully(es, px.Pixels);
        }
        return px;
    }

    private static GroupLayer LoadGroup(LayerDto ld, ZipArchive zip, int w, int h)
    {
        var g = new GroupLayer(ld.Name);
        foreach (var c in ld.Children)
            if (BuildLayer(c, zip, w, h) is { } l) g.Children.Add(l);
        return g;
    }

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
            if (read == 0) break;
            offset += read;
        }
    }
}
