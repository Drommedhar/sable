using System.Collections.Generic;

namespace Sable.Plugin.Sdk.Import;

/// <summary>An image a plugin decoded from a file (RGBA8, straight alpha, row-major).</summary>
public sealed record ImportImage
{
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>RGBA8 bytes, length = Width*Height*4.</summary>
    public required byte[] Rgba { get; init; }
}

/// <summary>
/// A file-format importer a plugin contributes (capability <c>import.provider</c>). The host shows
/// the format's extensions in the Open dialog and, for a matching file, reads its bytes and calls
/// <see cref="Decode"/> to get the pixels, which it opens as a new single-layer document.
/// </summary>
public interface IImportProvider
{
    /// <summary>Stable id, unique within the plugin.</summary>
    string Id { get; }

    /// <summary>Human label for the Open dialog, e.g. "OpenEXR".</summary>
    string Label { get; }

    /// <summary>File extensions handled, lowercase and without the dot (e.g. "exr", "hdr").</summary>
    IReadOnlyList<string> Extensions { get; }

    /// <summary>Decode the file's bytes to an RGBA8 image. Throw on a malformed file.</summary>
    ImportImage Decode(byte[] data);
}

/// <summary>Import-provider registration surface. Null when <c>import.provider</c> not granted.</summary>
public interface IImportApi
{
    void Register(IImportProvider provider);
}
