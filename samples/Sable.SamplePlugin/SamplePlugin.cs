using System.IO;
using System.Text;
using System.Collections.Generic;
using Sable.Plugin.Sdk;
using Sable.Plugin.Sdk.Automation;
using Sable.Plugin.Sdk.Commands;
using Sable.Plugin.Sdk.Export;
using Sable.Plugin.Sdk.Host;
using Sable.Plugin.Sdk.Import;
using Sable.Plugin.Sdk.Ui;

namespace Sable.SamplePlugin;

/// <summary>
/// Minimal reference plugin. On <see cref="Initialize"/> it contributes (capability-gated):
///  - a command in the Ctrl+K palette,
///  - a menu item under the Plugins menu,
///  - a "PPM" export format.
/// Each registration is guarded behind a null-check because the host hands a null API when the
/// matching capability was not granted — the canonical way to write a robust plugin.
/// </summary>
public sealed class SamplePlugin : IPlugin
{
    private IHostContext? _host;

    public void Initialize(IHostContext host)
    {
        _host = host;
        host.Logger.Info("Sample plugin initialising.");

        host.Commands?.Register(new PluginCommand
        {
            Id = "report",
            Title = "Report Active Document",
            Category = "Sample",
            Run = ReportDocument,
        });
        host.Menus?.AddCommand(new MenuContribution
        {
            Id = "report", Title = "Report Active Document", MenuPath = "Sample", Run = ReportDocument,
        });

        // Demonstrates undo.transaction + layer.read + layer.write.basic: a multi-layer edit that
        // the user undoes in a single step.
        host.Commands?.Register(new PluginCommand
        {
            Id = "halve", Title = "Halve All Layer Opacities", Category = "Sample", Run = HalveOpacities,
        });
        host.Menus?.AddCommand(new MenuContribution
        {
            Id = "halve", Title = "Halve All Layer Opacities", MenuPath = "Sample", Run = HalveOpacities,
        });

        // Demonstrates pixel.read + pixel.write.layer_output: read the active layer's pixels, invert
        // RGB, write them back as one undoable step.
        host.Commands?.Register(new PluginCommand
        {
            Id = "invert", Title = "Invert Active Layer", Category = "Sample",
            DefaultGesture = "Ctrl+Shift+I",   // host binds it unless the user rebound/unbound it or it's taken
            Run = InvertActiveLayer,
        });
        host.Menus?.AddCommand(new MenuContribution
        {
            Id = "invert", Title = "Invert Active Layer", MenuPath = "Sample", Run = InvertActiveLayer,
        });

        // Demonstrates automation.batch: invert every queued file and save a PNG next to it.
        host.Automation?.Register(new BatchOperation
        {
            Id = "invert-batch", Title = "Invert → PNG (batch)", Category = "Sample", Run = RunInvertBatch,
        });

        host.Export?.Register(new PpmExportProvider());
        host.Import?.Register(new PpmImportProvider());

        // document.events: react to changes (here we just log; a real plugin would refresh a panel).
        host.Events?.OnDocumentChanged(() => host.Logger.Debug("document changed"));
        host.Events?.OnSelectionChanged(() => host.Logger.Debug("selection changed"));
        host.Events?.OnActiveDocumentChanged(() => host.Logger.Debug("active document changed"));
    }

    private void InvertActiveLayer()
    {
        if (_host?.Pixels is not { } pixels || _host.PixelWrites is not { } writes) return;
        if (pixels.ActiveLayer() is not { } buf) { _host.Logger.Info("No active pixel layer to invert."); return; }

        var rgba = buf.Rgba;
        for (int i = 0; i < rgba.Length; i += 4)   // invert RGB, keep alpha
        {
            rgba[i] = (byte)(255 - rgba[i]);
            rgba[i + 1] = (byte)(255 - rgba[i + 1]);
            rgba[i + 2] = (byte)(255 - rgba[i + 2]);
        }
        writes.SetActiveLayerPixels(buf);   // one undoable step
    }

    private void RunInvertBatch(IBatchApi batch)
    {
        int n = batch.InputFiles.Count;
        for (int i = 0; i < n; i++)
        {
            if (batch.Cancellation.IsCancellationRequested) return;
            var path = batch.InputFiles[i];
            batch.Report((double)i / n, Path.GetFileName(path));
            if (!batch.OpenDocument(path)) continue;

            InvertActiveLayer();   // pixel.read + pixel.write target the now-active batch document

            var outPath = Path.Combine(
                Path.GetDirectoryName(path) ?? ".",
                Path.GetFileNameWithoutExtension(path) + "_inverted.png");
            batch.SaveDocument(outPath);
            batch.CloseDocument();
        }
        batch.Report(1.0, null);
    }

    public void Shutdown() => _host?.Logger.Info("Sample plugin shutting down.");

    private void ReportDocument()
    {
        if (_host is null) return;
        var info = _host.Document?.Active;
        if (info is null) { _host.Logger.Info("No active document."); return; }

        var sel = _host.Selection?.Current;
        string selText = sel is { HasSelection: true } ? $"{sel.Width}x{sel.Height} @ {sel.X},{sel.Y}" : "none";
        var comp = _host.Pixels?.Composite();
        _host.Logger.Info(
            $"Active document: {info.Width}x{info.Height}, {info.LayerCount} layer(s); " +
            $"selection: {selText}; composite: {(comp is null ? "n/a" : $"{comp.Width}x{comp.Height}")}.");
    }

    private void HalveOpacities()
    {
        if (_host?.Layers is not { } layers || _host.LayerWrites is not { } writes) return;

        void DoIt()
        {
            foreach (var l in layers.All())
                writes.SetOpacity(l.Id, l.Opacity * 0.5f);
        }

        // Group every SetOpacity into one undo step when the capability is granted, else apply directly.
        if (_host.Transactions is { } txn) txn.Run("Halve All Layer Opacities", DoIt);
        else DoIt();
    }
}

/// <summary>Exports the flattened composite as a binary PPM (P6) image — a tiny, dependency-free
/// example of an <see cref="IExportProvider"/>. Alpha is dropped (PPM is RGB-only).</summary>
public sealed class PpmExportProvider : IExportProvider
{
    public string Id => "ppm";
    public string Label => "Portable Pixmap (PPM)";
    public string Extension => "ppm";
    public bool SupportsAlpha => false;

    public byte[] Encode(ExportImage image, ExportOptions options)
    {
        var header = Encoding.ASCII.GetBytes($"P6\n{image.Width} {image.Height}\n255\n");
        using var ms = new MemoryStream(header.Length + image.Width * image.Height * 3);
        ms.Write(header);
        var src = image.Rgba;
        for (int i = 0; i < image.Width * image.Height; i++)
        {
            int p = i * 4;
            ms.WriteByte(src[p]);
            ms.WriteByte(src[p + 1]);
            ms.WriteByte(src[p + 2]);
        }
        return ms.ToArray();
    }
}

/// <summary>Reads a binary PPM (P6) back into an RGBA8 image (the inverse of
/// <see cref="PpmExportProvider"/>) — a tiny example of an <see cref="IImportProvider"/>.</summary>
public sealed class PpmImportProvider : IImportProvider
{
    public string Id => "ppm";
    public string Label => "Portable Pixmap (PPM)";
    public IReadOnlyList<string> Extensions => new[] { "ppm" };

    public ImportImage Decode(byte[] data)
    {
        int pos = 0;
        if (Token(data, ref pos) != "P6") throw new InvalidDataException("not a binary PPM (P6)");
        int w = int.Parse(Token(data, ref pos));
        int h = int.Parse(Token(data, ref pos));
        int max = int.Parse(Token(data, ref pos));
        if (max != 255) throw new InvalidDataException("only 8-bit PPM is supported");
        pos++;   // single whitespace separating the header from the pixel data

        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            rgba[i * 4] = data[pos++];
            rgba[i * 4 + 1] = data[pos++];
            rgba[i * 4 + 2] = data[pos++];
            rgba[i * 4 + 3] = 255;
        }
        return new ImportImage { Width = w, Height = h, Rgba = rgba };
    }

    // Read the next whitespace-delimited ASCII token, skipping '#' comment lines.
    private static string Token(byte[] d, ref int pos)
    {
        while (pos < d.Length && (char.IsWhiteSpace((char)d[pos]) || d[pos] == '#'))
        {
            if (d[pos] == '#') { while (pos < d.Length && d[pos] != '\n') pos++; }
            else pos++;
        }
        int start = pos;
        while (pos < d.Length && !char.IsWhiteSpace((char)d[pos])) pos++;
        return Encoding.ASCII.GetString(d, start, pos - start);
    }
}
