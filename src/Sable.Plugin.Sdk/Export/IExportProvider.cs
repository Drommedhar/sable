namespace Sable.Plugin.Sdk.Export;

/// <summary>Flattened composite handed to an export provider. RGBA8, straight alpha, row-major.</summary>
public sealed record ExportImage
{
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>RGBA8 bytes, length = Width*Height*4.</summary>
    public required byte[] Rgba { get; init; }
}

/// <summary>Per-export settings + cooperative cancellation/progress.</summary>
public sealed record ExportOptions
{
    /// <summary>0..100 quality hint for lossy formats; ignored otherwise.</summary>
    public int Quality { get; init; } = 90;

    /// <summary>ICC profile bytes to embed, or null.</summary>
    public byte[]? IccProfile { get; init; }
    public string? IccProfileName { get; init; }

    public IProgress<double>? Progress { get; init; }
    public CancellationToken Cancellation { get; init; }
}

/// <summary>
/// A file-format exporter a plugin contributes (capability <c>export.provider</c>,
/// PLUGIN_SDK_PLAN.md §29 example). The host shows it in the export UI and calls
/// <see cref="Encode"/> with the flattened composite. Implementations must honour
/// <see cref="ExportOptions.Cancellation"/>.
/// </summary>
public interface IExportProvider
{
    /// <summary>Stable id, unique within the plugin.</summary>
    string Id { get; }

    /// <summary>Human label for the format combo, e.g. "OpenEXR".</summary>
    string Label { get; }

    /// <summary>File extension without dot, e.g. "exr".</summary>
    string Extension { get; }

    bool SupportsAlpha { get; }

    /// <summary>Encode the image to the format's byte stream.</summary>
    byte[] Encode(ExportImage image, ExportOptions options);
}

/// <summary>Export-provider registration surface. Null when <c>export.provider</c> not granted.</summary>
public interface IExportApi
{
    void Register(IExportProvider provider);
}
