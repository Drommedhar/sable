namespace Sable.Plugin.Sdk.Document;

/// <summary>
/// Read-only snapshot of the active document (capability <c>document.read</c>). A plain
/// value record decoupled from the engine's <c>Document</c> — the host fills it on request.
/// Coordinates/sizes are in document pixels.
/// </summary>
public sealed record DocumentInfo
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required double Dpi { get; init; }

    /// <summary>Bit depth label, e.g. "8" or "16".</summary>
    public required string Depth { get; init; }

    public required int LayerCount { get; init; }

    /// <summary>Embedded ICC profile name, or null when none.</summary>
    public string? IccProfileName { get; init; }

    public bool HasSelection { get; init; }

    /// <summary>Selection bounding box in doc px (valid only when <see cref="HasSelection"/>).</summary>
    public int SelectionX { get; init; }
    public int SelectionY { get; init; }
    public int SelectionWidth { get; init; }
    public int SelectionHeight { get; init; }
}
