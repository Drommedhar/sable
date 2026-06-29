using System;
using System.Collections.Generic;
using System.Linq;

namespace Sable.Format;

/// <summary>
/// Categorises the raw <c>warnings</c>/<c>fonts</c> lists produced by <see cref="PsdReader.Load"/>
/// into a structured compatibility report (roadmap §15). Pure logic — no UI, no GPU — so the
/// categorisation is unit-tested in <c>PsdReaderTests</c> and the report window just binds to it.
/// </summary>
public sealed class CompatibilityReport
{
    public enum Severity
    {
        /// <summary>A feature was rasterised (opens visually, loses editability).</summary>
        Rasterised,
        /// <summary>A feature was approximated (e.g. 16→8-bit, gradient flattened to 2 colours).</summary>
        Partial,
        /// <summary>A feature was skipped entirely (adjustment layers, gradient/pattern fill, warp).</summary>
        Skipped,
        /// <summary>A structural issue (unbalanced groups, disabled mask dropped, unreadable data).</summary>
        Structural,
    }

    /// <summary>One categorised warning line.</summary>
    public sealed class Entry
    {
        public Severity Kind { get; init; }
        public string Layer { get; init; } = "";
        public string Message { get; init; } = "";
    }

    /// <summary>The document name the report is for.</summary>
    public string DocumentName { get; init; } = "";
    /// <summary>PostScript font names the PSD referenced.</summary>
    public List<string> Fonts { get; init; } = new();
    /// <summary>Font names not found installed at import time (filled by the app).</summary>
    public List<string> MissingFonts { get; init; } = new();
    /// <summary>Categorised warning entries.</summary>
    public List<Entry> Entries { get; init; } = new();

    public bool HasIssues => Entries.Count > 0 || MissingFonts.Count > 0;

    public int Count(Severity s) => Entries.Count(e => e.Kind == s);

    /// <summary>Build a report from the raw PSD importer output.</summary>
    public static CompatibilityReport Build(string docName, List<string> warnings, List<string> fonts)
    {
        var rep = new CompatibilityReport
        {
            DocumentName = docName,
            Fonts = fonts.ToList(),
        };

        foreach (var w in warnings)
            rep.Entries.Add(Classify(w));
        return rep;
    }

    /// <summary>Classify a single raw warning string into a severity + layer + message.
    /// The importer emits lines like <c>"LayerName: note."</c> or bare lines like
    /// <c>"16-bit document converted to 8-bit."</c>.</summary>
    private static Entry Classify(string warning)
    {
        (string layer, string msg) = SplitLayer(warning);
        Severity kind = msg switch
        {
            _ when msg.Contains("rasterised") || msg.Contains("rasterized") => Severity.Rasterised,
            _ when msg.Contains("converted to 8-bit") || msg.Contains("mapped to Normal")
                    || msg.Contains("flattened to 2") || msg.Contains("flattened to first")
                    || msg.Contains("anchor drift") => Severity.Partial,
            _ when msg.Contains("skipped") || msg.Contains("not imported") || msg.Contains("dropped") => Severity.Skipped,
            _ when msg.Contains("Unbalanced") || msg.Contains("unreadable") || msg.Contains("empty") => Severity.Structural,
            _ => Severity.Partial,
        };
        return new Entry { Kind = kind, Layer = layer, Message = msg };
    }

    /// <summary>Split <c>"Layer": note.</c> into (layer, note). Bare warnings → ("", whole).</summary>
    private static (string layer, string msg) SplitLayer(string w)
    {
        if (w.Length > 2 && w[0] == '"' )
        {
            int close = w.IndexOf('"', 1);
            if (close > 0 && close + 2 < w.Length && w.AsSpan(close + 1, 2).SequenceEqual(": "))
                return (w[1..close], w[(close + 3)..]);
        }
        return ("", w);
    }
}
