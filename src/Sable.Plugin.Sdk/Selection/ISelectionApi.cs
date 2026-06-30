namespace Sable.Plugin.Sdk.Selection;

/// <summary>
/// Read-only snapshot of the active document's selection (capability <c>selection.read</c>).
/// Coordinates are document pixels. <see cref="Mask"/> is a doc-sized coverage buffer
/// (1 byte/pixel, 255 = fully selected, 0 = outside), or null when the selection is a plain
/// rectangle / there is none.
/// </summary>
public sealed record SelectionInfo
{
    public required bool HasSelection { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>Doc-sized coverage mask (row-major, length = docW*docH), or null.</summary>
    public byte[]? Mask { get; init; }
}

/// <summary>Read access to the active selection (capability <c>selection.read</c>). Null on
/// <see cref="Host.IHostContext.Selection"/> when not granted.</summary>
public interface ISelectionApi
{
    /// <summary>Current selection snapshot, or null when no document is open.</summary>
    SelectionInfo? Current { get; }
}
